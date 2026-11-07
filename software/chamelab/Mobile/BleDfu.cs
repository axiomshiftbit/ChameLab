using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using ChameLab.Protocol;
using ChameLab.Models;
using ChameLab.Transport;
using Plugin.BLE;
using Plugin.BLE.Abstractions;
using Plugin.BLE.Abstractions.Contracts;
using Plugin.BLE.Abstractions.EventArgs;
using ChameLab.Localization;

namespace ChameLab.Mobile;

/// <summary>The nRF5 SDK bootloader's Secure DFU service.</summary>
internal static class SecureDfuService
{
    public static readonly Guid Service = new("0000FE59-0000-1000-8000-00805F9B34FB");
    /// <summary>Requests (write with response) and their responses (notify).</summary>
    public static readonly Guid ControlPoint = new("8EC90001-F315-4F60-9FB8-838830DAEA50");
    /// <summary>Image data (write without response).</summary>
    public static readonly Guid Packet = new("8EC90002-F315-4F60-9FB8-838830DAEA50");
}

/// <summary>
/// Finds the Chameleon's bootloader over BLE. After <c>ENTER_BOOTLOADER</c> it advertises
/// the Secure DFU service as "CU-xxxx" / "CL-xxxx" from the application's address + 1, and
/// puts the application's address in its manufacturer data; either pins down the right
/// device using the firmware address even when the OS hides addresses (iOS).
/// </summary>
public sealed class BleDfuLocator : IDfuLocator
{
    public TransportKind Kind => TransportKind.Ble;

    public IDfuTarget Prepare(TransportCandidate device, DeviceInfo info)
    {
        // The firmware reports its BLE address even on iOS, where peripheral ids are opaque.
        if (info.Address.Length != 12 || !info.Address.All(Uri.IsHexDigit))
            throw new InvalidOperationException(Strings.Firmware_WrongDevice);
        return new Target(Convert.FromHexString(info.Address));
    }

    /// <summary>
    /// Plugin.BLE on Android builds ids as <c>00000000-0000-0000-0000-{MAC}</c>, most significant
    /// byte first. Elsewhere ids are opaque.
    /// </summary>
    private static byte[]? Address(Guid id)
    {
        var hex = id.ToString("N");
        return hex.StartsWith("00000000000000000000", StringComparison.Ordinal) ? Convert.FromHexString(hex[^12..]) : null;
    }

    private sealed class Target(byte[] app) : IDfuTarget
    {
        // One scan for the whole wait: Android ignores an app's scans after five starts in 30 seconds.
        private static readonly TimeSpan ScanWindow = TimeSpan.FromSeconds(12);

        public async Task<IDfuChannel?> TryOpenAsync(CancellationToken ct)
        {
            var ble = CrossBluetoothLE.Current;
            if (!ble.IsAvailable || !ble.IsOn) throw new InvalidOperationException(Strings.Ble_Off);
            var adapter = ble.Adapter;

            // The adapter runs one scan at a time (a Rescan on the Connect page, say).
            while (adapter.IsScanning) await Task.Delay(250, ct);

            IDevice? ours = null;
            using var window = CancellationTokenSource.CreateLinkedTokenSource(ct);
            void OnDiscovered(object? sender, DeviceEventArgs e)
            {
                if (e.Device is not { } d || !IsBootloader(d)) return;
                // Never fall back to the nearest/first unrelated DFU peripheral.
                if (IsBootloaderOf(d, app))
                {
                    ours ??= d;
                    window.Cancel();
                }
            }

            adapter.DeviceDiscovered += OnDiscovered;
            try
            {
                window.CancelAfter(ScanWindow);
                await adapter.StartScanningForDevicesAsync(
                    new ScanFilterOptions { ServiceUuids = new[] { SecureDfuService.Service } },
                    deviceFilter: IsBootloader,
                    allowDuplicatesKey: false,
                    cancellationToken: window.Token).WaitAsync(window.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { /* found, or the window elapsed */ }
            finally
            {
                try { await adapter.StopScanningForDevicesAsync().WaitAsync(TimeSpan.FromSeconds(2)); } catch { /* ignore */ }
                adapter.DeviceDiscovered -= OnDiscovered;
            }

            ct.ThrowIfCancellationRequested();
            return ours is null ? null : await BleDfuChannel.OpenAsync(adapter, ours, ct);
        }

        private static bool IsBootloader(IDevice d) =>
            d.Name is { } name && (name.StartsWith("CU-", StringComparison.Ordinal) || name.StartsWith("CL-", StringComparison.Ordinal)) ||
            Records(d).Any(r =>
                r.Type is AdvertisementRecordType.UuidsComplete16Bit or AdvertisementRecordType.UuidsIncomplete16Bit &&
                r.Data.Length >= 2 && r.Data[0] == 0x59 && r.Data[1] == 0xFE);

    }

    /// <summary>
    /// The bootloader takes the application's address + 1 (least significant byte, no carry), and
    /// advertises the application's address after company id 0xFFFF, most significant byte first.
    /// </summary>
    internal static bool IsBootloaderOf(IDevice d, byte[] app)
    {
        var own = Address(d.Id);
        if (app.Length != 6) return false;
        if (own is not null && own.AsSpan(0, 5).SequenceEqual(app.AsSpan(0, 5)) && own[5] == (byte)(app[5] + 1)) return true;
        return Records(d).Any(r =>
            r.Type == AdvertisementRecordType.ManufacturerSpecificData &&
            r.Data.Length >= 8 && r.Data[0] == 0xFF && r.Data[1] == 0xFF && r.Data.AsSpan(2, 6).SequenceEqual(app));
    }

    private static IEnumerable<AdvertisementRecord> Records(IDevice d) =>
        d.AdvertisementRecords ?? (IEnumerable<AdvertisementRecord>)Array.Empty<AdvertisementRecord>();

}

/// <summary>Secure DFU over BLE: requests on the control point, image data on the packet characteristic.</summary>
public sealed class BleDfuChannel : IDfuChannel
{
    private static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(10);

    private readonly IAdapter _adapter;
    private readonly IDevice _device;
    private bool _disposed;
    private ICharacteristic _control = null!;
    private ICharacteristic _packet = null!;
    private readonly Channel<byte[]> _responses = Channel.CreateUnbounded<byte[]>();

    private BleDfuChannel(IAdapter adapter, IDevice device)
    {
        _adapter = adapter;
        _device = device;
    }

    public int MaxDataSize { get; private set; } = 20;

    // Below the bootloader's 8 receive buffers (NRF_DFU_BLE_BUFFERS), so data writes never overrun it.
    public int PacketReceiptInterval => 6;

    public static async Task<BleDfuChannel> OpenAsync(IAdapter adapter, IDevice device, CancellationToken ct)
    {
        var channel = new BleDfuChannel(adapter, device);
        try
        {
            // LE explicitly: Android's default transport may try classic Bluetooth on a device it just met (GATT error 133).
            await adapter.ConnectToDeviceAsync(device, new ConnectParameters(autoConnect: false, forceBleTransport: true), ct).WaitAsync(ResponseTimeout, ct);
            adapter.DeviceDisconnected += channel.OnDisconnected;
            adapter.DeviceConnectionLost += channel.OnConnectionLost;

            try
            {
                var mtu = await device.RequestMtuAsync(247, ct).WaitAsync(ResponseTimeout, ct);
                if (mtu > 23) channel.MaxDataSize = mtu - 3; // minus the ATT header
            }
            catch (OperationCanceledException) { throw; }
            catch { /* stay at the default 23-byte MTU */ }

            var service = await device.GetServiceAsync(SecureDfuService.Service, ct).WaitAsync(ResponseTimeout, ct)
                ?? throw new InvalidOperationException(Strings.Dfu_NoService);
            channel._control = await service.GetCharacteristicAsync(SecureDfuService.ControlPoint, ct).WaitAsync(ResponseTimeout, ct)
                ?? throw new InvalidOperationException(Strings.Dfu_NoControlPoint);
            channel._packet = await service.GetCharacteristicAsync(SecureDfuService.Packet, ct).WaitAsync(ResponseTimeout, ct)
                ?? throw new InvalidOperationException(Strings.Dfu_NoPacketChar);
            channel._control.WriteType = CharacteristicWriteType.WithResponse;
            channel._packet.WriteType = CharacteristicWriteType.WithoutResponse;

            channel._control.ValueUpdated += channel.OnControlValue;
            await channel._control.StartUpdatesAsync(ct).WaitAsync(ResponseTimeout, ct);
            return channel;
        }
        catch
        {
            await channel.DisposeAsync();
            throw;
        }
    }

    private void OnControlValue(object? sender, CharacteristicUpdatedEventArgs e)
    {
        if (e.Characteristic.Value is { Length: > 0 } value)
            _responses.Writer.TryWrite(value.ToArray());
    }

    private void OnDisconnected(object? sender, DeviceEventArgs e) => OnDrop(e.Device);
    private void OnConnectionLost(object? sender, DeviceErrorEventArgs e) => OnDrop(e.Device);

    private void OnDrop(IDevice? device)
    {
        if (device?.Id == _device.Id) _responses.Writer.TryComplete();
    }

    public async Task<byte[]> RequestAsync(byte[] request, CancellationToken ct)
    {
        try
        {
            await _control.WriteAsync(request, ct).WaitAsync(ResponseTimeout, ct);
        }
        catch when (_responses.Reader.Completion.IsCompleted)
        {
            throw new DfuLinkClosedException();
        }
        return await ReceiveAsync(ct);
    }

    public async Task<byte[]> ReceiveAsync(CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ResponseTimeout);
        try
        {
            return await _responses.Reader.ReadAsync(timeout.Token);
        }
        catch (ChannelClosedException)
        {
            throw new DfuLinkClosedException();
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException(Strings.Dfu_Stopped);
        }
    }

    public async Task WriteDataAsync(byte[] data, CancellationToken ct)
    {
        try
        {
            await _packet.WriteAsync(data, ct).WaitAsync(ResponseTimeout, ct);
        }
        catch when (_responses.Reader.Completion.IsCompleted)
        {
            throw new DfuLinkClosedException();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _responses.Writer.TryComplete();
        _adapter.DeviceDisconnected -= OnDisconnected;
        _adapter.DeviceConnectionLost -= OnConnectionLost;
        if (_control is not null)
        {
            _control.ValueUpdated -= OnControlValue;
        }
        // Closing GATT removes notifications too; waiting for a CCCD write can hang after reset.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try { await _adapter.DisconnectDeviceAsync(_device, timeout.Token).WaitAsync(timeout.Token); }
        catch { /* the bootloader may have reset already */ }
    }
}

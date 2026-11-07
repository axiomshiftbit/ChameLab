using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ChameLab.Transport;
using ChameLab.Localization;

namespace ChameLab.Protocol;

/// <summary>Raised when the bootloader drops the link; expected right after the last object is executed.</summary>
public sealed class DfuLinkClosedException() : IOException(Strings.Dfu_LinkLost);

/// <summary>
/// How Secure DFU requests and image data reach the bootloader: SLIP frames over
/// USB serial, or the control point / packet characteristics over BLE.
/// </summary>
public interface IDfuChannel : IAsyncDisposable
{
    /// <summary>Largest piece of image data per <see cref="WriteDataAsync"/>.</summary>
    int MaxDataSize { get; }

    /// <summary>
    /// Data packets after which the bootloader confirms what it got, 0 for none. BLE needs it:
    /// data goes out without responses and would otherwise overrun the bootloader's buffers.
    /// </summary>
    int PacketReceiptInterval { get; }

    /// <summary>Sends a request and returns the bootloader's response (starting with 0x60).</summary>
    Task<byte[]> RequestAsync(byte[] request, CancellationToken ct);

    /// <summary>Waits for a response without sending anything (a packet receipt).</summary>
    Task<byte[]> ReceiveAsync(CancellationToken ct);

    Task WriteDataAsync(byte[] data, CancellationToken ct);
}

/// <summary>Nordic Secure DFU (nRF5 SDK bootloader), independent of the transport.</summary>
public sealed class SecureDfu(IDfuChannel channel)
{
    public const byte OpCreate = 0x01, OpSetPrn = 0x02, OpCrc = 0x03, OpExecute = 0x04,
        OpSelect = 0x06, OpMtu = 0x07, OpWrite = 0x08, OpPing = 0x09, OpResponse = 0x60;
    private const byte ObjCommand = 0x01, ObjData = 0x02;

    /// <summary>Uploads a checked application. A retry starts from zero, including after power loss.</summary>
    public async Task FlashAsync(DfuPackage package, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (channel.MaxDataSize is <= 0 or > 65535 || channel.PacketReceiptInterval is < 0 or > 65535)
            throw new IOException(Strings.Dfu_InvalidResponse);
        var prn = (ushort)channel.PacketReceiptInterval;
        await RequestAsync(ct, OpSetPrn, (byte)prn, (byte)(prn >> 8)).ConfigureAwait(false);

        var command = await RequestAsync(ct, OpSelect, ObjCommand).ConfigureAwait(false);
        if (BitConverter.ToUInt32(command, 0) < package.InitPacket.Length)
            throw new IOException(Strings.Dfu_InvalidResponse);
        // CREATE command resets all saved progress in the nRF5 SDK. Do not mix a partial old image
        // with a new package, or assume that RAM progress survived a bootloader reset.
        await RequestAsync(ct, [OpCreate, ObjCommand, .. BitConverter.GetBytes((uint)package.InitPacket.Length)]).ConfigureAwait(false);
        await StreamAsync(package.InitPacket, 0, package.InitPacket.Length, 0, ct).ConfigureAwait(false);
        await RequestAsync(ct, OpExecute).ConfigureAwait(false); // device verifies the signed init packet here

        var fw = package.Firmware;
        var selected = await RequestAsync(ct, OpSelect, ObjData).ConfigureAwait(false);
        var maxObject = BitConverter.ToUInt32(selected, 0);
        if (maxObject is 0 or > 1024 * 1024 || BitConverter.ToUInt32(selected, 4) != 0 || BitConverter.ToUInt32(selected, 8) != 0)
            throw new IOException(Strings.Dfu_InvalidResponse);
        uint crc = 0;
        for (var offset = 0; offset < fw.Length;)
        {
            var length = Math.Min((int)maxObject, fw.Length - offset);
            await RequestAsync(ct, [OpCreate, ObjData, .. BitConverter.GetBytes((uint)length)]).ConfigureAwait(false);
            crc = await StreamAsync(fw, offset, length, crc, ct).ConfigureAwait(false);
            var last = offset + length == fw.Length;
            try
            {
                await RequestAsync(ct, OpExecute).ConfigureAwait(false);
            }
            // A reset may hide the final reply. The service MUST confirm the original device
            // and expected version after reconnecting before presenting a successful update.
            catch (Exception ex) when (last && !ct.IsCancellationRequested && ex is DfuLinkClosedException or TimeoutException) { }
            offset += length;
            progress?.Report((double)offset / fw.Length);
        }
    }

    private async Task<uint> StreamAsync(ReadOnlyMemory<byte> data, int offset, int length, uint crc, CancellationToken ct)
    {
        var receipts = 0;
        for (var i = 0; i < length;)
        {
            ct.ThrowIfCancellationRequested();
            var n = Math.Min(channel.MaxDataSize, length - i);
            var chunk = data.Slice(offset + i, n);
            await channel.WriteDataAsync(chunk.ToArray(), ct).ConfigureAwait(false);
            crc = Crc32(chunk.Span, crc);
            i += n;
            if (channel.PacketReceiptInterval > 0 && ++receipts == channel.PacketReceiptInterval)
            {
                receipts = 0;
                CheckCrc(Check(await channel.ReceiveAsync(ct).ConfigureAwait(false), OpCrc), offset + i, crc);
            }
        }
        CheckCrc(await RequestAsync(ct, OpCrc).ConfigureAwait(false), offset + length, crc);
        return crc;
    }

    private static void CheckCrc(byte[] payload, int offset, uint crc)
    {
        uint deviceOffset = BitConverter.ToUInt32(payload, 0), deviceCrc = BitConverter.ToUInt32(payload, 4);
        if (deviceOffset != offset || deviceCrc != crc)
            throw new IOException(string.Format(Strings.Dfu_Corrupted, deviceOffset, deviceCrc, offset, crc));
    }

    private async Task<byte[]> RequestAsync(CancellationToken ct, params byte[] request)
    {
        ct.ThrowIfCancellationRequested();
        return Check(await channel.RequestAsync(request, ct).ConfigureAwait(false), request[0]);
    }

    /// <summary>Validates a response to <paramref name="op"/> and returns its payload.</summary>
    public static byte[] Check(byte[] r, byte op)
    {
        if (r.Length < 3 || r[0] != OpResponse || r[1] != op)
            throw new IOException(string.Format(Strings.Dfu_Unexpected, Convert.ToHexString(r)));
        if (r[2] != 0x01)
            throw new IOException(string.Format(Strings.Dfu_Rejected, op, r[2], (r.Length > 3 ? $"/0x{r[3]:X2}" : "")));
        var size = op switch { OpSelect => 12, OpCrc => 8, OpMtu => 2, OpPing => 1, _ => 0 };
        if (r.Length != 3 + size) throw new IOException(Strings.Dfu_InvalidResponse);
        return r[3..];
    }

    // Incremental: each firmware byte is hashed once, rather than rehashing the prefix for every object.
    internal static uint Crc32(ReadOnlySpan<byte> data, uint previous = 0)
    {
        var crc = ~previous;
        foreach (var value in data)
        {
            crc ^= value;
            for (var k = 0; k < 8; k++) crc = (crc >> 1) ^ (0xEDB88320u & (uint)-(int)(crc & 1));
        }
        return ~crc;
    }
}

/// <summary>Secure DFU over the bootloader's USB serial port: SLIP frames, as nrfutil sends them.</summary>
public sealed class SerialDfuChannel : IDfuChannel
{
    private const byte SlipEnd = 0xC0, SlipEsc = 0xDB, SlipEscEnd = 0xDC, SlipEscEsc = 0xDD;
    private static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(10);

    private readonly ITransport _transport;
    private readonly byte[] _readBuffer = new byte[256];
    private int _readPos, _readLen;
    private Task? _disposeTask;

    private SerialDfuChannel(ITransport transport) => _transport = transport;

    public int MaxDataSize { get; private set; }

    // Every request waits for its answer, which keeps serial in step without receipts.
    public int PacketReceiptInterval => 0;

    /// <summary>Opens <paramref name="transport"/> and asks the bootloader for its frame size.</summary>
    public static async Task<SerialDfuChannel> OpenAsync(ITransport transport, CancellationToken ct)
    {
        var channel = new SerialDfuChannel(transport);
        try
        {
            await transport.ConnectAsync(ct).ConfigureAwait(false);
            await Task.Delay(200, ct).ConfigureAwait(false);
            var ping = SecureDfu.Check(await channel.RequestAsync([SecureDfu.OpPing, 0x01], ct).ConfigureAwait(false), SecureDfu.OpPing);
            if (ping[0] != 0x01) throw new IOException(Strings.Dfu_InvalidResponse);
            var mtu = BitConverter.ToUInt16(SecureDfu.Check(await channel.RequestAsync([SecureDfu.OpMtu], ct), SecureDfu.OpMtu), 0);
            // Every byte may need SLIP escaping, and the opcode rides along.
            channel.MaxDataSize = (mtu - 1) / 2 - 1;
            if (channel.MaxDataSize <= 0) throw new IOException(Strings.Dfu_InvalidResponse);
            return channel;
        }
        catch
        {
            await channel.DisposeAsync();
            throw;
        }
    }

    public async Task<byte[]> RequestAsync(byte[] request, CancellationToken ct)
    {
        await SendAsync(request, ct);
        return await ReceiveAsync(ct);
    }

    public Task WriteDataAsync(byte[] data, CancellationToken ct) => SendAsync([SecureDfu.OpWrite, .. data], ct);

    private async Task SendAsync(byte[] payload, CancellationToken ct)
    {
        var o = new List<byte>(payload.Length * 2 + 1);
        foreach (var b in payload)
        {
            if (b == SlipEnd) { o.Add(SlipEsc); o.Add(SlipEscEnd); }
            else if (b == SlipEsc) { o.Add(SlipEsc); o.Add(SlipEscEsc); }
            else o.Add(b);
        }
        o.Add(SlipEnd);
        try
        {
            await _transport.WriteAsync(o.ToArray(), ct).WaitAsync(ResponseTimeout, ct).ConfigureAwait(false);
        }
        catch
        {
            await DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<byte[]> ReceiveAsync(CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(ResponseTimeout);
        var frame = new List<byte>();
        var escaped = false;
        try
        {
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (_readPos == _readLen)
                {
                    // SerialPort's native read can ignore cancellation. WaitAsync bounds the wait;
                    // closing this channel then releases the native read before any retry uses it.
                    try { _readLen = await _transport.ReadAsync(_readBuffer, deadline.Token).WaitAsync(deadline.Token).ConfigureAwait(false); }
                    catch (IOException) { throw new DfuLinkClosedException(); }
                    _readPos = 0;
                    continue;
                }
                var b = _readBuffer[_readPos++];
                if (escaped)
                {
                    if (b is not SlipEscEnd and not SlipEscEsc) throw new IOException(Strings.Dfu_InvalidResponse);
                    frame.Add(b == SlipEscEnd ? SlipEnd : SlipEsc);
                    escaped = false;
                }
                else if (b == SlipEnd)
                {
                    if (frame.Count > 0) return frame.ToArray();
                }
                else if (b == SlipEsc) escaped = true;
                else frame.Add(b);
                if (frame.Count > 64) throw new IOException(Strings.Dfu_InvalidResponse);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            await DisposeAsync().ConfigureAwait(false);
            throw new TimeoutException(Strings.Dfu_Stopped);
        }
        catch
        {
            await DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _disposeTask ??= CloseTransportAsync();
        try { await _disposeTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
        catch (TimeoutException) { /* native close continues; this channel is never reused */ }
    }

    private async Task CloseTransportAsync()
    {
        try { await _transport.DisposeAsync().ConfigureAwait(false); }
        catch { /* preserve the original transfer error; native close can race a USB reset */ }
    }
}

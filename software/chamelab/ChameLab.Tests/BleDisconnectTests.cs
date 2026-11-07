using System;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using ChameLab.Mobile;
using Plugin.BLE.Abstractions.Contracts;
using Plugin.BLE.Abstractions.EventArgs;
using Xunit;

namespace ChameLab.Tests;

public class BleDisconnectTests
{
    [Fact]
    public async Task LateDropFromOldDeviceObjectDoesNotCloseNewSessionWithSameId()
    {
        var id = Guid.NewGuid();
        var characteristic = Fake<ICharacteristic>((name, _) => name == "StartUpdatesAsync" ? Task.CompletedTask : null);
        var service = Fake<IService>((name, _) => name == "GetCharacteristicAsync" ? Task.FromResult(characteristic) : null);
        IDevice Device() => Fake<IDevice>((name, _) => name switch
        {
            "get_Id" => id, "RequestMtuAsync" => Task.FromResult(247),
            "GetServiceAsync" => Task.FromResult(service), "UpdateConnectionInterval" => true, _ => null,
        });
        var old = Device(); var current = Device();
        EventHandler<DeviceEventArgs>? disconnected = null;
        var released = false;
        var adapter = Fake<IAdapter>((name, args) =>
        {
            switch (name)
            {
                case "get_ConnectedDevices": return new[] { old };
                case "ConnectToKnownDeviceAsync": Assert.True(released); return Task.FromResult(current);
                case "DisconnectDeviceAsync": return Task.CompletedTask;
                case "add_DeviceDisconnected": disconnected += (EventHandler<DeviceEventArgs>)args[0]!; break;
                case "remove_DeviceDisconnected": disconnected -= (EventHandler<DeviceEventArgs>)args[0]!; break;
            }
            return null;
        });
        var transport = new BleTransport(id, "Synthetic BLE", adapter, _ => released = true);
        await transport.ConnectAsync();
        try
        {
            var read = transport.ReadAsync(new byte[20]);
            disconnected!(adapter, new DeviceEventArgs { Device = old });
            Assert.False(read.IsCompleted);
            disconnected!(adapter, new DeviceEventArgs { Device = current });
            await Assert.ThrowsAsync<InvalidOperationException>(() => read.WaitAsync(TimeSpan.FromSeconds(1)));
        }
        finally { await transport.DisposeAsync(); }
    }

    [Theory]
    [InlineData(-1)] // native callback never arrives and ignores cancellation
    [InlineData(5)] // GATT rejects the write
    public async Task NativeWriteFailureOrMissingCallbackDoesNotHangCommand(int status)
    {
        var characteristic = Fake<ICharacteristic>((name, _) => name switch
        {
            "StartUpdatesAsync" => Task.CompletedTask,
            "WriteAsync" => status < 0 ? new TaskCompletionSource<int>().Task : Task.FromResult(status),
            _ => null,
        });
        var service = Fake<IService>((name, _) => name == "GetCharacteristicAsync" ? Task.FromResult(characteristic) : null);
        var device = Fake<IDevice>((name, _) => name switch
        {
            "RequestMtuAsync" => Task.FromResult(247), "GetServiceAsync" => Task.FromResult(service),
            "UpdateConnectionInterval" => true, _ => null,
        });
        var adapter = Fake<IAdapter>((name, _) => name switch
        {
            "ConnectToKnownDeviceAsync" => Task.FromResult(device),
            "DisconnectDeviceAsync" => Task.CompletedTask, _ => null,
        });
        var transport = new BleTransport(Guid.NewGuid(), "Synthetic BLE", adapter);
        await transport.ConnectAsync();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            var write = transport.WriteAsync(new byte[20], timeout.Token).WaitAsync(TimeSpan.FromSeconds(2));
            if (status < 0) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => write);
            else
            {
                await Assert.ThrowsAsync<IOException>(() => write);
                await Assert.ThrowsAsync<InvalidOperationException>(() => transport.ReadAsync(new byte[20]));
            }
        }
        finally { await transport.DisposeAsync(); }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task DisconnectSkipsCccdAndBoundsMissingNativeCallback(bool missingCallback, bool ignoresCancellation)
    {
        var stopUpdates = false;
        var disconnects = 0;
        var released = 0;
        var characteristic = Fake<ICharacteristic>((name, args) => name switch
        {
            "StartUpdatesAsync" => Task.CompletedTask,
            "StopUpdatesAsync" => Stall(),
            _ => null,
        });
        Task Stall() { stopUpdates = true; return Task.Delay(Timeout.Infinite); }
        var service = Fake<IService>((name, args) => name == "GetCharacteristicAsync"
            ? Task.FromResult(characteristic) : null);
        var device = Fake<IDevice>((name, args) => name switch
        {
            "RequestMtuAsync" => Task.FromResult(247),
            "GetServiceAsync" => Task.FromResult(service),
            "UpdateConnectionInterval" => true,
            _ => null,
        });
        var adapter = Fake<IAdapter>((name, args) => name switch
        {
            "ConnectToKnownDeviceAsync" => Task.FromResult(device),
            "DisconnectDeviceAsync" => Disconnect((CancellationToken)args[1]!),
            _ => null,
        });
        Task Disconnect(CancellationToken ct)
        {
            disconnects++;
            Assert.True(ct.CanBeCanceled);
            return missingCallback
                ? Task.Delay(Timeout.Infinite, ignoresCancellation ? CancellationToken.None : ct)
                : Task.CompletedTask;
        }
        var transport = new BleTransport(Guid.NewGuid(), "Synthetic BLE", adapter, d => { Assert.Same(device, d); released++; });
        await transport.ConnectAsync();
        var read = transport.ReadAsync(new byte[20]);
        var closing = transport.DisposeAsync().AsTask();
        // Two seconds is a fallback for missing callbacks, not a mandatory delay.
        if (!missingCallback) Assert.True(closing.IsCompletedSuccessfully);
        await Assert.ThrowsAsync<InvalidOperationException>(() => read.WaitAsync(TimeSpan.FromSeconds(1)));
        await closing.WaitAsync(TimeSpan.FromSeconds(4));
        Assert.False(stopUpdates);
        Assert.Equal(1, released);
        Assert.Equal(1, disconnects);
        await transport.DisposeAsync();
        Assert.Equal(1, disconnects);
        Assert.Equal(1, released);
    }

    [Theory]
    [InlineData("00000000-0000-0000-0000-c00102030406", true)]
    [InlineData("00000000-0000-0000-0000-c00102030409", false)]
    [InlineData("11111111-2222-3333-4444-555555555555", false)]
    public void DfuNeverMatchesAnUnrelatedAddress(string id, bool expected)
    {
        var device = Fake<IDevice>((name, args) => name == "get_Id" ? Guid.Parse(id) : null);
        Assert.Equal(expected, BleDfuLocator.IsBootloaderOf(device, Convert.FromHexString("C00102030405")));
    }

    [Fact]
    public async Task DfuCloseDoesNotWaitForNotificationShutdown()
    {
        var stopUpdates = false;
        var characteristic = Fake<ICharacteristic>((name, args) => name switch
        {
            "StartUpdatesAsync" => Task.CompletedTask,
            "StopUpdatesAsync" => Stall(), _ => null,
        });
        Task Stall() { stopUpdates = true; return Task.Delay(Timeout.Infinite); }
        var service = Fake<IService>((name, args) => name == "GetCharacteristicAsync" ? Task.FromResult(characteristic) : null);
        var device = Fake<IDevice>((name, args) => name switch
        {
            "RequestMtuAsync" => Task.FromResult(247), "GetServiceAsync" => Task.FromResult(service), _ => null,
        });
        var adapter = Fake<IAdapter>((name, args) => name switch
        {
            "ConnectToDeviceAsync" => Task.CompletedTask,
            "DisconnectDeviceAsync" => Task.Delay(Timeout.Infinite, (CancellationToken)args[1]!), _ => null,
        });
        var channel = await BleDfuChannel.OpenAsync(adapter, device, default);
        var read = channel.ReceiveAsync(default);
        await channel.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(4));
        await Assert.ThrowsAsync<ChameLab.Protocol.DfuLinkClosedException>(() => read);
        Assert.False(stopUpdates);
    }

    private static T Fake<T>(Func<string, object?[], object?> call) where T : class
    {
        var result = DispatchProxy.Create<T, Proxy>();
        ((Proxy)(object)result).Call = call;
        return result;
    }

    public class Proxy : DispatchProxy
    {
        public Func<string, object?[], object?> Call = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args) => Call(method!.Name, args ?? []);
    }
}

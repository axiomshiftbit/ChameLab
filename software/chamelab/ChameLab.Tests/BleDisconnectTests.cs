using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using ChameLab.Mobile;
using Plugin.BLE.Abstractions.Contracts;
using Xunit;

namespace ChameLab.Tests;

public class BleDisconnectTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task DisconnectSkipsCccdAndBoundsMissingNativeCallback(bool missingCallback, bool ignoresCancellation)
    {
        var stopUpdates = false;
        var disconnects = 0;
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
        var transport = new BleTransport(Guid.NewGuid(), "Synthetic BLE", adapter);
        await transport.ConnectAsync();
        var read = transport.ReadAsync(new byte[20]);
        var closing = transport.DisposeAsync().AsTask();
        await Assert.ThrowsAsync<InvalidOperationException>(() => read.WaitAsync(TimeSpan.FromSeconds(1)));
        await closing.WaitAsync(TimeSpan.FromSeconds(4));
        Assert.False(stopUpdates);
        Assert.Equal(1, disconnects);
        await transport.DisposeAsync();
        Assert.Equal(1, disconnects);
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

using System;
using System.Threading;
using System.Threading.Tasks;
using ChameLab.Protocol;
using ChameLab.Transport;
using Xunit;

namespace ChameLab.Tests;

public class DisconnectResponsivenessTests
{
    private sealed class SlowCloseTransport : ITransport
    {
        public TaskCompletionSource Written { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Close { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TransportKind Kind => TransportKind.Ble;
        public string DisplayName => "Synthetic BLE";
        public bool IsConnected => true;
        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
        { Written.TrySetResult(); return Task.CompletedTask; }
        public async Task<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        { await Task.Delay(Timeout.Infinite, ct); return 0; }
        public ValueTask DisposeAsync() => new(Close.Task);
    }

    [Fact]
    public async Task PendingCommandFailsBeforeSlowTransportCleanupCompletes()
    {
        var transport = new SlowCloseTransport();
        var connection = new ChameleonConnection(transport);
        await connection.OpenAsync();
        var pending = connection.SendAsync(Command.GetAppVersion, timeoutMs: 30000);
        await transport.Written.Task;
        var closing = connection.DisposeAsync().AsTask();
        try
        {
            await Assert.ThrowsAsync<ChameleonDisconnectedException>(() => pending.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.False(closing.IsCompleted);
        }
        finally { transport.Close.TrySetResult(); await closing; }
    }
}

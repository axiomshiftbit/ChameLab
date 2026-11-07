using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using ChameLab.Protocol;
using ChameLab.Transport;
using Xunit;

namespace ChameLab.Tests;

public class SerialDfuTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4)]
    public async Task InvalidMtuClosesPort(int mtu)
    {
        var transport = new SerialFake { Mtu = mtu };
        await Assert.ThrowsAsync<IOException>(() => SerialDfuChannel.OpenAsync(transport, default));
        Assert.True(transport.Disposed);
    }

    [Fact]
    public async Task CancelDuringInitialDelayClosesPort()
    {
        var transport = new SerialFake();
        using var cancel = new CancellationTokenSource(30);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SerialDfuChannel.OpenAsync(transport, cancel.Token));
        Assert.True(transport.Disposed);
    }

    [Fact]
    public async Task CancellationReleasesReadEvenWhenNativeReadIgnoresToken()
    {
        var transport = new SerialFake();
        await using var channel = await SerialDfuChannel.OpenAsync(transport, default);
        using var cancel = new CancellationTokenSource(30);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => channel.ReceiveAsync(cancel.Token).WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(transport.Disposed);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InvalidOrUnboundedSlipFrameIsRejected(bool badEscape)
    {
        var transport = new SerialFake();
        await using var channel = await SerialDfuChannel.OpenAsync(transport, default);
        transport.Frames.Writer.TryWrite(badEscape ? [0xDB, 0x42, 0xC0] : new byte[100]);
        await Assert.ThrowsAsync<IOException>(() => channel.ReceiveAsync(default));
        Assert.True(transport.Disposed);
    }

    private sealed class SerialFake : ITransport
    {
        public int Mtu = 256;
        public bool Disposed;
        public Channel<byte[]> Frames = Channel.CreateUnbounded<byte[]>();
        public TransportKind Kind => TransportKind.Serial;
        public string DisplayName => "Synthetic DFU";
        public bool IsConnected => !Disposed;
        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct = default)
        {
            var command = bytes.Span[0];
            byte[] payload = command == SecureDfu.OpPing ? [1] : [(byte)Mtu, (byte)(Mtu >> 8)];
            Frames.Writer.TryWrite([0x60, command, 1, .. payload, 0xC0]);
            return Task.CompletedTask;
        }
        public async Task<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            var bytes = await Frames.Reader.ReadAsync(); // intentionally ignores cancellation
            bytes.CopyTo(buffer); return bytes.Length;
        }
        public ValueTask DisposeAsync() { Disposed = true; Frames.Writer.TryComplete(); return ValueTask.CompletedTask; }
    }
}

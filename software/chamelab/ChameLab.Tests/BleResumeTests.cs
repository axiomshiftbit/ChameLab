using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using ChameLab.Protocol;
using ChameLab.Services;
using ChameLab.Transport;
using Xunit;

namespace ChameLab.Tests;

public class BleResumeTests
{
    [Fact]
    public async Task ResponsiveSessionSurvivesResumeWithoutReconnecting()
    {
        var scanner = new Scanner(); var device = new DeviceService([scanner]);
        await device.ConnectAsync(scanner.Candidate);
        var client = device.Client;
        try
        {
            await device.ResumeBleAsync(scanner.Candidate);
            Assert.Same(client, device.Client);
            Assert.Single(scanner.Links);
            Assert.Equal(2, scanner.Links[0].Commands.Count(c => c == Command.GetAppVersion));
        }
        finally { await device.DisconnectAsync(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReopensOnlyTheSameDeviceAfterSilentOrReportedLoss(bool reported)
    {
        var scanner = new Scanner(); var device = new DeviceService([scanner]);
        await device.ConnectAsync(scanner.Candidate);
        var first = scanner.Links[0];
        if (reported)
        {
            var dropped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            device.StateChanged += s => { if (s == ConnectionState.Disconnected) dropped.TrySetResult(); };
            first.Drop();
            await dropped.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        else first.Hold = Command.GetAppVersion;
        try
        {
            await device.ResumeBleAsync(scanner.Candidate).WaitAsync(TimeSpan.FromSeconds(8));
            Assert.Equal(ConnectionState.Connected, device.State);
            Assert.Equal(scanner.Candidate, device.Candidate);
            Assert.Equal(2, scanner.Links.Count);
            Assert.Equal(1, first.Disposed);
            Assert.True(first.ReaderStopped.Task.IsCompleted);
        }
        finally { await device.DisconnectAsync(); }
    }

    [Fact]
    public async Task ManualDisconnectDuringProbeWinsOverAutomaticRecovery()
    {
        var scanner = new Scanner(); var device = new DeviceService([scanner]);
        await device.ConnectAsync(scanner.Candidate);
        scanner.Links[0].Hold = Command.GetAppVersion;
        var resume = device.ResumeBleAsync(scanner.Candidate);
        await scanner.Links[0].Held.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await device.DisconnectAsync();
        await resume;
        Assert.Equal(ConnectionState.Disconnected, device.State);
        Assert.Single(scanner.Links);
    }

    [Fact]
    public async Task SelectingAnotherDeviceDuringProbeDoesNotRestoreTheOldOne()
    {
        var scanner = new Scanner(); var device = new DeviceService([scanner]);
        await device.ConnectAsync(scanner.Candidate);
        scanner.Links[0].Hold = Command.GetAppVersion;
        var resume = device.ResumeBleAsync(scanner.Candidate);
        await scanner.Links[0].Held.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var other = scanner.Candidate with { Id = "another-synthetic-device" };
        try
        {
            await device.ConnectAsync(other);
            await resume;
            Assert.Equal(other, device.Candidate);
            Assert.Equal(2, scanner.Links.Count);
        }
        finally { await device.DisconnectAsync(); }
    }

    [Fact]
    public async Task ResumeDoesNotProbeThroughOrReplayAnActiveCardWrite()
    {
        var scanner = new Scanner(); var device = new DeviceService([scanner]);
        await device.ConnectAsync(scanner.Candidate);
        var link = scanner.Links[0];
        link.Hold = Command.Mf1WriteEmuBlockData;
        var write = device.Client!.Connection.SendAsync(Command.Mf1WriteEmuBlockData, new byte[17]);
        await link.Held.Task.WaitAsync(TimeSpan.FromSeconds(2));
        try
        {
            await device.ResumeBleAsync(scanner.Candidate);
            Assert.Single(scanner.Links);
            Assert.Equal(1, link.Commands.Count(c => c == Command.GetAppVersion));
            Assert.Equal(1, link.Commands.Count(c => c == Command.Mf1WriteEmuBlockData));
            link.Reply(Command.Mf1WriteEmuBlockData, []);
            await write;
        }
        finally { await device.DisconnectAsync(); }
    }

    [Fact]
    public async Task CancellationDuringResumeDoesNotStartAnotherConnection()
    {
        var scanner = new Scanner(); var device = new DeviceService([scanner]);
        await device.ConnectAsync(scanner.Candidate);
        scanner.Links[0].Hold = Command.GetAppVersion;
        using var cancel = new CancellationTokenSource();
        var resume = device.ResumeBleAsync(scanner.Candidate, cancel.Token);
        await scanner.Links[0].Held.Task.WaitAsync(TimeSpan.FromSeconds(2));
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => resume);
        Assert.Single(scanner.Links);
        await device.DisconnectAsync();
    }

    [Fact]
    public async Task CardWriteStartedDuringProbeIsNotInterruptedByRecovery()
    {
        var scanner = new Scanner(); var device = new DeviceService([scanner]);
        await device.ConnectAsync(scanner.Candidate);
        var link = scanner.Links[0];
        link.Hold = Command.GetAppVersion;
        var resume = device.ResumeBleAsync(scanner.Candidate);
        await link.Held.Task.WaitAsync(TimeSpan.FromSeconds(2));
        link.Hold = Command.Mf1WriteEmuBlockData;
        var write = device.Client!.Connection.SendAsync(Command.Mf1WriteEmuBlockData, new byte[17], timeoutMs: 10000);
        try
        {
            await resume.WaitAsync(TimeSpan.FromSeconds(6));
            Assert.Single(scanner.Links);
            Assert.False(write.IsCompleted);
            link.Reply(Command.Mf1WriteEmuBlockData, []);
            await write;
        }
        finally { await device.DisconnectAsync(); }
    }

    [Fact]
    public async Task DisconnectCancelsHandshakeAndStopsItsReader()
    {
        var scanner = new Scanner { HoldFirstHandshake = true }; var device = new DeviceService([scanner]);
        var opening = device.ConnectAsync(scanner.Candidate);
        await scanner.Links[0].Held.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await device.DisconnectAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => opening);
        Assert.Equal(ConnectionState.Disconnected, device.State);
        Assert.Null(device.Client);
        Assert.Equal(1, scanner.Links[0].Disposed);
        Assert.True(scanner.Links[0].ReaderStopped.Task.IsCompleted);
    }

    private sealed class Scanner : ITransportScanner
    {
        public List<Link> Links { get; } = [];
        public bool HoldFirstHandshake;
        public TransportKind Kind => TransportKind.Ble;
        public string DisplayName => "Synthetic BLE";
        public TransportCandidate Candidate => new(Kind, "synthetic", DisplayName);
        public Task<IReadOnlyList<TransportCandidate>> ScanAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TransportCandidate>>([]);
        public ITransport Create(TransportCandidate candidate)
        {
            var link = new Link { Hold = HoldFirstHandshake && Links.Count == 0 ? Command.GetAppVersion : null };
            Links.Add(link); return link;
        }
    }

    private sealed class Link : ITransport
    {
        private readonly Channel<byte[]> _replies = Channel.CreateUnbounded<byte[]>();
        public List<Command> Commands { get; } = [];
        public Command? Hold;
        public int Disposed;
        public TaskCompletionSource Held { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReaderStopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TransportKind Kind => TransportKind.Ble;
        public string DisplayName => "Synthetic BLE";
        public bool IsConnected => Disposed == 0;
        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public void Drop() => _replies.Writer.TryComplete(new IOException("Synthetic link loss"));
        public void Reply(Command cmd, byte[] data) => _replies.Writer.TryWrite(FrameCodec.Encode(cmd, data, (ushort)Status.Success));
        public Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
        {
            foreach (var frame in new FrameParser().Push(data))
            {
                Commands.Add(frame.Command);
                if (frame.Command == Hold) { Held.TrySetResult(); continue; }
                byte[] reply = frame.Command switch
                {
                    Command.GetAppVersion => [2, 3], Command.GetGitVersion => Encoding.ASCII.GetBytes("2.3.15-cl"),
                    Command.GetDeviceChipId => [1, 2, 3, 4, 5, 6, 7, 8], Command.GetDeviceAddress => [0xC0, 1, 2, 3, 4, 5],
                    Command.GetDeviceModel => [0], Command.GetDeviceMode => [0], _ => []
                };
                Reply(frame.Command, reply);
            }
            return Task.CompletedTask;
        }
        public async Task<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            try
            {
                var next = await _replies.Reader.ReadAsync(ct);
                next.CopyTo(buffer); return next.Length;
            }
            catch { ReaderStopped.TrySetResult(); throw; }
        }
        // Deliberately does not unblock reads: the connection must cancel its own reader.
        public ValueTask DisposeAsync() { Disposed++; return ValueTask.CompletedTask; }
    }
}

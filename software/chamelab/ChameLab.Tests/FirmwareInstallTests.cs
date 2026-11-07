using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using ChameLab.Models;
using ChameLab.Protocol;
using ChameLab.Services;
using ChameLab.Transport;
using Xunit;

namespace ChameLab.Tests;

public class FirmwareInstallTests
{
    private static readonly DeviceInfo Original = new()
    {
        Model = DeviceModel.Ultra, FirmwareVersion = "2.3", GitVersion = "v2.3.14-cl",
        ChipId = "0102030405060708", Address = "C00102030405",
    };

    [Fact]
    public async Task LowBatteryDoesNotRestartBleDevice()
    {
        var scanner = new Scanner { Kind = TransportKind.Ble };
        var device = new DeviceService([scanner]);
        await device.ConnectAsync(scanner.Candidate);
        var bootloader = new Target();
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => new FirmwareUpdateService(device, []).InstallAsync(
                DfuPackage.Parse(DfuPackageTests.Zip()), new(scanner.Candidate, device.DeviceInfo!, bootloader),
                new Progress<FirmwareUpdateProgress>()));
            Assert.Equal(0, bootloader.Opens);
            Assert.Equal(ConnectionState.Connected, device.State);
        }
        finally { await device.DisconnectAsync(); }
    }

    [Fact]
    public async Task WrongModelIsRejectedBeforeOpeningBootloader()
    {
        var bootloader = new Target();
        var target = new FirmwareUpdateTarget(new(TransportKind.Serial, "test", "test"), Original, bootloader);
        var service = new FirmwareUpdateService(new DeviceService([]), []);
        await Assert.ThrowsAsync<InvalidDataException>(() => service.InstallAsync(
            DfuPackage.Parse(DfuPackageTests.Zip(model: 1)), target, new Progress<FirmwareUpdateProgress>()));
        Assert.Equal(0, bootloader.Opens);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RecoveryRetryConfirmsOriginalChipAndVersionEvenWhenLastAckIsLost(bool loseAck)
    {
        var scanner = new Scanner();
        var device = new DeviceService([scanner]);
        var bootloader = new Target(); bootloader.Channel.LoseLastAck = loseAck;
        var target = new FirmwareUpdateTarget(scanner.Candidate, Original, bootloader);
        var recovery = new FirmwareRecoveryStore(new MemoryKeyValueStore());
        try
        {
            await new FirmwareUpdateService(device, [], recovery: recovery).InstallAsync(DfuPackage.Parse(DfuPackageTests.Zip()), target,
                new Progress<FirmwareUpdateProgress>(), expectedVersion: new FirmwareVersion(2, 3, 15, 0));
            Assert.Equal(Original.ChipId, device.DeviceInfo!.ChipId);
            Assert.Equal(1, scanner.Connections);
            Assert.Null(recovery.Load());
        }
        finally { await device.DisconnectAsync(); }
    }

    [Fact]
    public async Task OldVersionAfterRestartIsNotReportedAsUpdated()
    {
        var scanner = new Scanner { Version = "v2.3.14-cl" };
        var device = new DeviceService([scanner]);
        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => new FirmwareUpdateService(device, []).InstallAsync(
                DfuPackage.Parse(DfuPackageTests.Zip()), new(scanner.Candidate, Original, new Target()),
                new Progress<FirmwareUpdateProgress>(), expectedVersion: new FirmwareVersion(2, 3, 15, 0)));
        }
        finally { await device.DisconnectAsync(); }
    }

    [Fact]
    public async Task AnUnrelatedCandidateIsNeverChosenAfterFlash()
    {
        var scanner = new Scanner { ShowOriginal = false };
        var device = new DeviceService([scanner]);
        using var stop = new CancellationTokenSource(150);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new FirmwareUpdateService(device, []).InstallAsync(
            DfuPackage.Parse(DfuPackageTests.Zip()), new(scanner.Candidate, Original, new Target()),
            new Progress<FirmwareUpdateProgress>(), stop.Token));
        Assert.Equal(0, scanner.Connections);
    }

    private sealed class Target : IDfuTarget
    {
        public int Opens;
        public readonly SecureDfuTests.Bootloader Channel = new();
        public Task<IDfuChannel?> TryOpenAsync(CancellationToken ct) { Opens++; return Task.FromResult<IDfuChannel?>(Channel); }
    }

    private sealed class Scanner : ITransportScanner
    {
        public string Version = "v2.3.15-cl";
        public bool ShowOriginal = true;
        public int Connections;
        public TransportCandidate Candidate => new(Kind, "original", "Synthetic device");
        public TransportKind Kind { get; set; } = TransportKind.Serial;
        public string DisplayName => "Synthetic scanner";
        public Task<IReadOnlyList<TransportCandidate>> ScanAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TransportCandidate>>(ShowOriginal ? [Candidate] : [new(Kind, "unrelated", "Other device")]);
        public ITransport Create(TransportCandidate candidate) { Connections++; return new DeviceTransport(Version); }
    }

    private sealed class DeviceTransport(string version) : ITransport
    {
        private readonly Channel<byte[]> _replies = Channel.CreateUnbounded<byte[]>();
        public TransportKind Kind => TransportKind.Serial;
        public string DisplayName => "Synthetic app";
        public bool IsConnected => true;
        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct = default)
        {
            foreach (var request in new FrameParser().Push(bytes))
            {
                byte[] payload = request.Command switch
                {
                    Command.GetBatteryInfoEx => [0x0C, 0xE4, 5, 0],
                    Command.GetAppVersion => [2, 3], Command.GetGitVersion => Encoding.UTF8.GetBytes(version),
                    Command.GetDeviceChipId => [1, 2, 3, 4, 5, 6, 7, 8], Command.GetDeviceAddress => [0xC0, 1, 2, 3, 4, 5],
                    Command.GetDeviceModel or Command.GetDeviceMode => [0], _ => [],
                };
                _replies.Writer.TryWrite(FrameCodec.Encode(request.Command, payload, (ushort)Status.Success));
            }
            return Task.CompletedTask;
        }
        public async Task<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        { var bytes = await _replies.Reader.ReadAsync(ct); bytes.CopyTo(buffer); return bytes.Length; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
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

public class DeviceBackupServiceTests
{
    private const string Password = "synthetic backup password";

    [Fact]
    public async Task MissingInitialStateAbortsBackupBeforeChangingTheSlotOrMode()
    {
        var scanner = new Scanner(); var device = new DeviceService([scanner]);
        await device.ConnectAsync(scanner.Candidate);
        try
        {
            scanner.Link.ShortState = true; scanner.Link.Commands.Clear();
            await Assert.ThrowsAsync<InvalidDataException>(() => new DeviceBackupService(device, new MemoryKeyValueStore()).CreateAsync(Password));
            Assert.DoesNotContain(Command.SetActiveSlot, scanner.Link.Commands);
            Assert.DoesNotContain(Command.ChangeDeviceMode, scanner.Link.Commands);
        }
        finally { await device.DisconnectAsync(); }
    }

    [Fact]
    public async Task DisconnectUpdatesStateImmediatelyButReconnectWaitsForNativeClose()
    {
        var scanner = new Scanner(); var device = new DeviceService([scanner]);
        await device.ConnectAsync(scanner.Candidate);
        var close = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        scanner.Link.CloseGate = close.Task;
        var closing = device.DisconnectAsync();
        try
        {
            Assert.Equal(ConnectionState.Disconnected, device.State);
            Assert.Null(device.Client);
            Assert.False(closing.IsCompleted);
            var reconnecting = device.ConnectAsync(scanner.Candidate);
            Assert.False(reconnecting.IsCompleted);
            Assert.Equal(1, scanner.Created);
            close.SetResult();
            await closing; await reconnecting;
            Assert.Equal(2, scanner.Created);
            Assert.Equal(ConnectionState.Connected, device.State);
        }
        finally { close.TrySetResult(); await closing; await device.DisconnectAsync(); }
    }

    [Fact]
    public async Task MissingRestoreCommandIsRejectedBeforeWritingAnySlot()
    {
        var scanner = new Scanner(); scanner.Link.HasClassic = true; scanner.Link.Unsupported = Command.Mf1SetGen2Mode;
        var device = new DeviceService([scanner]); var storage = new MemoryKeyValueStore();
        await device.ConnectAsync(scanner.Candidate);
        try
        {
            var service = new DeviceBackupService(device, storage);
            await service.CreateAsync(Password);
            var snapshot = EncryptedDeviceBackup.Decrypt((await service.ReadEncryptedAsync())!, Password);
            scanner.Link.Commands.Clear();
            await Assert.ThrowsAsync<NotSupportedException>(() => service.RestoreAsync(snapshot));
            Assert.Empty(scanner.Link.Commands);
        }
        finally { await device.DisconnectAsync(); }
    }

    [Fact]
    public async Task PopulatedDisabledClassicSlotRestoresMemoryAndEmulatorFlags()
    {
        var scanner = new Scanner(); scanner.Link.HasClassic = true;
        var device = new DeviceService([scanner]); var storage = new MemoryKeyValueStore();
        await device.ConnectAsync(scanner.Candidate);
        try
        {
            var service = new DeviceBackupService(device, storage);
            await service.CreateAsync(Password);
            var snapshot = EncryptedDeviceBackup.Decrypt((await service.ReadEncryptedAsync())!, Password);
            Assert.False(snapshot.Slots[0].Hf!.Enabled);
            Assert.True(snapshot.Slots[0].Emulator.Mf1!.Gen2);
            Array.Clear(scanner.Link.Memory); Array.Clear(scanner.Link.Config);
            await service.RestoreAsync(snapshot);
            Assert.Equal(snapshot.Slots[0].Hf!.Card.Data.SelectMany(b => b), scanner.Link.Memory);
            Assert.Equal(new byte[] { 1, 0, 1, 1, 0 }, scanner.Link.Config);
        }
        finally { await device.DisconnectAsync(); }
    }

    [Fact]
    public async Task RestoreStopsAtRejectedWriteAndDoesNotReportSuccess()
    {
        var scanner = new Scanner(); scanner.Link.HasClassic = true;
        var device = new DeviceService([scanner]); var storage = new MemoryKeyValueStore();
        await device.ConnectAsync(scanner.Candidate);
        try
        {
            var service = new DeviceBackupService(device, storage);
            await service.CreateAsync(Password);
            var snapshot = EncryptedDeviceBackup.Decrypt((await service.ReadEncryptedAsync())!, Password);
            scanner.Link.Reject = Command.Mf1SetGen2Mode;
            scanner.Link.Commands.Clear();
            await Assert.ThrowsAsync<ChameleonException>(() => service.RestoreAsync(snapshot));
            Assert.DoesNotContain(Command.SetAnimationMode, scanner.Link.Commands);
            Assert.Equal(3, scanner.Link.ActiveSlot); Assert.Equal(DeviceMode.Reader, scanner.Link.Mode);
            Assert.NotNull(await service.ReadEncryptedAsync());
        }
        finally { await device.DisconnectAsync(); }
    }

    [Fact]
    public async Task TruncatedOptionalSettingCannotBecomeAZeroInBackup()
    {
        var scanner = new Scanner(); var device = new DeviceService([scanner]);
        await device.ConnectAsync(scanner.Candidate);
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => device.Client!.GetMf1PrngTypeAsync(strict: true));
            await Assert.ThrowsAsync<InvalidDataException>(() => device.Client!.GetMf0UidMagicModeAsync(strict: true));
            await Assert.ThrowsAsync<InvalidDataException>(() => device.Client!.GetMf0WriteModeAsync(strict: true));
            await Assert.ThrowsAsync<InvalidDataException>(() => device.Client!.GetMf0DetectionEnableAsync(strict: true));
        }
        finally { await device.DisconnectAsync(); }
    }

    [Fact]
    public async Task CaptureRestoresModeAndSlotPersistsOnlyCiphertextAndRestoresAfterRestart()
    {
        var scanner = new Scanner(); var device = new DeviceService([scanner]);
        var storage = new MemoryKeyValueStore();
        await device.ConnectAsync(scanner.Candidate);
        try
        {
            var service = new DeviceBackupService(device, storage);
            await service.CreateAsync(Password);
            Assert.Equal(3, scanner.Link.ActiveSlot);
            Assert.Equal(DeviceMode.Reader, scanner.Link.Mode);
            Assert.DoesNotContain(Command.SlotDataConfigSave, scanner.Link.Commands);
            var copy = await new DeviceBackupService(device, storage).ReadEncryptedAsync();
            var snapshot = EncryptedDeviceBackup.Decrypt(copy!, Password);
            Assert.Equal(8, snapshot.Slots.Length);
            scanner.Link.ActiveSlot = 5;
            await service.RestoreAsync(snapshot);
            Assert.Equal(3, scanner.Link.ActiveSlot);
            Assert.Equal(DeviceMode.Reader, scanner.Link.Mode);
            Assert.Equal(16, scanner.Link.Commands.Count(c => c == Command.DeleteSlotSenseType));
        }
        finally { await device.DisconnectAsync(); }
    }

    [Fact]
    public async Task IncompleteSummaryDoesNotReplacePreviousEncryptedCopy()
    {
        var scanner = new Scanner(); var device = new DeviceService([scanner]);
        var storage = new MemoryKeyValueStore();
        await device.ConnectAsync(scanner.Candidate);
        try
        {
            var service = new DeviceBackupService(device, storage);
            await service.CreateAsync(Password);
            var before = await service.ReadEncryptedAsync();
            scanner.Link.ShortSlotInfo = true;
            await Assert.ThrowsAsync<InvalidDataException>(() => service.CreateAsync(Password));
            Assert.Equal(before, await service.ReadEncryptedAsync());
            Assert.Equal(3, scanner.Link.ActiveSlot);
            Assert.Equal(DeviceMode.Reader, scanner.Link.Mode);
        }
        finally { await device.DisconnectAsync(); }
    }

    [Fact]
    public async Task TruncatedMemoryIsRejectedInsteadOfFilledWithZeros()
    {
        var scanner = new Scanner(); var device = new DeviceService([scanner]);
        await device.ConnectAsync(scanner.Candidate);
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => device.Client!.ReadSlotContentAsync(
                new SlotInfo { Index = 2, HfType = TagSpecificType.Mifare1024, HfEnabled = true }, strict: true));
            Assert.Equal(3, scanner.Link.ActiveSlot);
        }
        finally { await device.DisconnectAsync(); }
    }

    [Fact]
    public async Task WrongDeviceIsRejectedBeforeAnyRestoreCommand()
    {
        var scanner = new Scanner(); var device = new DeviceService([scanner]);
        await device.ConnectAsync(scanner.Candidate);
        try
        {
            var snapshot = EncryptedDeviceBackupTests.Snapshot();
            snapshot = snapshot with { Device = snapshot.Device with { ChipId = "0000000000000000" } };
            scanner.Link.Commands.Clear();
            await Assert.ThrowsAsync<InvalidOperationException>(() => new DeviceBackupService(device, new MemoryKeyValueStore()).RestoreAsync(snapshot));
            Assert.Empty(scanner.Link.Commands);
        }
        finally { await device.DisconnectAsync(); }
    }

    private sealed class Scanner : ITransportScanner
    {
        public readonly Link Link = new();
        public int Created;
        public TransportKind Kind => TransportKind.Serial;
        public string DisplayName => "Synthetic";
        public TransportCandidate Candidate => new(Kind, "synthetic", "Synthetic");
        public Task<IReadOnlyList<TransportCandidate>> ScanAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<TransportCandidate>>([Candidate]);
        public ITransport Create(TransportCandidate candidate) { Created++; return Link; }
    }

    private sealed class Link : ITransport
    {
        private readonly Channel<byte[]> _replies = Channel.CreateUnbounded<byte[]>();
        public readonly List<Command> Commands = new();
        public int ActiveSlot = 3;
        public DeviceMode Mode = DeviceMode.Reader;
        public bool ShortSlotInfo;
        public bool ShortState;
        public bool HasClassic;
        public Command? Reject;
        public Command? Unsupported;
        public Task CloseGate = Task.CompletedTask;
        public byte[] Memory = Enumerable.Range(0, 1024).Select(i => (byte)(i * 31)).ToArray();
        public byte[] Config = [1, 0, 1, 1, 0];
        private bool _enabled;
        private string _nick = "Synthetic Classic";
        private byte[] _colors = new byte[48];
        public TransportKind Kind => TransportKind.Serial;
        public string DisplayName => "Synthetic";
        public bool IsConnected => true;
        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
        {
            foreach (var frame in new FrameParser().Push(data))
            {
                Commands.Add(frame.Command);
                if (Reject == frame.Command)
                {
                    _replies.Writer.TryWrite(FrameCodec.Encode(frame.Command, [], (ushort)Status.FlashWriteFail));
                    continue;
                }
                if (frame.Command == Command.ChangeDeviceMode) Mode = (DeviceMode)frame.Data[0];
                if (frame.Command == Command.SetActiveSlot) ActiveSlot = frame.Data[0] + 1;
                if (HasClassic)
                {
                    var p = frame.Data;
                    switch (frame.Command)
                    {
                        case Command.SetSlotDataDefault: Array.Clear(Memory); Array.Clear(Config); break;
                        case Command.Mf1WriteEmuBlockData: p.AsSpan(1).CopyTo(Memory.AsSpan(p[0] * 16)); break;
                        case Command.SetSlotEnable when p[0] == 0 && p[1] == (byte)TagSenseType.Hf: _enabled = p[2] != 0; break;
                        case Command.SetSlotTagNick when p[0] == 0: _nick = Encoding.UTF8.GetString(p[2..]); break;
                        case Command.DeleteSlotTagNick when p[0] == 0: _nick = ""; break;
                        case Command.SetSlotColor when p[0] == 0: p.AsSpan(2, 3).CopyTo(_colors); break;
                        case Command.Mf1SetDetectionEnable: Config[0] = p[0]; break;
                        case Command.Mf1SetGen1aMode: Config[1] = p[0]; break;
                        case Command.Mf1SetGen2Mode: Config[2] = p[0]; break;
                        case Command.Mf1SetBlockAntiCollMode: Config[3] = p[0]; break;
                        case Command.Mf1SetWriteMode: Config[4] = p[0]; break;
                    }
                }
                byte[] reply = frame.Command switch
                {
                    Command.GetAppVersion => [2, 3], Command.GetGitVersion => Encoding.ASCII.GetBytes("v2.3.15-cl"),
                    Command.GetDeviceChipId => [1, 2, 3, 4, 5, 6, 7, 8], Command.GetDeviceAddress => [0xC0, 1, 2, 3, 4, 5],
                    Command.GetDeviceModel => [0], Command.GetDeviceMode => [(byte)Mode],
                    Command.GetDeviceCapabilities => Enum.GetValues<Command>().Where(c => c != Unsupported).SelectMany(c => new[] { (byte)((ushort)c >> 8), (byte)c }).ToArray(),
                    Command.GetActiveSlot when ShortState => [],
                    Command.GetActiveSlot => [(byte)(ActiveSlot - 1)],
                    Command.GetSlotInfo when HasClassic => [(byte)((ushort)TagSpecificType.Mifare1024 >> 8), (byte)((ushort)TagSpecificType.Mifare1024 & 255), .. new byte[30]],
                    Command.GetSlotInfo => new byte[ShortSlotInfo ? 31 : 32],
                    Command.GetEnabledSlots => [(byte)(_enabled ? 1 : 0), .. new byte[15]],
                    Command.GetAllSlotNicks when HasClassic => [(byte)Encoding.UTF8.GetByteCount(_nick), .. Encoding.UTF8.GetBytes(_nick), .. new byte[15]],
                    Command.GetAllSlotNicks => new byte[16], Command.GetAllSlotColors => _colors,
                    Command.GetAnimationMode or Command.GetSleepTimeout or Command.GetButtonPressConfig or Command.GetLongButtonPressConfig or Command.GetBlePairingEnable => [0],
                    Command.GetLongPressThreshold => [1, 244], Command.GetBlePairingKey => Encoding.ASCII.GetBytes("123456"),
                    Command.Hf14aGetAntiCollData => [4, 1, 2, 3, 4, 4, 0, 8, 0],
                    Command.Mf1ReadEmuBlockData when HasClassic => Memory.AsSpan(frame.Data[0] * 16, frame.Data[1] * 16).ToArray(),
                    Command.Mf1ReadEmuBlockData => [0],
                    Command.Mf1GetEmulatorConfig when HasClassic => Config,
                    Command.Mf1GetPrngType or Command.Mf1GetFieldOffDoReset when HasClassic => [0],
                    _ => [],
                };
                _replies.Writer.TryWrite(FrameCodec.Encode(frame.Command, reply, (ushort)Status.Success));
            }
            return Task.CompletedTask;
        }
        public async Task<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        { var data = await _replies.Reader.ReadAsync(ct); data.CopyTo(buffer); return data.Length; }
        public ValueTask DisposeAsync() => new(CloseGate);
    }
}

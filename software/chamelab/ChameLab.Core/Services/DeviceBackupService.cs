using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ChameLab.Localization;
using ChameLab.Models;
using ChameLab.Protocol;

namespace ChameLab.Services;

public sealed class DeviceBackupService(DeviceService device, IKeyValueStore store)
{
    private const string StoreKey = "device-backup";
    private readonly SemaphoreSlim _gate = new(1, 1);
    public bool IsSupported => EncryptedDeviceBackup.IsSupported;

    public Task<byte[]?> ReadEncryptedAsync() => Task.Run(() =>
    {
        var value = store.Read(StoreKey);
        if (value is null) return null;
        if (value.Length > EncryptedDeviceBackup.MaxFileSize * 2) throw new InvalidDataException(Strings.Backup_Invalid);
        try { return Convert.FromBase64String(value); }
        catch (FormatException) { throw new InvalidDataException(Strings.Backup_Invalid); }
    });

    public Task CreateAsync(string password, IProgress<int>? progress = null, CancellationToken ct = default) => Task.Run(async () =>
    {
        if (!EncryptedDeviceBackup.IsValidPassword(password)) throw new ArgumentException(Strings.Backup_PasswordHint);
        if (!await _gate.WaitAsync(0, ct)) throw new InvalidOperationException(Strings.Backup_Busy);
        try
        {
            var client = device.Client ?? throw new ChameleonDisconnectedException();
            var info = device.DeviceInfo ?? throw new ChameleonDisconnectedException();
            var snapshot = await CaptureAsync(client, info, progress, ct);
            var bytes = EncryptedDeviceBackup.Encrypt(snapshot, password);
            // Authenticate and parse what will actually be stored before allowing the update.
            _ = EncryptedDeviceBackup.Decrypt(bytes, password);
            ct.ThrowIfCancellationRequested();
            store.WriteDurable(StoreKey, Convert.ToBase64String(bytes));
            var saved = await ReadEncryptedAsync();
            if (saved is null || !saved.AsSpan().SequenceEqual(bytes)) throw new IOException(Strings.Backup_SaveFailed);
        }
        finally { _gate.Release(); }
    }, ct);

    private static async Task<DeviceBackupSnapshot> CaptureAsync(ChameleonClient client, DeviceInfo info,
        IProgress<int>? progress, CancellationToken ct)
    {
        return await client.WithSlotLockAsync(async () =>
        {
            var active = await client.GetActiveSlotAsync(ct, strict: true);
            var mode = await client.GetDeviceModeAsync(ct, strict: true);
            try
            {
                await client.SetDeviceModeAsync(DeviceMode.Tag, ct);
                // Missing summary commands must not silently lose names, enable flags or colors.
                var slots = await client.GetAllSlotsAsync(ct, strict: true);
                var content = new List<SlotContent>();
                foreach (var slot in slots)
                {
                    content.Add(await client.ReadSlotContentAsync(slot, ct: ct, strict: true));
                    progress?.Report(slot.Index);
                }
                async Task<T?> Optional<T>(Func<Task<T>> read) where T : struct
                {
                    try { return await read(); }
                    catch (ChameleonException ex) when (ex.Status is Status.InvalidCmd or Status.NotImplemented) { return null; }
                }
                async Task<int> ReadValue(Command command, int size = 1, byte[]? payload = null)
                {
                    var reply = await client.Connection.SendExpectAsync(command, Status.Success, payload, ct: ct);
                    if (reply.Data.Length != size) throw new InvalidDataException(Strings.Backup_IncompleteRead);
                    return size == 1 ? reply.Data[0] : System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(reply.Data);
                }
                var pairingValue = await Optional(() => ReadValue(Command.GetBlePairingEnable));
                if (pairingValue is not (null or 0 or 1)) throw new InvalidDataException(Strings.Backup_Invalid);
                bool? pairing = pairingValue is null ? null : pairingValue == 1;
                var settings = new DeviceBackupSettings((AnimationMode)await ReadValue(Command.GetAnimationMode), await ReadValue(Command.GetSleepTimeout),
                    (ButtonPressFunction)await ReadValue(Command.GetButtonPressConfig, payload: [(byte)ButtonType.A]),
                    (ButtonPressFunction)await ReadValue(Command.GetButtonPressConfig, payload: [(byte)ButtonType.B]),
                    (ButtonPressFunction)await ReadValue(Command.GetLongButtonPressConfig, payload: [(byte)ButtonType.A]),
                    (ButtonPressFunction)await ReadValue(Command.GetLongButtonPressConfig, payload: [(byte)ButtonType.B]),
                    await Optional(() => ReadValue(Command.GetLongPressThreshold, 2)), pairing,
                    pairing is not null ? await client.GetBlePairingKeyAsync(ct) : null);
                var snapshot = new DeviceBackupSnapshot(1, DateTimeOffset.UtcNow, info, active, mode, settings, content.ToArray());
                EncryptedDeviceBackup.Validate(snapshot);
                return snapshot;
            }
            finally
            {
                // Cleanup gets its own bounded token: a cancelled read must still restore the slot.
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                try { await client.SetActiveSlotAsync(active, cleanup.Token); }
                finally { await client.SetDeviceModeAsync(mode, cleanup.Token); }
            }
        }, ct);
    }

    public Task RestoreAsync(DeviceBackupSnapshot snapshot, IProgress<int>? progress = null, CancellationToken ct = default) => Task.Run(async () =>
    {
        EncryptedDeviceBackup.Validate(snapshot);
        if (!await _gate.WaitAsync(0, ct)) throw new InvalidOperationException(Strings.Backup_Busy);
        try
        {
            var client = device.Client ?? throw new ChameleonDisconnectedException();
            var info = device.DeviceInfo ?? throw new ChameleonDisconnectedException();
            if (snapshot.Device.Model != info.Model || snapshot.Device.ChipId != info.ChipId)
                throw new InvalidOperationException(Strings.Firmware_WrongDevice);
            var capabilities = await client.GetCapabilitiesAsync(ct);
            var required = new List<Command>
            {
                Command.ChangeDeviceMode, Command.SetActiveSlot, Command.DeleteSlotSenseType, Command.SlotDataConfigSave,
                Command.SetAnimationMode, Command.SetSleepTimeout, Command.SetButtonPressConfig, Command.SetLongButtonPressConfig,
                Command.SaveSettings, Command.SetSlotColor, Command.GetAllSlotColors, Command.GetAllSlotNicks,
            };
            if (snapshot.Slots.Any(s => s.Hf is not null || s.Lf is not null))
                required.AddRange([Command.SetSlotTagType, Command.SetSlotDataDefault, Command.SetSlotEnable, Command.SetSlotTagNick, Command.DeleteSlotTagNick]);
            if (snapshot.Slots.Any(s => s.Hf is not null)) required.Add(Command.Hf14aSetAntiCollData);
            if (snapshot.Slots.Any(s => s.Emulator.Mf1 is not null))
                required.AddRange([Command.Mf1WriteEmuBlockData, Command.Mf1SetGen1aMode, Command.Mf1SetGen2Mode,
                    Command.Mf1SetBlockAntiCollMode, Command.Mf1SetDetectionEnable, Command.Mf1SetWriteMode]);
            if (snapshot.Slots.Any(s => s.Emulator.Mf0 is not null))
                required.AddRange([Command.Mf0NtagWriteEmuPageData, Command.Mf0NtagSetUidMagicMode, Command.Mf0NtagSetWriteMode]);
            foreach (var slot in snapshot.Slots)
            {
                if (slot.Hf?.Card is { } hf)
                {
                    if (hf.Extra.UltralightVersion.Length > 0) required.Add(Command.Mf0NtagSetVersionData);
                    if (hf.Extra.UltralightSignature.Length > 0) required.Add(Command.Mf0NtagSetSignatureData);
                    if (hf.Extra.UltralightCounters.Count > 0) required.Add(Command.Mf0NtagSetCounterData);
                    if (hf.Tag.HasUltralightCounters()) required.Add(Command.Mf0NtagResetAuthCnt);
                }
                if (slot.Lf is { } lf) required.Add(ChameleonClient.LfEmulatorSetCommand(lf.Card.Tag));
            }
            if (snapshot.Slots.Any(s => s.LfCycle is not null)) required.Add(Command.LfSetCycle);
            if (snapshot.Slots.Any(s => s.Emulator.Mf1?.Prng is not null)) required.Add(Command.Mf1SetPrngType);
            if (snapshot.Slots.Any(s => s.Emulator.Mf1?.FieldOffReset is not null)) required.Add(Command.Mf1SetFieldOffDoReset);
            if (snapshot.Slots.Any(s => s.Emulator.Mf0?.Detection is not null)) required.Add(Command.Mf0NtagSetDetectionEnable);
            if (snapshot.Settings.LongPressThreshold is not null) required.Add(Command.SetLongPressThreshold);
            if (snapshot.Settings.BlePairing is not null) { required.Add(Command.SetBlePairingEnable); required.Add(Command.SetBlePairingKey); }
            if (required.Any(c => !capabilities.Contains((ushort)c))) throw new NotSupportedException(Strings.Backup_NotSupported);
            await client.WithSlotLockAsync(async () =>
            {
                var mode = await client.GetDeviceModeAsync(ct, strict: true);
                var active = await client.GetActiveSlotAsync(ct, strict: true);
                var completed = false;
                try
                {
                    await client.SetDeviceModeAsync(DeviceMode.Tag, ct);
                    foreach (var slot in snapshot.Slots.OrderBy(s => s.Index))
                    {
                        await client.WriteSlotContentAsync(slot.Index, slot, ct: ct, strict: true);
                        progress?.Report(slot.Index);
                    }
                    var s = snapshot.Settings;
                    await client.SetAnimationModeAsync(s.Animation, ct);
                    await client.SetSleepTimeoutAsync(s.SleepTimeout, ct);
                    await client.SetButtonConfigAsync(ButtonType.A, false, s.AShort, ct);
                    await client.SetButtonConfigAsync(ButtonType.B, false, s.BShort, ct);
                    await client.SetButtonConfigAsync(ButtonType.A, true, s.ALong, ct);
                    await client.SetButtonConfigAsync(ButtonType.B, true, s.BLong, ct);
                    if (s.LongPressThreshold is { } threshold) await client.SetLongPressThresholdAsync(threshold, ct);
                    if (s.BlePin is { } pin) await client.SetBlePairingKeyAsync(pin, ct);
                    if (s.BlePairing is { } pairing) await client.SetBlePairingEnableAsync(pairing, ct);
                    await client.SetActiveSlotAsync(snapshot.ActiveSlot, ct);
                    await client.SaveSettingsAsync(ct);
                    await client.SaveSlotConfigAsync(ct);
                    completed = true;
                }
                finally
                {
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    try { await client.SetActiveSlotAsync(completed ? snapshot.ActiveSlot : active, cleanup.Token); }
                    finally { await client.SetDeviceModeAsync(completed ? snapshot.Mode : mode, cleanup.Token); }
                }
            }, ct);
            var actual = await CaptureAsync(client, info, null, ct);
            if (!Equivalent(snapshot, actual)) throw new IOException(Strings.Backup_VerificationFailed);
        }
        finally { _gate.Release(); }
    }, ct);

    internal static bool Equivalent(DeviceBackupSnapshot expected, DeviceBackupSnapshot actual)
    {
        // Card library IDs/names are regenerated while reading a slot; they are not device data.
        static byte[] Canonical(DeviceBackupSnapshot snapshot)
        {
            var slots = snapshot.Slots.OrderBy(s => s.Index).Select(s => s with
            {
                Hf = Normalize(s.Hf), Lf = Normalize(s.Lf),
            }).ToArray();
            return JsonSerializer.SerializeToUtf8Bytes(snapshot with { Created = default, Device = snapshot.Device with
            { FirmwareVersion = "", GitVersion = "", Mode = default, SupportedCommands = null, SupportsSlotColors = false }, Slots = slots }, DeviceBackupJson.Default.DeviceBackupSnapshot);
        }
        static SlotSide? Normalize(SlotSide? side)
        {
            if (side is null) return null;
            var card = side.Card.Clone();
            card.Id = ""; card.Name = ""; card.Color = "";
            return side with { Card = card };
        }
        var left = Canonical(expected); var right = Canonical(actual);
        try { return left.AsSpan().SequenceEqual(right); }
        finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(left); System.Security.Cryptography.CryptographicOperations.ZeroMemory(right); }
    }
}

using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ChameLab.Localization;
using ChameLab.Models;
using ChameLab.Protocol;

namespace ChameLab.Services;

public sealed record DeviceBackupSettings(AnimationMode Animation, int SleepTimeout,
    ButtonPressFunction AShort, ButtonPressFunction BShort, ButtonPressFunction ALong, ButtonPressFunction BLong,
    int? LongPressThreshold, bool? BlePairing, string? BlePin);

public sealed record DeviceBackupSnapshot(int Version, DateTimeOffset Created, DeviceInfo Device,
    int ActiveSlot, DeviceMode Mode, DeviceBackupSettings Settings, SlotContent[] Slots);

/// <summary>Versioned authenticated encryption; passwords and plaintext are never persisted.</summary>
public static class EncryptedDeviceBackup
{
    private static ReadOnlySpan<byte> Magic => "CLBACK01"u8;
    private const int HeaderSize = 8 + 16 + 12; // version, random PBKDF2 salt, GCM nonce
    private const int TagSize = 16;
    private const int Iterations = 600_000; // fixed by format version, never supplied by an untrusted file
    public const int MaxFileSize = 1024 * 1024;
    public static bool IsSupported => AesGcm.IsSupported;
    public static bool IsValidPassword(string password) => password.Length is >= 12 and <= 1024;

    public static byte[] Encrypt(DeviceBackupSnapshot snapshot, string password)
    {
        if (!IsValidPassword(password)) throw new ArgumentException(Strings.Backup_PasswordHint);
        Validate(snapshot);
        var plain = JsonSerializer.SerializeToUtf8Bytes(snapshot, DeviceBackupJson.Default.DeviceBackupSnapshot);
        byte[]? key = null;
        try
        {
            if (plain.Length > MaxFileSize - HeaderSize - TagSize) throw new InvalidDataException(Strings.Backup_Invalid);
            var result = new byte[HeaderSize + plain.Length + TagSize];
            Magic.CopyTo(result);
            RandomNumberGenerator.Fill(result.AsSpan(8, 16 + 12));
            key = Derive(password, result.AsSpan(8, 16));
            using var aes = new AesGcm(key, TagSize);
            aes.Encrypt(result.AsSpan(24, 12), plain, result.AsSpan(HeaderSize, plain.Length),
                result.AsSpan(HeaderSize + plain.Length, TagSize), result.AsSpan(0, HeaderSize));
            return result;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
            if (key is not null) CryptographicOperations.ZeroMemory(key);
        }
    }

    public static DeviceBackupSnapshot Decrypt(byte[] encrypted, string password)
    {
        if (encrypted.Length <= HeaderSize + TagSize || encrypted.Length > MaxFileSize ||
            !encrypted.AsSpan(0, 8).SequenceEqual(Magic) || !IsValidPassword(password))
            throw new InvalidDataException(Strings.Backup_InvalidOrPassword);
        var plain = new byte[encrypted.Length - HeaderSize - TagSize];
        byte[]? key = null;
        try
        {
            key = Derive(password, encrypted.AsSpan(8, 16));
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(encrypted.AsSpan(24, 12), encrypted.AsSpan(HeaderSize, plain.Length),
                encrypted.AsSpan(HeaderSize + plain.Length, TagSize), plain, encrypted.AsSpan(0, HeaderSize));
            var snapshot = JsonSerializer.Deserialize(plain, DeviceBackupJson.Default.DeviceBackupSnapshot)
                           ?? throw new InvalidDataException(Strings.Backup_Invalid);
            Validate(snapshot);
            return snapshot;
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException)
        {
            throw new InvalidDataException(Strings.Backup_InvalidOrPassword);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
            if (key is not null) CryptographicOperations.ZeroMemory(key);
        }
    }

    private static byte[] Derive(string password, ReadOnlySpan<byte> salt) =>
        Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, 32);

    public static void Validate(DeviceBackupSnapshot snapshot)
    {
        static void Require(bool condition) { if (!condition) throw new InvalidDataException(Strings.Backup_Invalid); }
        Require(snapshot.Version == 1 && snapshot.Device is not null && snapshot.Settings is not null && snapshot.Slots is { Length: 8 });
        Require(snapshot.Device!.Model is DeviceModel.Ultra or DeviceModel.Lite && snapshot.Device.ChipId is { Length: 16 } && snapshot.Device.ChipId.All(Uri.IsHexDigit));
        Require(snapshot.ActiveSlot is >= 1 and <= 8 && Enum.IsDefined(snapshot.Mode));
        var settings = snapshot.Settings!;
        Require(Enum.IsDefined(settings.Animation) && settings.SleepTimeout is >= 0 and <= 255 &&
                Enum.IsDefined(settings.AShort) && Enum.IsDefined(settings.BShort) && Enum.IsDefined(settings.ALong) && Enum.IsDefined(settings.BLong) &&
                (settings.LongPressThreshold is null or >= 0 and <= ushort.MaxValue) &&
                ((settings.BlePairing is null) == (settings.BlePin is null)) &&
                (settings.BlePin is null || settings.BlePin.Length == 6 && settings.BlePin.All(char.IsAsciiDigit)));
        Require(snapshot.Slots!.All(s => s is not null) && snapshot.Slots.Select(s => s.Index).Order().SequenceEqual(Enumerable.Range(1, 8)));
        foreach (var slot in snapshot.Slots)
        {
            Require(slot.Emulator is not null);
            foreach (var (side, sense) in new[] { (slot.Hf, TagSenseType.Hf), (slot.Lf, TagSenseType.Lf) })
            {
                if (side is null) continue;
                Require(side.Card is not null && side.Nick is not null && Encoding.UTF8.GetByteCount(side.Nick) <= 32);
                Require(side.Color is null || side.Color.Length == 7 && side.Color[0] == '#' && side.Color.AsSpan(1).ToArray().All(Uri.IsHexDigit));
                var card = side.Card!;
                Require(card.Tag != TagSpecificType.Undefined && card.Tag == ChameleonClient.SlotTypeFor(card.Tag) && card.Sense == sense);
                Require(card.Uid is not null && card.Data is not null && card.Extra is not null);
                if (sense == TagSenseType.Lf) Require(card.Uid!.Length == card.Tag.LfIdLength());
                else
                {
                    Require(card.Uid!.Length is 4 or 7 or 10 && card.Atqa is { Length: 2 } && card.Ats is { Length: <= 255 });
                    Require(card.Tag.IsMifareClassic() || card.Tag.IsUltralight());
                    var width = card.Tag.IsMifareClassic() ? 16 : 4;
                    var count = card.Tag.IsMifareClassic() ? card.Tag.MifareClassicBlocks() : card.Tag.UltralightPages();
                    Require(card.Data!.Count == count && card.Data.All(d => d is not null && d.Length == width));
                    Require(card.Extra!.UltralightVersion is { Length: 0 or 8 } && card.Extra.UltralightSignature is { Length: 0 or 32 } &&
                            card.Extra.UltralightCounters is { Count: <= 3 } && card.Extra.UltralightCounters.All(n => n is >= 0 and <= 0xFFFFFF));
                    Require(card.Extra.UnknownKeyA is { Count: 0 } && card.Extra.UnknownKeyB is { Count: 0 });
                }
            }
            if (slot.LfCycle is { } cycle)
            {
                Require(slot.Lf is not null && cycle.Extras is { Length: <= 7 } && cycle.Extras.All(c => c is not null && c.Id is not null && c.IsValid));
            }
            Require((slot.Hf?.Card.Tag.IsMifareClassic() == true) == (slot.Emulator!.Mf1 is not null));
            Require((slot.Hf?.Card.Tag.IsUltralight() == true) == (slot.Emulator.Mf0 is not null));
            if (slot.Emulator!.Mf1 is { } mf1)
                Require(slot.Hf?.Card.Tag.IsMifareClassic() == true && Enum.IsDefined(mf1.WriteMode) && (mf1.Prng is null || Enum.IsDefined(mf1.Prng.Value)));
            if (slot.Emulator.Mf0 is { } mf0) Require(slot.Hf?.Card.Tag.IsUltralight() == true && Enum.IsDefined(mf0.WriteMode));
        }
    }
}

[JsonSourceGenerationOptions(IgnoreReadOnlyProperties = true)]
[JsonSerializable(typeof(DeviceBackupSnapshot))]
internal partial class DeviceBackupJson : JsonSerializerContext;

using System;
using System.IO;
using System.Linq;
using System.Text;
using ChameLab.Models;
using ChameLab.Protocol;
using ChameLab.Services;
using Xunit;

namespace ChameLab.Tests;

public class EncryptedDeviceBackupTests
{
    private const string Password = "synthetic backup password";

    [Fact]
    public void RoundTripPreservesSlotsSettingsAndEmulatorData()
    {
        var expected = Snapshot();
        var bytes = EncryptedDeviceBackup.Encrypt(expected, Password);
        var actual = EncryptedDeviceBackup.Decrypt(bytes, Password);
        Assert.True(DeviceBackupService.Equivalent(expected, actual));
        Assert.Equal(expected.Settings.BlePin, actual.Settings.BlePin);
        Assert.False(Encoding.UTF8.GetString(bytes).Contains("Synthetic slot", StringComparison.Ordinal));
    }

    [Fact]
    public void EachCopyUsesFreshSaltAndNonce()
    {
        var a = EncryptedDeviceBackup.Encrypt(Snapshot(), Password);
        var b = EncryptedDeviceBackup.Encrypt(Snapshot(), Password);
        Assert.False(a.AsSpan(8, 28).SequenceEqual(b.AsSpan(8, 28)));
    }

    [Theory]
    [InlineData(0)] // format
    [InlineData(12)] // salt
    [InlineData(28)] // nonce
    [InlineData(50)] // ciphertext
    [InlineData(-1)] // authentication tag
    public void AnyTamperingIsRejectedBeforeReturningDeviceData(int index)
    {
        var bytes = EncryptedDeviceBackup.Encrypt(Snapshot(), Password);
        bytes[index < 0 ? bytes.Length - 1 : index] ^= 1;
        Assert.Throws<InvalidDataException>(() => EncryptedDeviceBackup.Decrypt(bytes, Password));
    }

    [Fact]
    public void WrongPasswordAndTruncatedFilesAreRejected()
    {
        var bytes = EncryptedDeviceBackup.Encrypt(Snapshot(), Password);
        Assert.Throws<InvalidDataException>(() => EncryptedDeviceBackup.Decrypt(bytes, "incorrect test password"));
        Assert.Throws<InvalidDataException>(() => EncryptedDeviceBackup.Decrypt(bytes[..^1], Password));
        Assert.Throws<InvalidDataException>(() => EncryptedDeviceBackup.Decrypt(new byte[EncryptedDeviceBackup.MaxFileSize + 1], Password));
    }

    [Fact]
    public void MissingAndDuplicateSlotsCannotBecomeAValidBackup()
    {
        var snapshot = Snapshot();
        Assert.Throws<InvalidDataException>(() => EncryptedDeviceBackup.Encrypt(snapshot with { Slots = snapshot.Slots[..7] }, Password));
        snapshot.Slots[7] = snapshot.Slots[0];
        Assert.Throws<InvalidDataException>(() => EncryptedDeviceBackup.Encrypt(snapshot, Password));
    }

    [Fact]
    public void IncompleteCardMemoryCannotBeSavedAsACompleteBackup()
    {
        var snapshot = Snapshot();
        snapshot.Slots[0].Hf!.Card.Data.RemoveAt(0);
        Assert.Throws<InvalidDataException>(() => EncryptedDeviceBackup.Encrypt(snapshot, Password));
    }

    [Fact]
    public void ReadbackComparisonIgnoresLibraryIdsButDetectsAChangedByte()
    {
        var expected = Snapshot(); var actual = Snapshot();
        Assert.True(DeviceBackupService.Equivalent(expected, actual));
        actual.Slots[0].Hf!.Card.Data[0][2] ^= 1;
        Assert.False(DeviceBackupService.Equivalent(expected, actual));
    }

    internal static DeviceBackupSnapshot Snapshot()
    {
        var slots = Enumerable.Range(1, 8).Select(i => new SlotContent(i, null, null, SlotEmulatorSettings.None)).ToArray();
        var card = new CardSave
        {
            Tag = TagSpecificType.Mifare1024, Uid = [1, 2, 3, 4], Atqa = [4, 0], Sak = 8,
            Data = Enumerable.Range(0, 64).Select(i => Enumerable.Repeat((byte)i, 16).ToArray()).ToList(),
        };
        slots[0] = new(1, new(card, "Synthetic slot", "#FF4000", false),
            new(new CardSave { Tag = TagSpecificType.Em410x, Uid = [1, 2, 3, 4, 5] }, "Synthetic LF", null, true),
            new(new(false, true, false, false, EmulatorWriteMode.Normal, MifareClassicPrngType.Weak, true), null),
            new([new(TagSpecificType.Em410x, [5, 4, 3, 2, 1])]));
        return new(1, DateTimeOffset.UtcNow, new DeviceInfo
        {
            Model = DeviceModel.Ultra, ChipId = "0102030405060708", Address = "C00102030405",
            FirmwareVersion = "2.3", GitVersion = "v2.3.15-cl",
        }, 3, DeviceMode.Reader, new(AnimationMode.Minimal, 30, ButtonPressFunction.NextSlot,
            ButtonPressFunction.PrevSlot, ButtonPressFunction.Battery, ButtonPressFunction.None, 500, true, "123456"), slots);
    }
}

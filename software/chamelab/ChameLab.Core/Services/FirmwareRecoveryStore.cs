using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using ChameLab.Localization;
using ChameLab.Models;
using ChameLab.Protocol;
using ChameLab.Transport;

namespace ChameLab.Services;

/// <summary>Only device identity and a public application package; never slot data or keys.</summary>
public sealed record FirmwareRecovery(
    TransportCandidate Candidate, DeviceInfo Device, DfuPackage Package, FirmwareVersion? ExpectedVersion);

public sealed class FirmwareRecoveryStore(IKeyValueStore store)
{
    internal const string Key = "firmware-recovery";

    public void Save(FirmwareUpdateTarget target, DfuPackage package, FirmwareVersion? expectedVersion)
    {
        package.RequireModel(target.Device.Model);
        var record = new FirmwareRecoveryRecord(1, target.Candidate, target.Device, package.Zip.ToArray(), expectedVersion?.ToString());
        Validate(record);
        RequireCompatible(target.Device);
        // Failure must propagate: do not enter DFU if the recovery record could not be saved.
        store.WriteDurable(Key, JsonSerializer.Serialize(record, FirmwareRecoveryJson.Default.FirmwareRecoveryRecord));
    }

    public FirmwareRecovery? Load()
    {
        try
        {
            var json = store.Read(Key);
            if (string.IsNullOrWhiteSpace(json) || json == "null") return null;
            if (json.Length > DfuPackage.MaxZipSize * 2) throw new InvalidDataException();
            var record = JsonSerializer.Deserialize(json, FirmwareRecoveryJson.Default.FirmwareRecoveryRecord)
                         ?? throw new InvalidDataException();
            Validate(record);
            var package = DfuPackage.Parse(record.Zip);
            package.RequireModel(record.Device.Model);
            FirmwareVersion? version = null;
            if (record.ExpectedVersion is { } text)
                version = FirmwareVersion.TryParse(text, out var parsed) ? parsed : throw new InvalidDataException();
            return new(record.Candidate, record.Device, package, version);
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException or ArgumentException)
        {
            // Leave an unreadable record untouched so the user can recover it or explicitly discard it.
            throw new InvalidDataException(Strings.Firmware_RecoveryInvalid, ex);
        }
    }

    public void Clear() => store.Write(Key, "null");

    public void RequireCompatible(DeviceInfo device)
    {
        if (Load() is { } previous && (previous.Device.ChipId != device.ChipId || previous.Device.Model != device.Model))
            throw new InvalidOperationException(Strings.Firmware_RecoveryOtherDevice);
    }

    private static void Validate(FirmwareRecoveryRecord record)
    {
        if (record.Version != 1 || record.Candidate is null || record.Device is null ||
            record.Zip is not { Length: > 0 and <= DfuPackage.MaxZipSize } ||
            record.Candidate.Kind is not (TransportKind.Serial or TransportKind.Ble) ||
            string.IsNullOrWhiteSpace(record.Candidate.Id) || record.Device.ChipId is not { Length: 16 } chip || !System.Linq.Enumerable.All(chip, Uri.IsHexDigit) ||
            record.Device.Model is not (DeviceModel.Ultra or DeviceModel.Lite) ||
            record.Device.Address is not { Length: 12 } address || !System.Linq.Enumerable.All(address, Uri.IsHexDigit))
            throw new InvalidDataException(Strings.Firmware_RecoveryInvalid);
    }
}

internal sealed record FirmwareRecoveryRecord(int Version, TransportCandidate Candidate, DeviceInfo Device, byte[] Zip, string? ExpectedVersion);

[JsonSerializable(typeof(FirmwareRecoveryRecord))]
internal partial class FirmwareRecoveryJson : JsonSerializerContext;

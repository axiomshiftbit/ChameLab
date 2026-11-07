using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using ChameLab.Localization;
using ChameLab.Models;

namespace ChameLab.Protocol;

/// <summary>
/// A bounded, SHA-256-checked application-only nRF5 DFU package. The bootloader still
/// verifies the signature before accepting image data; a checksum is not authentication.
/// </summary>
public sealed class DfuPackage
{
    public const int MaxZipSize = 2 * 1024 * 1024;
    // Application starts at 0x27000; preserve all 22 * 8192 FDS bytes below 0xF3000.
    // Enforce this on the host too: older installed bootloaders reserve only 12 KiB.
    private const int MaxFirmwareSize = 0xA0000;
    public ReadOnlyMemory<byte> InitPacket { get; }
    public ReadOnlyMemory<byte> Firmware { get; }
    public DeviceModel Model { get; }
    public ReadOnlyMemory<byte> Zip { get; }

    private DfuPackage(byte[] init, byte[] firmware, DeviceModel model, byte[] zip) =>
        (InitPacket, Firmware, Model, Zip) = (init, firmware, model, zip);

    public void RequireModel(DeviceModel model)
    {
        if (Model != model) throw new InvalidDataException(Strings.Dfu_WrongModel);
    }

    public static DfuPackage Parse(byte[] zip)
    {
        try
        {
            if (zip.Length is 0 or > MaxZipSize) throw Invalid();
            using var archive = new ZipArchive(new MemoryStream(zip, writable: false), ZipArchiveMode.Read);
            byte[] Read(string name, int limit)
            {
                var entries = archive.Entries.Where(e => e.FullName == name).ToArray();
                if (entries.Length != 1 || entries[0].Length is <= 0 || entries[0].Length > limit)
                    throw Invalid();
                using var stream = entries[0].Open();
                var bytes = new byte[(int)entries[0].Length];
                stream.ReadExactly(bytes);
                if (stream.ReadByte() != -1) throw Invalid();
                return bytes;
            }

            using var manifest = JsonDocument.Parse(Read("manifest.json", 16 * 1024));
            var contents = manifest.RootElement.GetProperty("manifest");
            if (contents.EnumerateObject().Count() != 1 || !contents.TryGetProperty("application", out var app))
                throw new InvalidDataException(Strings.Dfu_AppOnly);
            var init = Read(app.GetProperty("dat_file").GetString()!, 512);
            var firmware = Read(app.GetProperty("bin_file").GetString()!, MaxFirmwareSize);

            // nRF5 SDK dfu-cc.proto: Packet -> SignedCommand -> Command -> InitCommand.
            var packet = Fields(init);
            if (packet.Any(f => f.Number == 1)) throw Invalid(); // unsigned command is not accepted
            var signed = Fields(Bytes(packet, 2));
            if (Integer(signed, 2) != 0 || Bytes(signed, 3).Length != 64) throw Invalid(); // ECDSA P-256
            var command = Fields(Bytes(signed, 1));
            if (Integer(command, 1) != 1) throw Invalid(); // INIT
            var metadata = Fields(Bytes(command, 2));
            var model = Integer(metadata, 2);
            if (model > 1 || Integer(metadata, 4) != 0 || Integer(metadata, 5, 0) != 0 ||
                Integer(metadata, 6, 0) != 0 || Integer(metadata, 9, 0) != 0 ||
                Integer(metadata, 7) != (ulong)firmware.Length)
                throw Invalid(); // application only, no debug bypass, exact size
            // Chameleon uses S140 7.2.0 (0x0100). A wildcard/other SoftDevice must not overwrite it.
            var requirements = metadata.Where(f => f.Number == 3).SelectMany(RepeatedIntegers).ToArray();
            if (requirements.Length == 0 || requirements.Any(v => v != 0x100)) throw Invalid();
            var hash = Fields(Bytes(metadata, 8));
            if (Integer(hash, 1) != 3) throw Invalid(); // SHA256
            var expected = Bytes(hash, 2).ToArray();
            if (expected.Length != 32) throw Invalid();
            Array.Reverse(expected); // nRF5 init packets store the digest in little-endian order.
            if (!CryptographicOperations.FixedTimeEquals(expected, SHA256.HashData(firmware)))
                throw new InvalidDataException(Strings.Dfu_HashMismatch);
            return new DfuPackage(init, firmware, (DeviceModel)model, (byte[])zip.Clone());
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or KeyNotFoundException
                                   or InvalidOperationException or EndOfStreamException)
        {
            throw new InvalidDataException(string.Format(Strings.Dfu_NotPackage, ex.Message), ex);
        }
    }

    private static InvalidDataException Invalid() => new(Strings.Dfu_InvalidPackage);

    // Only the two wire types used by dfu-cc.proto; bounded input, no recursion on unknown fields.
    private sealed record Field(int Number, int Wire, ulong Value, byte[] Data);
    private static List<Field> Fields(ReadOnlySpan<byte> data)
    {
        var fields = new List<Field>();
        var offset = 0;
        while (offset < data.Length)
        {
            var tag = Varint(data, ref offset);
            if (tag >> 3 is 0 or > 0x1FFFFFFF) throw Invalid();
            var wire = (int)(tag & 7);
            var value = Varint(data, ref offset);
            byte[] bytes = [];
            if (wire == 2)
            {
                if (value > (ulong)(data.Length - offset)) throw Invalid();
                bytes = data.Slice(offset, (int)value).ToArray();
                offset += (int)value;
            }
            else if (wire != 0) throw Invalid();
            fields.Add(new Field((int)(tag >> 3), wire, value, bytes));
        }
        return fields;
    }

    private static Field? Single(List<Field> fields, int number)
    {
        var matches = fields.Where(f => f.Number == number).ToArray();
        if (matches.Length > 1) throw Invalid();
        return matches.FirstOrDefault();
    }

    private static ulong Integer(List<Field> fields, int number, ulong? fallback = null) =>
        Single(fields, number) is { Wire: 0 } f ? f.Value :
        Single(fields, number) is null && fallback.HasValue ? fallback.Value : throw Invalid();

    private static byte[] Bytes(List<Field> fields, int number) =>
        Single(fields, number) is { Wire: 2 } f ? f.Data : throw Invalid();

    private static IEnumerable<ulong> RepeatedIntegers(Field f)
    {
        if (f.Wire == 0) return [f.Value];
        var values = new List<ulong>();
        var offset = 0;
        while (offset < f.Data.Length) values.Add(Varint(f.Data, ref offset));
        return values;
    }

    private static ulong Varint(ReadOnlySpan<byte> data, ref int offset)
    {
        ulong result = 0;
        for (var shift = 0; shift < 64; shift += 7)
        {
            if (offset >= data.Length) throw Invalid();
            var b = data[offset++];
            if (shift == 63 && b > 1) throw Invalid();
            result |= (ulong)(b & 127) << shift;
            if ((b & 128) == 0) return result;
        }
        throw Invalid();
    }
}

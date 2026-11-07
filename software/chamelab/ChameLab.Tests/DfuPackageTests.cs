using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using ChameLab.Models;
using ChameLab.Protocol;
using Xunit;

namespace ChameLab.Tests;

public class DfuPackageTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void ApplicationHashAndModelAreChecked(int model)
    {
        var package = DfuPackage.Parse(Zip(model: model));
        Assert.Equal((DeviceModel)model, package.Model);
        package.RequireModel((DeviceModel)model);
        Assert.Throws<InvalidDataException>(() => package.RequireModel((DeviceModel)(1 - model)));
    }

    [Theory]
    [InlineData("corrupt-image")]
    [InlineData("bad-size")]
    [InlineData("wrong-hash-order")]
    [InlineData("full-package")]
    [InlineData("unsigned")]
    [InlineData("debug")]
    [InlineData("softdevice")]
    [InlineData("bootloader-size")]
    [InlineData("wrong-softdevice")]
    [InlineData("unknown-model")]
    [InlineData("duplicate-init-field")]
    [InlineData("duplicate-file")]
    [InlineData("truncated-init")]
    [InlineData("huge-image")]
    public void UnsafePackageIsRejectedBeforeDfu(string fault) =>
        Assert.Throws<InvalidDataException>(() => DfuPackage.Parse(Zip(fault)));

    [Fact]
    public void OversizedOrNonZipInputIsRejected()
    {
        Assert.Throws<InvalidDataException>(() => DfuPackage.Parse(new byte[DfuPackage.MaxZipSize + 1]));
        Assert.Throws<InvalidDataException>(() => DfuPackage.Parse([1, 2, 3]));
    }

    // Synthetic images and a dummy 64-byte signature. No signing key is used in these tests:
    // the client checks integrity; the actual bootloader must authenticate the init packet.
    internal static byte[] Zip(string fault = "", int model = 0)
    {
        var firmware = Enumerable.Range(0, fault == "huge-image" ? 1024 * 1024 + 1 : 117).Select(i => (byte)i).ToArray();
        var hash = SHA256.HashData(firmware);
        if (fault != "wrong-hash-order") Array.Reverse(hash);
        var init = Join(
            Int(1, 1), Int(2, (ulong)(fault == "unknown-model" ? 2 : model)),
            Bytes(3, Varint(fault == "wrong-softdevice" ? 0ul : 0x100ul)),
            Int(4, fault == "softdevice" ? 1ul : 0ul),
            Int(5, 0), Int(6, fault == "bootloader-size" ? 4ul : 0ul),
            Int(7, (ulong)firmware.Length + (fault == "bad-size" ? 1ul : 0ul)),
            Bytes(8, Join(Int(1, 3), Bytes(2, hash))), Int(9, fault == "debug" ? 1ul : 0ul));
        if (fault == "duplicate-init-field") init = Join(init, Int(2, 1));
        var command = Join(Int(1, 1), Bytes(2, init));
        var packet = fault == "unsigned" ? Bytes(1, command) : Bytes(2, Join(Bytes(1, command), Int(2, 0), Bytes(3, new byte[64])));
        if (fault == "truncated-init") packet = packet[..^1];
        if (fault == "corrupt-image") firmware[10] ^= 1;
        var manifest = "{\"manifest\":{\"application\":{\"dat_file\":\"app.dat\",\"bin_file\":\"app.bin\"}" +
                       (fault == "full-package" ? ",\"bootloader\":{}" : "") + "}}";
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string name, byte[] data) { using var entry = archive.CreateEntry(name).Open(); entry.Write(data); }
            Add("manifest.json", Encoding.UTF8.GetBytes(manifest));
            Add("app.dat", packet);
            Add("app.bin", firmware);
            if (fault == "duplicate-file") Add("app.dat", packet);
        }
        return stream.ToArray();
    }

    private static byte[] Join(params byte[][] parts) => parts.SelectMany(p => p).ToArray();
    private static byte[] Int(int number, ulong value) => Join(Varint((ulong)(number << 3)), Varint(value));
    private static byte[] Bytes(int number, byte[] value) => Join(Varint((ulong)(number << 3 | 2)), Varint((ulong)value.Length), value);
    private static byte[] Varint(ulong value)
    {
        var result = new List<byte>();
        do { result.Add((byte)((value & 127) | (value >= 128 ? 128ul : 0ul))); value >>= 7; } while (value != 0);
        return result.ToArray();
    }
}

using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ChameLab.Protocol;

// Protocol and layout ported from GameTec-live/ChameleonUltraGUI (GPL-3.0),
// lib/helpers/mifare_classic/gen4.dart. GTU/Ultimate, not GDM/USCUID.
public sealed class Gen4Configuration
{
    public byte[] Bytes { get; }
    public Gen4Configuration(byte[] bytes)
    {
        if (bytes.Length is not (30 or 32 or 34) || bytes[7] > 16)
            throw new FormatException("Invalid Gen4 configuration.");
        Bytes = bytes[..30];
    }
    public int Protocol => Bytes[0];
    public int UidLength => Bytes[1] switch { 0 => 4, 1 => 7, 2 => 10, _ => 0 };
    public byte[] Password => Bytes[2..6];
    public static int? PackPage(byte[] version) => version.Length is not (5 or 7) || version.Take(3).Any(b => b != 0)
        ? null : (version[3] << 8 | version[4]) is 0x06A0 or 0x6666 ? 0x13 : 0xE6;
}

public sealed class Gen4Client(Func<byte[], CancellationToken, Task<byte[]>> exchange, byte[] password)
{
    private byte[] _password = password.Length == 4 ? password.ToArray() : throw new ArgumentException("Password must be 4 bytes.");

    private Task<byte[]> Command(byte command, byte[] data, CancellationToken ct) =>
        exchange([0xCF, .._password, command, ..data], ct);

    private static void CheckStatus(byte[] response)
    {
        if (response.Length is not (2 or 4) || response[0] != 0x90 || response[1] != 0)
            throw new InvalidOperationException("Gen4 command rejected.");
    }

    public async Task<Gen4Configuration> ReadConfigurationAsync(CancellationToken ct = default) =>
        new(await Command(0xC6, [], ct));
    public Task<byte[]> ReadVersionAsync(CancellationToken ct = default) => Command(0xCC, [], ct);

    public async Task WriteConfigurationAsync(Gen4Configuration config, bool permanent, CancellationToken ct = default)
    {
        CheckStatus(await Command(permanent ? (byte)0xF1 : (byte)0xF0, config.Bytes, ct));
        _password = config.Password;
    }

    public async Task<byte[]> ReadBlockAsync(int block, CancellationToken ct = default)
    {
        if (block is < 0 or > 255) throw new ArgumentOutOfRangeException(nameof(block));
        var data = await Command(0xCE, [(byte)block], ct);
        if (data.Length is not (16 or 18)) throw new FormatException("Expected a 16-byte block.");
        return data[..16];
    }

    public async Task WriteBlockAsync(int block, byte[] data, CancellationToken ct = default)
    {
        if (block is < 0 or > 255 || data.Length != 16) throw new ArgumentException("Invalid Gen4 block.");
        CheckStatus(await Command(0xCD, [(byte)block, ..data], ct));
    }

    private static void ValidateRange(int start, int length, int stride)
    {
        if (stride is not (4 or 16) || start < 0 || length < 1 || start + (length - 1) / stride > 255)
            throw new ArgumentOutOfRangeException(nameof(length));
    }

    public async Task<byte[]> ReadMemoryAsync(int start, int length, int stride, CancellationToken ct = default)
    {
        ValidateRange(start, length, stride);
        var result = new byte[length];
        for (var offset = 0; offset < length; offset += stride)
        {
            var block = await ReadBlockAsync(start + offset / stride, ct);
            block.AsSpan(0, Math.Min(stride, length - offset)).CopyTo(result.AsSpan(offset));
        }
        return result;
    }

    public async Task WriteMemoryAsync(int start, byte[] data, int stride, CancellationToken ct = default)
    {
        ValidateRange(start, data.Length, stride);
        for (var offset = 0; offset < data.Length; offset += stride)
        {
            var block = await ReadBlockAsync(start + offset / stride, ct);
            data.AsSpan(offset, Math.Min(stride, data.Length - offset)).CopyTo(block);
            await WriteBlockAsync(start + offset / stride, block, ct);
        }
    }

    public async Task WriteUidAsync(Gen4Configuration config, byte[] uid, CancellationToken ct = default)
    {
        if (config.Protocol > 1 || config.UidLength == 0 || uid.Length != config.UidLength)
            throw new ArgumentException("UID length does not match the Gen4 configuration.");
        if (config.Protocol == 1 && uid.Length == 7)
        {
            var blocks = new[] { await ReadBlockAsync(0, ct), await ReadBlockAsync(1, ct), await ReadBlockAsync(2, ct) };
            uid.AsSpan(0, 3).CopyTo(blocks[0]);
            blocks[0][3] = (byte)(0x88 ^ uid[0] ^ uid[1] ^ uid[2]);
            uid.AsSpan(3).CopyTo(blocks[1]);
            blocks[2][0] = (byte)(uid[3] ^ uid[4] ^ uid[5] ^ uid[6]);
            for (var i = 0; i < 3; i++) await WriteBlockAsync(i, blocks[i], ct);
        }
        else
        {
            var block = await ReadBlockAsync(0, ct);
            uid.CopyTo(block, 0);
            if (config.Protocol == 0 && uid.Length == 4) block[4] = (byte)(uid[0] ^ uid[1] ^ uid[2] ^ uid[3]);
            await WriteBlockAsync(0, block, ct);
        }
    }
}

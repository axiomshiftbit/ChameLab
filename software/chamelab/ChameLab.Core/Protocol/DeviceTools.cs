using System;
using System.Buffers.Binary;
using System.Threading;
using System.Threading.Tasks;
using ChameLab.Models;

namespace ChameLab.Protocol;

/// <summary>Wire contracts shared by the advanced tools and their protocol tests.</summary>
public static class DeviceTools
{
    public static Task<ChameleonFrame> RawAsync(ChameleonClient client, int command, byte[] data, int timeout,
        CancellationToken ct = default)
    {
        if (command is < 0 or > ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(command));
        if (timeout is < 1 or > 60000) throw new ArgumentOutOfRangeException(nameof(timeout));
        if (data.Length > FrameCodec.MaxDataLength) throw new ArgumentException("Payload exceeds 4096 bytes.");
        return client.Connection.SendAsync((Command)command, data, timeoutMs: timeout, ct: ct);
    }

    public static byte[] HfPayload(byte flags, byte[] data, int bits, int timeout)
    {
        if (timeout is < 1 or > 30000) throw new ArgumentOutOfRangeException(nameof(timeout));
        if (bits < 0 || bits > data.Length * 8 || bits > 64 * 8)
            throw new ArgumentOutOfRangeException(nameof(bits));
        if (data.Length > 64) throw new ArgumentException("HF payload exceeds the 64-byte RF FIFO.");
        if ((flags & 0x20) != 0 && (bits == 0 || bits % 8 != 0 || bits > 62 * 8))
            throw new ArgumentException("CRC requires 1–62 whole bytes.");
        var result = new byte[data.Length + 5];
        result[0] = flags;
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(1), (ushort)timeout);
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(3), (ushort)bits);
        data.CopyTo(result, 5);
        return result;
    }

    public static byte[] T55Payload(int page, int block, byte[] word, byte[]? password)
    {
        if (page is < 0 or > 1 || block < 0 || block > (page == 1 ? 3 : 7))
            throw new ArgumentOutOfRangeException(nameof(block));
        if (word.Length != 4 || password is not null && password.Length != 4)
            throw new ArgumentException("T55xx words and passwords must be 4 bytes.");
        return [(byte)block, ..word, (byte)(password is null ? 0 : 1), ..password ?? new byte[4], (byte)page];
    }

    public static byte[] AuthTracePayload(int block, MfKeyType type, byte[] key, int timeout)
    {
        ValidateKey(block, type, key);
        if (timeout is < 1 or > 30000) throw new ArgumentOutOfRangeException(nameof(timeout));
        return [(byte)type, (byte)block, ..key, (byte)(timeout >> 8), (byte)timeout];
    }

    public static byte[] ValuePayload(int source, MfKeyType sourceType, byte[] sourceKey,
        byte operation, int amount, int destination, MfKeyType destinationType, byte[] destinationKey)
    {
        ValidateKey(source, sourceType, sourceKey);
        ValidateKey(destination, destinationType, destinationKey);
        if (operation is not (0xC0 or 0xC1 or 0xC2)) throw new ArgumentOutOfRangeException(nameof(operation));
        if (amount < 0) throw new ArgumentOutOfRangeException(nameof(amount));
        byte[] result = [(byte)sourceType, (byte)source, ..sourceKey, operation, 0, 0, 0, 0,
            (byte)destinationType, (byte)destination, ..destinationKey];
        BinaryPrimitives.WriteInt32BigEndian(result.AsSpan(9), operation == 0xC2 ? 0 : amount);
        return result;
    }

    private static void ValidateKey(int block, MfKeyType type, byte[] key)
    {
        if (block is < 0 or > 255) throw new ArgumentOutOfRangeException(nameof(block));
        if (key.Length != 6 || (byte)type is not (0x60 or 0x61))
            throw new ArgumentException("Expected a six-byte key and key type A or B.");
    }

    public static byte[] EncodeValue(int value, byte address)
    {
        var result = new byte[16];
        BinaryPrimitives.WriteInt32LittleEndian(result, value);
        for (var i = 0; i < 4; i++) { result[i + 4] = (byte)~result[i]; result[i + 8] = result[i]; }
        result[12] = result[14] = address;
        result[13] = result[15] = (byte)~address;
        return result;
    }

    public static int? DecodeValue(byte[] block)
    {
        if (block.Length != 16) return null;
        for (var i = 0; i < 4; i++)
            if (block[i] != block[i + 8] || block[i + 4] != (byte)~block[i]) return null;
        if (block[12] != block[14] || block[13] != block[15] || block[13] != (byte)~block[12]) return null;
        return BinaryPrimitives.ReadInt32LittleEndian(block);
    }

    // C1 is bit 0, C2 bit 1, C3 bit 2; columns: read, write, increment, decrement/transfer.
    private static readonly int[][] Permissions =
        [[3,3,3,3], [3,2,0,0], [3,0,0,0], [3,2,2,3], [3,0,0,3], [2,0,0,0], [2,2,0,0], [0,0,0,0]];

    public static int AccessMask(byte[] trailer, int block, int permission)
    {
        if (!MifareClassic.AccessBitsValid(trailer)) return 0;
        var sector = MifareClassic.SectorOfBlock(block);
        var group = block - MifareClassic.SectorFirstBlock(sector);
        if (sector >= 32) group /= 5;
        if (group > 2 || permission is < 0 or > 3) return 0;
        var condition = ((trailer[7] >> (4 + group)) & 1)
            | (((trailer[8] >> group) & 1) << 1) | (((trailer[8] >> (4 + group)) & 1) << 2);
        return Permissions[condition][permission];
    }
}

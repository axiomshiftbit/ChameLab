using System;

namespace ChameLab.Models;

[Flags]
public enum MifareAccessKey { None = 0, A = 1, B = 2, AOrB = A | B }

public sealed record MifareDataAccess(byte Condition, MifareAccessKey Read, MifareAccessKey Write,
    MifareAccessKey Increment, MifareAccessKey DecrementTransferRestore);

public sealed record MifareTrailerAccess(byte Condition, MifareAccessKey WriteKeyA,
    MifareAccessKey ReadAccess, MifareAccessKey WriteAccess, MifareAccessKey ReadKeyB, MifareAccessKey WriteKeyB);

/// <summary>
/// NXP MF1S70YYX_V1 tables 6–8. Conditions use C1 C2 C3 (C1 is the most significant bit).
/// These describe normal Classic authentication, not magic-card backdoors or possession of keys.
/// </summary>
public static class MifareAccess
{
    public static MifareDataAccess? DataBlock(ReadOnlySpan<byte> trailer, int block)
    {
        if (block is < 0 or > 255 || MifareClassic.IsTrailer(block))
            throw new ArgumentOutOfRangeException(nameof(block));
        if (!MifareClassic.AccessBitsValid(trailer)) return null;
        var group = block < 128 ? block % 4 : block % 16 / 5;
        var condition = Condition(trailer, group);
        const MifareAccessKey n = MifareAccessKey.None, b = MifareAccessKey.B, ab = MifareAccessKey.AOrB;
        var (read, write, increment, decrement) = condition switch
        {
            0b000 => (ab, ab, ab, ab),
            0b001 => (ab, n, n, ab),
            0b010 => (ab, n, n, n),
            0b011 => (b, b, n, n),
            0b100 => (ab, b, n, n),
            0b101 => (b, n, n, n),
            0b110 => (ab, b, b, ab),
            _ => (n, n, n, n),
        };
        // A readable key B is data: it cannot authenticate ANY block in this sector.
        var usable = MifareClassic.KeyBReadable(trailer) ? MifareAccessKey.A : ab;
        // The original manufacturer's block remains read-only, regardless of access bits.
        if (block == 0) write = increment = decrement = n;
        return new(condition, read & usable, write & usable, increment & usable, decrement & usable);
    }

    public static MifareTrailerAccess? Trailer(ReadOnlySpan<byte> trailer)
    {
        if (!MifareClassic.AccessBitsValid(trailer)) return null;
        var condition = Condition(trailer, 3);
        const MifareAccessKey n = MifareAccessKey.None, a = MifareAccessKey.A,
            b = MifareAccessKey.B, ab = MifareAccessKey.AOrB;
        // Key A is never readable. The remaining columns are write A, read/write ACL, read/write B.
        var (writeA, readAccess, writeAccess, readB, writeB) = condition switch
        {
            0b000 => (a, a, n, a, a),
            0b001 => (a, a, a, a, a),
            0b010 => (n, a, n, a, n),
            0b011 => (b, ab, b, n, b),
            0b100 => (b, ab, n, n, b),
            0b101 => (n, ab, b, n, n),
            _ => (n, ab, n, n, n),
        };
        return new(condition, writeA, readAccess, writeAccess, readB, writeB);
    }

    private static byte Condition(ReadOnlySpan<byte> trailer, int group) =>
        (byte)((((trailer[7] >> (4 + group)) & 1) << 2)
               | (((trailer[8] >> group) & 1) << 1)
               | ((trailer[8] >> (4 + group)) & 1));
}

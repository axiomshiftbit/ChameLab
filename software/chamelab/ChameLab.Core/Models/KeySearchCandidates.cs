using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;

namespace ChameLab.Models;

/// <summary>A stable dictionary snapshot or a lazy, inclusive big-endian key range.</summary>
public sealed class KeySearchCandidates
{
    private readonly byte[][]? _keys;
    private readonly BigInteger _first;
    public int KeySize { get; }
    public ulong Count { get; }
    private KeySearchCandidates(byte[][] keys) { _keys = keys; KeySize = keys[0].Length; Count = (ulong)keys.Length; }
    private KeySearchCandidates(byte[] first, byte[] last)
    {
        KeySize = first.Length; _first = new BigInteger(first, isUnsigned: true, isBigEndian: true);
        var count = new BigInteger(last, isUnsigned: true, isBigEndian: true) - _first + 1;
        if (count <= 0 || count > ulong.MaxValue) throw new ArgumentException("Choose an ascending range of at most 2^64−1 candidates.");
        Count = (ulong)count;
    }
    public static KeySearchCandidates Dictionary(IEnumerable<byte[]> keys, int size)
    {
        if (!KeyDictionary.IsSupportedSize(size)) throw new ArgumentOutOfRangeException(nameof(size));
        var snapshot = keys.Select(k => k.Length == size ? k.ToArray() : throw new ArgumentException("Wrong key size."))
            .DistinctBy(Convert.ToHexString).ToArray();
        if (snapshot.Length == 0) throw new ArgumentException("The dictionary is empty.");
        return new(snapshot);
    }
    public static KeySearchCandidates Range(byte[] first, byte[] last)
    {
        if (!KeyDictionary.IsSupportedSize(first.Length) || last.Length != first.Length) throw new ArgumentException("Wrong key size.");
        return new(first, last);
    }
    public byte[] At(ulong index)
    {
        if (index >= Count) throw new ArgumentOutOfRangeException(nameof(index));
        if (_keys is not null) return _keys[(int)index].ToArray();
        var bytes = (_first + index).ToByteArray(isUnsigned: true, isBigEndian: true);
        var result = new byte[KeySize]; bytes.CopyTo(result, KeySize - bytes.Length); return result;
    }
}

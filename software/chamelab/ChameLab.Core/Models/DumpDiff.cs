using System;
using System.Collections.Generic;
using System.Linq;
using ChameLab.Protocol;

namespace ChameLab.Models;

/// <summary>
/// One block (MIFARE Classic) or page (Ultralight / NTAG) of two dumps side by side. A side is
/// null where that dump doesn't have the block: unread, or beyond its end.
/// </summary>
public enum DumpByteState { Equal, Different, Unknown }

public sealed record DumpBlockDiff(int Index, byte[]? A, byte[]? B)
{
    public DumpByteState[] States { get; init; } = [];
    public bool Differs => DifferentBytes > 0 || UnknownBytes > 0;
    public bool ByteDiffers(int i) => i < States.Length && States[i] == DumpByteState.Different;
    public int DifferentBytes => States.Count(s => s == DumpByteState.Different);
    public int UnknownBytes => States.Count(s => s == DumpByteState.Unknown);
}

/// <summary>The card fields outside the memory that are compared.</summary>
public enum DumpField { Type, Uid, Sak, Atqa, Ats }

/// <summary>A card field outside the memory that differs between the two cards, as shown.</summary>
public sealed record DumpFieldDiff(DumpField Field, string A, string B);

/// <summary>The memory and identity of two cards compared block by block.</summary>
public sealed class DumpComparison
{
    private DumpComparison(IReadOnlyList<DumpBlockDiff> blocks, IReadOnlyList<DumpFieldDiff> fields)
    {
        Blocks = blocks;
        Fields = fields;
    }

    /// <summary>Every block of the longer dump, in order.</summary>
    public IReadOnlyList<DumpBlockDiff> Blocks { get; }

    /// <summary>Identity fields that differ (type, UID, SAK, ATQA, ATS), in that order.</summary>
    public IReadOnlyList<DumpFieldDiff> Fields { get; }

    public int DifferentBlocks => Blocks.Count(b => b.Differs);

    public int DifferentBytes => Blocks.Sum(b => b.DifferentBytes);
    public int UnknownBytes => Blocks.Sum(b => b.UnknownBytes);

    /// <summary>Neither the memory nor the identity differs.</summary>
    public bool IsSame => DifferentBlocks == 0 && Fields.Count == 0;

    /// <summary>
    /// Compares <paramref name="a"/> with <paramref name="b"/>. An empty block (unread) counts as
    /// missing, so a block read in one dump and not in the other shows as a difference.
    /// </summary>
    public static DumpComparison Compare(CardSave a, CardSave b)
    {
        if (!CanCompare(a, b)) throw new ArgumentException("Incompatible dump types.");
        var width = a.Tag.IsMifareClassic() ? 16 : 4;
        var count = Math.Max(a.Tag.IsMifareClassic() ? a.Tag.MifareClassicBlocks() : a.Data.Count,
            b.Tag.IsMifareClassic() ? b.Tag.MifareClassicBlocks() : b.Data.Count);
        var blocks = new DumpBlockDiff[count];
        for (var i = 0; i < count; i++)
        {
            var states = Enumerable.Range(0, width).Select(col =>
                !MifareDumpComparison.Known(a, i, col) || !MifareDumpComparison.Known(b, i, col)
                    ? DumpByteState.Unknown : a.Data[i][col] == b.Data[i][col] ? DumpByteState.Equal : DumpByteState.Different).ToArray();
            blocks[i] = new DumpBlockDiff(i, Block(a, i), Block(b, i)) { States = states };
        }

        var fields = new List<DumpFieldDiff>();
        if (a.Tag != b.Tag) fields.Add(new DumpFieldDiff(DumpField.Type, a.Tag.DisplayName(), b.Tag.DisplayName()));
        AddBytes(fields, DumpField.Uid, a.Uid, b.Uid);
        if (a.Sak != b.Sak) fields.Add(new DumpFieldDiff(DumpField.Sak, a.Sak.ToString("X2"), b.Sak.ToString("X2")));
        AddBytes(fields, DumpField.Atqa, a.Atqa, b.Atqa);
        AddBytes(fields, DumpField.Ats, a.Ats, b.Ats);
        return new DumpComparison(blocks, fields);
    }

    private static byte[]? Block(CardSave card, int i) =>
        i < card.Data.Count && card.Data[i].Length > 0 ? card.Data[i] : null;

    private static void AddBytes(List<DumpFieldDiff> fields, DumpField name, byte[] a, byte[] b)
    {
        if (!a.AsSpan().SequenceEqual(b)) fields.Add(new DumpFieldDiff(name, Show(a), Show(b)));
    }

    private static string Show(byte[] bytes) => bytes.Length == 0 ? "-" : Hex.Spaced(bytes);

    /// <summary>The two kinds of memory a card can have are only compared with the same kind.</summary>
    public static bool CanCompare(CardSave a, CardSave b) =>
        (a.Tag.IsMifareClassic() && b.Tag.IsMifareClassic()) || (a.Tag.IsUltralight() && b.Tag.IsUltralight());
}

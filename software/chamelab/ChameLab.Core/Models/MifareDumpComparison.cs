using System;
using System.Collections.Generic;
using System.Linq;
using ChameLab.Protocol;

namespace ChameLab.Models;

public sealed record MifareBlockDifference(int Block, int DifferentBytes, int UnknownBytes);

/// <summary>Comparison metadata only: never carries memory contents or key values into reports.</summary>
public sealed record MifareDumpComparison(bool IdentityMatches, int EqualBytes, int DifferentBytes,
    int UnknownBytes, IReadOnlyList<MifareBlockDifference> Blocks)
{
    public static bool CanCompare(CardSave left, CardSave right) =>
        left.Tag.IsMifareClassic() && left.Tag == right.Tag;

    public static MifareDumpComparison Compare(CardSave left, CardSave right)
    {
        if (!CanCompare(left, right)) throw new ArgumentException("Matching MIFARE Classic capacities are required.");
        var equal = 0; var different = 0; var unknown = 0;
        var differences = new List<MifareBlockDifference>();
        for (var block = 0; block < left.Tag.MifareClassicBlocks(); block++)
        {
            var blockDifferent = 0; var blockUnknown = 0;
            for (var column = 0; column < 16; column++)
            {
                if (!Known(left, block, column) || !Known(right, block, column)) blockUnknown++;
                else if (left.Data[block][column] != right.Data[block][column]) blockDifferent++;
                else equal++;
            }
            different += blockDifferent; unknown += blockUnknown;
            if (blockDifferent > 0 || blockUnknown > 0) differences.Add(new(block, blockDifferent, blockUnknown));
        }
        return new(left.Uid.SequenceEqual(right.Uid) && left.Sak == right.Sak
            && left.Atqa.SequenceEqual(right.Atqa) && left.Ats.SequenceEqual(right.Ats),
            equal, different, unknown, differences);
    }

    private static bool Known(CardSave card, int block, int column)
    {
        if (block >= card.Data.Count || card.Data[block].Length != 16) return false;
        if (!MifareClassic.IsTrailer(block)) return true;
        var sector = MifareClassic.SectorOfBlock(block);
        return column switch
        {
            < 6 => !card.Extra.UnknownKeyA.Contains(sector),
            >= 10 => !card.Extra.UnknownKeyB.Contains(sector),
            _ => true,
        };
    }
}

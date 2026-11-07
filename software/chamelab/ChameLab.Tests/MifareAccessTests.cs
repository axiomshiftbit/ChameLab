using System;
using ChameLab.Models;
using Xunit;

namespace ChameLab.Tests;

public class MifareAccessTests
{
    // NXP table 8 in C1 C2 C3 order; 1=A, 2=B, 3=A or B, 0=never.
    [Theory]
    [InlineData(0, 3, 3, 3, 3)]
    [InlineData(1, 3, 0, 0, 3)]
    [InlineData(2, 3, 0, 0, 0)]
    [InlineData(3, 2, 2, 0, 0)]
    [InlineData(4, 3, 2, 0, 0)]
    [InlineData(5, 2, 0, 0, 0)]
    [InlineData(6, 3, 2, 2, 3)]
    [InlineData(7, 0, 0, 0, 0)]
    public void DataPermissionsRespectAllConditionsAndReadableKeyB(int condition, int read, int write, int increment, int decrement)
    {
        for (var trailerCondition = 0; trailerCondition < 8; trailerCondition++)
        {
            var trailer = Encode(condition, condition, condition, trailerCondition);
            var mask = trailerCondition is 0 or 1 or 2 ? 1 : 3;
            for (var block = 0; block < 256; block++)
            {
                if (MifareClassic.IsTrailer(block)) continue;
                var access = MifareAccess.DataBlock(trailer, block)!;
                Assert.Equal(condition, access.Condition);
                Assert.Equal((MifareAccessKey)(read & mask), access.Read);
                Assert.Equal((MifareAccessKey)(block == 0 ? 0 : write & mask), access.Write);
                Assert.Equal((MifareAccessKey)(block == 0 ? 0 : increment & mask), access.Increment);
                Assert.Equal((MifareAccessKey)(block == 0 ? 0 : decrement & mask), access.DecrementTransferRestore);
            }
        }
    }

    // NXP table 7: write A, read/write access bits, read/write B. Key A is never readable.
    [Theory]
    [InlineData(0, 1, 1, 0, 1, 1)]
    [InlineData(1, 1, 1, 1, 1, 1)]
    [InlineData(2, 0, 1, 0, 1, 0)]
    [InlineData(3, 2, 3, 2, 0, 2)]
    [InlineData(4, 2, 3, 0, 0, 2)]
    [InlineData(5, 0, 3, 2, 0, 0)]
    [InlineData(6, 0, 3, 0, 0, 0)]
    [InlineData(7, 0, 3, 0, 0, 0)]
    public void TrailerPermissions(int condition, int wa, int ra, int wac, int rb, int wb)
    {
        Assert.Equal(new MifareTrailerAccess((byte)condition, (MifareAccessKey)wa, (MifareAccessKey)ra,
            (MifareAccessKey)wac, (MifareAccessKey)rb, (MifareAccessKey)wb),
            MifareAccess.Trailer(Encode(0, 0, 0, condition)));
    }

    [Fact]
    public void EveryBlockUsesItsOwnAccessGroup()
    {
        for (var config = 0; config < 4096; config++)
        {
            int[] conditions = [config & 7, (config >> 3) & 7, (config >> 6) & 7, (config >> 9) & 7];
            var trailer = Encode(conditions);
            for (var block = 0; block < 256; block++)
            {
                if (MifareClassic.IsTrailer(block)) continue;
                var group = block < 128 ? block % 4 : (block % 16) switch { < 5 => 0, < 10 => 1, _ => 2 };
                Assert.Equal(conditions[group], MifareAccess.DataBlock(trailer, block)!.Condition);
            }
        }
    }

    [Fact]
    public void InvalidOrUnreadTrailerNeverInventsPermissions()
    {
        Assert.Null(MifareAccess.Trailer([]));
        Assert.Null(MifareAccess.DataBlock([], 1));
        for (var bit = 0; bit < 24; bit++)
        {
            var trailer = Encode(0, 1, 2, 3);
            trailer[6 + bit / 8] ^= (byte)(1 << (bit % 8));
            Assert.Null(MifareAccess.Trailer(trailer));
            Assert.Null(MifareAccess.DataBlock(trailer, 129));
        }
        Assert.Throws<ArgumentOutOfRangeException>(() => MifareAccess.DataBlock(new byte[16], 256));
        Assert.Throws<ArgumentOutOfRangeException>(() => MifareAccess.DataBlock(new byte[16], 143));
    }

    private static byte[] Encode(params int[] conditions)
    {
        var trailer = new byte[16];
        var c1 = 0; var c2 = 0; var c3 = 0;
        for (var group = 0; group < 4; group++)
        {
            c1 |= ((conditions[group] >> 2) & 1) << group;
            c2 |= ((conditions[group] >> 1) & 1) << group;
            c3 |= (conditions[group] & 1) << group;
        }
        trailer[6] = (byte)(((~c2 & 15) << 4) | (~c1 & 15));
        trailer[7] = (byte)((c1 << 4) | (~c3 & 15));
        trailer[8] = (byte)((c3 << 4) | c2);
        return trailer;
    }
}

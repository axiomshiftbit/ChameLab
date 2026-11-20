using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using ChameLab.Models;
using ChameLab.Protocol;
using Xunit;

namespace ChameLab.Tests;

public class QrBoundaryTests
{
    [Fact]
    public void OfflineSharingStripsAllLocalIdentityAndKeepsUnknownKeys()
    {
        var card = new CardSave { Name = "Synthetic", Tag = TagSpecificType.Mifare1024,
            FolderId = "local-only", Folder = "Local", Uid = [1, 2, 3, 4], Data = [new byte[16]] };
        card.Extra.UnknownKeyA.Add(1); card.Extra.UnknownKeyB.Add(2);
        var collector = new CardQrCollector();
        foreach (var part in CardQr.Encode(card)) { Assert.StartsWith("#card=", part); collector.Add(part); }
        Assert.NotEqual(card.Id, collector.Card!.Id);
        Assert.Null(collector.Card.FolderId); Assert.Equal("", collector.Card.Folder);
        Assert.Equal(card.Extra.UnknownKeyA, collector.Card.Extra.UnknownKeyA);
        Assert.Equal(card.Extra.UnknownKeyB, collector.Card.Extra.UnknownKeyB);
    }

    [Fact]
    public void ExcessivePartsLinesDimensionsAndEmptyAssemblyAreRejected()
    {
        var collector = new CardQrCollector();
        Assert.Throws<FormatException>(() => collector.Add(new CardQrPart(1, int.MaxValue, "12345678", "AA")));
        Assert.Equal(0, collector.Received);
        Assert.False(CardQr.TryParse("#card=1.1.1.12345678." + new string('A', 601), out _));
        Assert.Throws<FormatException>(() => CardQr.Assemble([]));
        Assert.Throws<ArgumentException>(() => QrCodes.Luminance([], int.MaxValue, int.MaxValue, true));
        Assert.Throws<ArgumentOutOfRangeException>(() => CardQr.Encode(new CardSave(), maxPartChars: 0));
    }

    [Fact]
    public void CompressedBombDeepJsonAndExcessiveBlocksDoNotProduceACard()
    {
        Assert.Throws<FormatException>(() => CardQr.Unpack(Compress(new string(' ', CardQr.MaxJsonBytes + 1))));
        Assert.Throws<FormatException>(() => CardQr.Unpack(Compress(new string('[', 17) + "0" + new string(']', 17))));
        Assert.Throws<FormatException>(() => CardQr.Unpack(Compress("{\"data\":[" + string.Join(',', Enumerable.Repeat("\"\"", CardQr.MaxBlocks + 1)) + "]}")));
    }

    [Fact]
    public void CorruptCompleteSeriesDoesNotWriteOrReturnAPartialCard()
    {
        var payload = Compress("invalid json");
        var id = Convert.ToHexString(SHA256.HashData(payload), 0, 4).ToLowerInvariant();
        var part = new CardQrPart(1, 1, id, Convert.ToBase64String(payload).TrimEnd('=').Replace('+', '-').Replace('/', '_'));
        var collector = new CardQrCollector();
        Assert.Throws<FormatException>(() => collector.Add(part));
        Assert.Null(collector.Card);
    }
    private static byte[] Compress(string text)
    {
        using var output = new MemoryStream();
        using (var writer = new DeflateStream(output, CompressionLevel.SmallestSize, true)) writer.Write(Encoding.UTF8.GetBytes(text));
        return output.ToArray();
    }
}

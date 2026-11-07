using System;
using System.Linq;
using ChameLab.Models;
using ChameLab.Protocol;
using ChameLab.Services;
using ChameLab.ViewModels;
using Xunit;

namespace ChameLab.Tests;

public class MifareDumpComparisonTests
{
    private static CardSave Card(TagSpecificType tag = TagSpecificType.Mifare4096) => new()
    {
        Name = "Synthetic comparison", Tag = tag, Uid = [1, 2, 3, 4], Sak = 8, Atqa = [4, 0],
        Data = Enumerable.Range(0, tag.MifareClassicBlocks()).Select(MifareClassic.BlankBlock).ToList(),
    };

    [Theory]
    [InlineData(TagSpecificType.MifareMini, 320)]
    [InlineData(TagSpecificType.Mifare1024, 1024)]
    [InlineData(TagSpecificType.Mifare2048, 2048)]
    [InlineData(TagSpecificType.Mifare4096, 4096)]
    public void IdenticalKnownDumpsHaveNoDifferences(TagSpecificType tag, int bytes)
    {
        var source = Card(tag);
        var result = MifareDumpComparison.Compare(source, source.Clone());
        Assert.True(result.IdentityMatches);
        Assert.Equal(bytes, result.EqualBytes);
        Assert.Equal(0, result.DifferentBytes);
        Assert.Equal(0, result.UnknownBytes);
        Assert.Empty(result.Blocks);
    }

    [Fact]
    public void UnknownKeysAndUnreadBlocksNeverCountAsEqualOrDifferent()
    {
        var source = Card();
        var target = source.Clone();
        target.Data[129][15] = 17;
        target.Data[130] = [];
        source.Extra.UnknownKeyA.Add(32);
        target.Extra.UnknownKeyB.Add(32);
        target.Data[143][0] = 11; // Still unknown on the source side.
        target.Data[143][10] = 22; // Still unknown on the target side.
        target.Data[143][9] = 33; // GPB remains known and compared.
        target.Uid = [5, 6, 7, 8];
        var result = MifareDumpComparison.Compare(source, target);
        Assert.False(result.IdentityMatches);
        Assert.Equal(28, result.UnknownBytes);
        Assert.Equal(2, result.DifferentBytes);
        Assert.Equal(4096 - 30, result.EqualBytes);
        Assert.Equal(new[] { new MifareBlockDifference(129, 1, 0), new(130, 0, 16), new(143, 1, 12) }, result.Blocks);
        Assert.Equal(0, source.Data[129][15]);
    }

    [Fact]
    public void MissingAndMalformedRowsAreUnknownAndMismatchedTypesRejected()
    {
        var source = Card(); var target = Card();
        target.Data = [new byte[15]];
        Assert.Equal(4096, MifareDumpComparison.Compare(source, target).UnknownBytes);
        target.Tag = TagSpecificType.Mifare1024;
        Assert.False(MifareDumpComparison.CanCompare(source, target));
        Assert.Throws<ArgumentException>(() => MifareDumpComparison.Compare(source, target));
    }

    [Fact]
    public void EditorClearsStaleComparisonButKeepsItWhenBrowsingSectors()
    {
        var store = new MemoryKeyValueStore();
        var library = new CardLibrary(store);
        var source = Card(); library.Add(source);
        var vm = new CardsViewModel(new DeviceService([]), library, new DictionaryLibrary(store));
        vm.OpenCard(source.Id);
        vm.CompareWithCard(source.Clone());
        Assert.NotNull(vm.ComparisonSummary);
        Assert.Empty(vm.ComparisonRows);
        vm.Form!.SelectedSector = 32;
        Assert.NotNull(vm.ComparisonSummary);
        vm.Form.SelectedMemory!.Write(0, [1]);
        Assert.Null(vm.ComparisonSummary);
        vm.CompareWithCard(source.Clone());
        Assert.Single(vm.ComparisonRows);
        Assert.Equal(0, source.Data[128][0]);
        vm.Form.Uid = "05 06 07 08";
        Assert.Null(vm.ComparisonSummary);
        Assert.Empty(vm.ComparisonRows);
    }
}

using System;
using System.Linq;
using ChameLab.Controls;
using ChameLab.Models;
using ChameLab.Protocol;
using ChameLab.ViewModels;
using Xunit;

namespace ChameLab.Tests;

public class HexNavigationTests
{
    [Fact]
    public void SearchSkipsUnreadAndTemplateBytesAndWrapsAcrossRows()
    {
        var buffer = new HexBuffer([new byte[] { 1, 2, 3, 4 }, Array.Empty<byte>(), new byte[] { 3, 4, 1, 2 }], 4)
        { Template = _ => new byte[] { 0, 0, 0, 0 } };
        Assert.Equal(2, buffer.Find([3, 4]));
        Assert.Equal(8, buffer.Find([3, 4], 3));
        Assert.Equal(2, buffer.Find([3, 4], 9));
        Assert.Equal(-1, buffer.Find([4, 0]));
        Assert.Equal(-1, buffer.Find([0, 0]));
        Assert.Equal(-1, buffer.Find([]));
        Assert.Equal(-1, buffer.Find(new byte[13]));
        var known = new HexBuffer([new byte[] { 1, 2 }, new byte[] { 3, 4 }], 2) { CanResize = true };
        Assert.Equal(1, known.Find([2, 3]));
        Assert.Equal(-1, known.Find([4, 0])); // The append row is not memory.
        Assert.Equal(-1, known.Slice(1, 1).Find([2, 3]));
    }

    [Fact]
    public void WholeMemoryViewSharesEditsAndClearingStillTargetsOneSector()
    {
        var card = new CardSave { Tag = TagSpecificType.Mifare4096,
            Data = Enumerable.Range(0, 256).Select(MifareClassic.BlankBlock).ToList() };
        var editor = new CardEditorViewModel(card) { SelectedSector = 32, ShowAllMemory = true };
        Assert.Same(editor.Memory, editor.SelectedMemory);
        Assert.Equal(256, editor.SelectedMemory!.RowCount);
        editor.SelectedMemory.Write(129 * 16, [0x42]);
        editor.ClearSectorCommand.Execute(null);
        Assert.False(editor.Memory!.IsRead(128));
        Assert.False(editor.Memory.IsRead(143));
        Assert.True(editor.Memory.IsRead(127));
        Assert.True(editor.Memory.IsRead(144));
        editor.Memory.Undo();
        Assert.Equal(0x42, editor.Memory[129L * 16]);
        editor.SelectedSector = 32; // Selecting even the same sector leaves the full view.
        Assert.False(editor.ShowAllMemory);
        Assert.Equal(128 * 16, editor.SelectedMemory.ByteOffset);
        Assert.Equal(16, editor.SelectedMemory.RowCount);
        Assert.Equal(0x42, editor.SelectedMemory[16L]);
        Assert.Equal(0, card.Data[129][0]);
    }
}

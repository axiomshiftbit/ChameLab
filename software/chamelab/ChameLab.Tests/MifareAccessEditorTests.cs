using System.Linq;
using ChameLab.Localization;
using ChameLab.Models;
using ChameLab.Protocol;
using ChameLab.ViewModels;
using Xunit;

namespace ChameLab.Tests;

public class MifareAccessEditorTests
{
    [Fact]
    public void AccessViewTracksSectorEditsUndoAndUnreadDataWithoutChangingCard()
    {
        var source = new CardSave { Tag = TagSpecificType.Mifare4096, Uid = [1, 2, 3, 4],
            Data = Enumerable.Range(0, 256).Select(MifareClassic.BlankBlock).ToList() };
        var editor = new CardEditorViewModel(source);
        Assert.False(editor.IsDirty);
        Assert.False(editor.AccessUnavailable);
        Assert.Equal(4, editor.AccessRows.Count);
        Assert.Contains(Strings.Cards_AclReadableB, editor.AccessStatus);

        editor.SelectedSector = 32;
        Assert.Contains("128–132", editor.AccessRows[0].Field);
        Assert.Contains("133–137", editor.AccessRows[1].Field);
        Assert.Contains("138–142", editor.AccessRows[2].Field);
        Assert.Contains("143", editor.AccessRows[3].Field);
        Assert.False(editor.IsDirty);

        editor.SelectedMemory!.Write(15 * 16 + 6, [0]);
        Assert.True(editor.AccessUnavailable);
        Assert.Empty(editor.AccessRows);
        Assert.Equal(Strings.Cards_AclInvalid, editor.AccessStatus);
        Assert.Equal(0xff, source.Data[143][6]);

        editor.SelectedMemory.Undo();
        Assert.False(editor.AccessUnavailable);
        Assert.Equal(4, editor.AccessRows.Count);
        editor.SelectedMemory.ClearRows(15, 1);
        Assert.True(editor.AccessUnavailable);
        Assert.Equal(Strings.Cards_AclUnread, editor.AccessStatus);
        Assert.Empty(editor.AccessRows);
        editor.SelectedSector = 31;
        Assert.False(editor.AccessUnavailable);
        Assert.Contains("124", editor.AccessRows[0].Field);
    }

    [Fact]
    public void IdentityOnlyCardDoesNotUseBlankTemplateAsReadAccessBits()
    {
        var editor = new CardEditorViewModel(new CardSave { Tag = TagSpecificType.Mifare1024 });
        Assert.True(editor.AccessUnavailable);
        Assert.Equal(Strings.Cards_AclUnread, editor.AccessStatus);
        Assert.Empty(editor.AccessRows);
        editor.Tag = TagSpecificType.Ntag213;
        Assert.Empty(editor.AccessRows);
        Assert.Empty(editor.AccessStatus);
    }
}

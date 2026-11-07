using System.Collections.Generic;
using ChameLab.Models;
using ChameLab.Protocol;
using ChameLab.ViewModels;
using Xunit;

namespace ChameLab.Tests;

public class SlotRefreshTests
{
    private static SlotInfo Snapshot() => new()
    {
        Index = 1, HfType = TagSpecificType.Mifare1024, HfEnabled = true,
        LfType = TagSpecificType.Em410x, LfEnabled = true,
        LfCycle = new([new(TagSpecificType.Em410x, [1, 2, 3, 4, 5])]),
    };

    [Fact]
    public void IdenticalReadbackDoesNotInvalidateSlotBindings()
    {
        var row = new SlotItemViewModel(Snapshot());
        var changes = new List<string?>();
        row.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        row.Update(Snapshot());
        Assert.Empty(changes);
        var active = Snapshot(); active.IsActive = true;
        row.Update(active);
        Assert.Equal(new[] { nameof(row.IsActive) }, changes);
    }

    [Fact]
    public void ChangedLfCardAndNicknameStillRefreshDisplayedContents()
    {
        var row = new SlotItemViewModel(Snapshot());
        var changes = new List<string?>();
        row.PropertyChanged += (_, e) => changes.Add(e.PropertyName);
        var updated = Snapshot(); updated.LfCycle!.Extras[0].Id[4] = 6; updated.HfNick = "Updated";
        row.Update(updated);
        Assert.Contains(nameof(row.LfNames), changes);
        Assert.Contains(nameof(row.HfLabel), changes);
        Assert.Equal("Updated", row.HfLabel);
        Assert.Same(updated, row.Info);
    }
}

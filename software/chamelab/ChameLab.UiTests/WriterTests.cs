using Avalonia.Headless.XUnit;
using ChameLab.Models;
using ChameLab.Protocol;
using ChameLab.Services;
using ChameLab.ViewModels;
using Xunit;

namespace ChameLab.UiTests;

public class WriterTests
{
    [AvaloniaFact]
    public void SwitchingWriterSourceChangesAcceptedAuthenticationKeyLength()
    {
        var store = new MemoryKeyValueStore();
        var writer = new WriterViewModel(new DeviceService([]), new CardLibrary(store), new DictionaryLibrary(store));
        var lengthsChanged = 0;
        writer.Engine.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(CardsViewModel.WritePasswordLengths)) lengthsChanged++;
        };
        writer.Source = new CardSave { Tag = TagSpecificType.Mf0Icu2, Uid = [1,2,3,4] };
        writer.Engine.WritePassword = new string('1', 32);
        Assert.Equal("16", writer.Engine.WritePasswordLengths);
        Assert.Null(writer.Engine.WritePasswordError);
        writer.Engine.WritePassword = new string('1', 8);
        Assert.NotNull(writer.Engine.WritePasswordError);

        writer.Source = new CardSave { Tag = TagSpecificType.Ntag213, Uid = [1,2,3,4] };
        writer.Engine.WritePassword = new string('1', 8);
        Assert.Equal("4", writer.Engine.WritePasswordLengths);
        Assert.Null(writer.Engine.WritePasswordError);
        writer.Engine.WritePassword = new string('1', 32);
        Assert.NotNull(writer.Engine.WritePasswordError);
        Assert.True(lengthsChanged >= 2);
    }
}

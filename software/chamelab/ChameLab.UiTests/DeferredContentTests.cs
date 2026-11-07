using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ChameLab.Controls;
using ChameLab.Services;
using ChameLab.ViewModels;
using ChameLab.Views;
using Xunit;

namespace ChameLab.UiTests;

public class DeferredContentTests
{
    [AvaloniaFact]
    public void CreatesOnlyWhenOpenedAndPreservesTheEditorOnReturn()
    {
        var builds = 0;
        var content = new DeferredContent
        {
            DataContext = new object(),
            ContentTemplate = new FuncDataTemplate<object>((_, _) => { builds++; return new TextBox(); }),
        };
        Assert.Null(content.Child);
        content.IsActive = true;
        var editor = Assert.IsType<TextBox>(content.Child);
        editor.Text = "unsaved synthetic edit";
        content.IsActive = false;
        Assert.False(content.IsVisible);
        content.IsActive = true;
        Assert.Same(editor, content.Child);
        Assert.Equal("unsaved synthetic edit", editor.Text);
        Assert.Equal(1, builds);
    }

    [AvaloniaFact]
    public void ColdCardLibraryDoesNotCreateMemoryAndWriteEditors()
    {
        var store = new MemoryKeyValueStore();
        var vm = new CardsViewModel(new DeviceService([]), new CardLibrary(store), new DictionaryLibrary(store));
        var view = new CardsView { DataContext = vm };
        var window = new Window { Width = 360, Height = 800, Content = view };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Assert.Empty(view.GetVisualDescendants().OfType<CardWriterView>());
            Assert.Empty(view.GetVisualDescendants().OfType<HexEditor>());
            Assert.All(view.GetVisualDescendants().OfType<DeferredContent>(), c => Assert.Null(c.Child));
        }
        finally { window.Close(); }
    }
}

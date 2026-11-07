using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ChameLab.Services;
using ChameLab.ViewModels;
using ChameLab.Views;
using Xunit;

namespace ChameLab.UiTests;

public class MenuNavigationTests
{
    private sealed class WaitingPage : PageViewModelBase
    {
        public TaskCompletionSource Refresh { get; } = new();
        public WaitingPage() : base(() => "Waiting", "info") { }
        public override Task OnActivatedAsync() => Refresh.Task;
    }

    [AvaloniaFact]
    public void DeviceRefreshDoesNotHoldNavigationToAnotherPage()
    {
        var device = new DeviceService([]);
        var store = new MemoryKeyValueStore();
        var model = new MainViewModel(device, new CardLibrary(store), new DictionaryLibrary(store),
            new FirmwareUpdateService(device, []));
        var page = new WaitingPage();
        try
        {
            model.SelectedNavItem = page;
            Assert.Same(page, model.CurrentPage);
            model.SelectedNavItem = model.Settings;
            Assert.Same(model.Settings, model.CurrentPage);
        }
        finally { page.Refresh.TrySetResult(); model.Connect.StopWatching(); }
    }

    [AvaloniaFact]
    public void EmptySlotsPageDoesNotShowAModalPickerWithAnInactiveCloseButton()
    {
        var view = new SlotsView { DataContext = new SlotsViewModel(new DeviceService([]), new CardLibrary(new MemoryKeyValueStore())) };
        var window = new Window { Width = 1100, Height = 700, Content = view };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Assert.False(view.FindControl<Border>("PickerDialog")!.IsEffectivelyVisible);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(360)]
    [InlineData(1100)]
    public async Task EveryOfflineMenuPageOpensAndBackReturnsToConnect(int width)
    {
        var device = new DeviceService([]);
        var store = new MemoryKeyValueStore();
        var model = new MainViewModel(device, new CardLibrary(store), new DictionaryLibrary(store), new FirmwareUpdateService(device, []));
        var view = new MainView { DataContext = model };
        var window = new Window { Width = width, Height = 900, Content = view };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var list = view.GetVisualDescendants().OfType<ListBox>().Single(l => l.Classes.Contains("nav"));
            foreach (var page in model.NavPages.Where(p => p != model.Connect).ToArray())
            {
                model.IsPaneOpen = true;
                window.UpdateLayout();
                await Task.Delay(250);
                var row = (Control)list.ContainerFromItem(page)!;
                row.BringIntoView(); window.UpdateLayout();
                var point = row.TranslatePoint(new Point(row.Bounds.Width / 2, row.Bounds.Height / 2), window)!.Value;
                window.MouseMove(point);
                window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left);
                for (var i = 0; i < 50 && model.CurrentPage != page; i++) await Task.Delay(20);
                Assert.Same(page, model.CurrentPage);
                Assert.False(model.IsCompact && model.IsPaneOpen);
                Assert.True(model.TryGoBack());
                for (var i = 0; i < 50 && model.CurrentPage != model.Connect; i++) await Task.Delay(20);
                Assert.Same(model.Connect, model.CurrentPage);
            }
        }
        finally { model.Connect.StopWatching(); window.Close(); }
    }
}

using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using ChameLab.UiTesting;
using ChameLab.ViewModels;
using ChameLab.Views;
using ChameLab.Services;
using Xunit;

namespace ChameLab.UiTests;

public class PageLifetimeTests
{
    [AvaloniaFact]
    public void DetachedMainViewIsCollectedWhileApplicationModelStaysAlive()
    {
        var device = new DeviceService([]);
        var store = new MemoryKeyValueStore();
        var model = new MainViewModel(device, new CardLibrary(store), new DictionaryLibrary(store),
            new FirmwareUpdateService(device, []));
        var window = new Window { Width = 360, Height = 800 };
        window.Show();
        try
        {
            var reference = ShowAndRemoveMainView(window, model);
            Dispatcher.UIThread.RunJobs();
            using (window.CaptureRenderedFrame()) { }
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Assert.False(reference.IsAlive, "The application model retains the previous main view.");
            Assert.Null(StorageService.TopLevelAccessor);
            GC.KeepAlive(model);
        }
        finally { window.Close(); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference ShowAndRemoveMainView(Window window, MainViewModel model)
    {
        var view = new MainView { DataContext = model };
        window.Content = view;
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        using (window.CaptureRenderedFrame()) { }
        window.Content = null;
        return new WeakReference(view);
    }

    public static IEnumerable<object[]> Pages()
    {
        foreach (var page in LayoutScenarios.Pages) yield return [page];
    }

    [AvaloniaTheory]
    [MemberData(nameof(Pages))]
    public void DetachedPageIsCollectedWhileWindowStaysOpen(string page)
    {
        var window = new Window { Width = 320, Height = 640 };
        window.Show();
        try
        {
            var reference = ShowAndRemovePage(window, page);
            Dispatcher.UIThread.RunJobs();
            using (window.CaptureRenderedFrame()) { }
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Assert.False(reference.IsAlive, $"Detached page '{page}' is still retained.");
        }
        finally { window.Close(); }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference ShowAndRemovePage(Window window, string page)
    {
        var scene = LayoutScenarios.Create(page, "en");
        window.Content = scene;
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        using (window.CaptureRenderedFrame()) { }
        if (scene.Child?.DataContext is PageViewModelBase vm)
            vm.OnDeactivatedAsync().GetAwaiter().GetResult();
        window.Content = null;
        return new WeakReference(scene);
    }
}

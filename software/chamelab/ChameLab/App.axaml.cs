using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using ChameLab.Services;
using ChameLab.ViewModels;
using ChameLab.Views;

namespace ChameLab;

public partial class App : Application
{
    private MainViewModel? _mainViewModel;

    public System.Threading.Tasks.Task SetForegroundAsync(bool foreground) =>
        _mainViewModel?.SetForegroundAsync(foreground) ?? System.Threading.Tasks.Task.CompletedTask;

    public override void Initialize()
    {
        // Before any XAML loads, so every view reads its strings in the chosen language.
        LocalizationService.ApplySaved();
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
#if CHAMELAB_UI_TESTS
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime testDesktop)
            testDesktop.MainWindow = new Avalonia.Controls.Window { Width = 360, Height = 800, Content = new UiTesting.UiTestHost() };
        else if (ApplicationLifetime is IActivityApplicationLifetime testActivity)
            testActivity.MainViewFactory = () => new UiTesting.UiTestHost();
        else if (ApplicationLifetime is ISingleViewApplicationLifetime testSingle)
            testSingle.MainView = new UiTesting.UiTestHost();
#else
        AppearanceService.ApplySaved();
        var vm = _mainViewModel = new MainViewModel(AppServices.Device, AppServices.Cards, AppServices.Dictionaries, AppServices.Firmware);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow { DataContext = vm };
        }
        else if (ApplicationLifetime is IActivityApplicationLifetime activityLifetime)
        {
            // Activity recreation replaces the view, while connection and unsaved edits
            // belong to the application and must keep the same model.
            activityLifetime.MainViewFactory = () => new MainView { DataContext = vm };
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime singleViewPlatform)
        {
            singleViewPlatform.MainView = new MainView { DataContext = vm };
        }

#endif
        base.OnFrameworkInitializationCompleted();
    }
}

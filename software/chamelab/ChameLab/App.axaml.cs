using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using ChameLab.Services;
using ChameLab.ViewModels;
using ChameLab.Views;

namespace ChameLab;

public partial class App : Application
{
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
        var vm = new MainViewModel(AppServices.Device, AppServices.Cards, AppServices.Dictionaries, AppServices.Firmware);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow { DataContext = vm };
        }
        else if (ApplicationLifetime is IActivityApplicationLifetime activityLifetime)
        {
            activityLifetime.MainViewFactory = () => new MainView { DataContext = new MainViewModel(AppServices.Device, AppServices.Cards, AppServices.Dictionaries, AppServices.Firmware) };
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime singleViewPlatform)
        {
            singleViewPlatform.MainView = new MainView { DataContext = vm };
        }

#endif
        base.OnFrameworkInitializationCompleted();
    }
}
using System.Linq;
using System.Reflection;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using ChameLab.Localization;
using ChameLab.Models;
using ChameLab.Services;
using ChameLab.Transport;
using ChameLab.UiTesting;
using ChameLab.ViewModels;
using ChameLab.Views;
using ReactiveUI;
using ReactiveUI.Primitives;
using Xunit;

namespace ChameLab.UiTests;

public class FirmwareRecoveryUiTests
{
    [AvaloniaTheory]
    [InlineData("backup")]
    [InlineData("confirm")]
    [InlineData("recovery")]
    public async Task BackupAndRecoveryDialogsFitSmallPhoneWithLargeText(string dialog)
    {
        LocalizationService.Apply("ru"); LocalizationService.UseOnThisThread();
        var store = new MemoryKeyValueStore(); store.Write("device-backup", "AQID");
        var device = new DeviceService([]);
        var model = new MainViewModel(device, new CardLibrary(store), new DictionaryLibrary(store), new(device, [new Locator()], recovery: new(store)), new(device, store));
        var scene = new MainView { DataContext = model };
        var window = new Window { Width = 320, Height = 480, Content = scene };
        window.Show();
        try
        {
            if (dialog == "backup")
            {
                await ((ReactiveCommand<RxVoid, RxVoid>)model.Backups.OpenCommand).Execute();
                // Layout fixture: expose the confirmation controls; protocol validation is tested separately.
                typeof(DeviceBackupViewModel).GetProperty(nameof(DeviceBackupViewModel.CanRestore))!.SetValue(model.Backups, true);
            }
            else if (dialog == "confirm") await ((ReactiveCommand<RxVoid, RxVoid>)model.Firmware.ConfirmCommand).Execute();
            else await ((ReactiveCommand<RxVoid, RxVoid>)model.Firmware.OpenRecoveryCommand).Execute();
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            LayoutScenarios.ScaleText(scene, 1.5); window.UpdateLayout();
            Assert.Empty(LayoutScenarios.FindOverflow(scene));
        }
        finally { model.Connect.StopWatching(); window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData("device", "ru")]
    [InlineData("connect", "ru")]
    [InlineData("device", "en")]
    [InlineData("connect", "en")]
    public void BackupButtonsFitNarrowPhoneWithLargeText(string page, string language)
    {
        var scene = LayoutScenarios.Create(page, language);
        var window = new Window { Width = 320, Height = 640, Content = scene };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            foreach (var expander in scene.GetVisualDescendants().OfType<Expander>().ToArray()) expander.IsExpanded = true;
            LayoutScenarios.ScaleText(scene, 1.5); window.UpdateLayout();
            Assert.Empty(LayoutScenarios.FindOverflow(scene));
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task SystemBackCannotNavigateAwayWhileBackupIsWorking()
    {
        var store = new MemoryKeyValueStore(); var device = new DeviceService([]);
        var model = new MainViewModel(device, new CardLibrary(store), new DictionaryLibrary(store), new(device, []), new(device, store));
        var view = new MainView { DataContext = model };
        var window = new Window { Content = view, Width = 360, Height = 800 };
        window.Show();
        try
        {
            model.SelectedNavPage = model.Cards;
            for (var i = 0; i < 50 && model.CurrentPage != model.Cards; i++) await Task.Delay(20);
            Assert.Same(model.Cards, model.CurrentPage);
            await ((ReactiveCommand<RxVoid, RxVoid>)model.Backups.OpenCommand).Execute();
            typeof(DeviceBackupViewModel).GetProperty(nameof(DeviceBackupViewModel.IsWorking))!.SetValue(model.Backups, true);
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var back = new RoutedEventArgs();
            typeof(MainView).GetMethod("OnBackRequested", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(view, [window, back]);
            Assert.True(back.Handled);
            Assert.True(model.Backups.IsOpen);
            Assert.Same(model.Cards, model.CurrentPage);
        }
        finally { model.Connect.StopWatching(); window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(320, 480)]
    [InlineData(360, 800)]
    public async Task RecoveryWithoutSavedTaskCannotStartFlashAndFitsPhone(int width, int height)
    {
        LocalizationService.Apply("ru"); LocalizationService.UseOnThisThread();
        var store = new MemoryKeyValueStore(); var device = new DeviceService([]);
        var firmware = new FirmwareUpdateService(device, [new Locator()], recovery: new(store));
        var model = new MainViewModel(device, new CardLibrary(store), new DictionaryLibrary(store), firmware, new(device, store));
        var view = new MainView { DataContext = model };
        var host = new Border { Child = view, Classes = { "compact" } };
        var window = new Window { Width = width, Height = height, Content = host };
        window.Show();
        try
        {
            await ((ReactiveCommand<RxVoid, RxVoid>)model.Firmware.OpenRecoveryCommand).Execute();
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Assert.True(model.Firmware.IsRecovery);
            Assert.False(model.Firmware.CanRetry);
            Assert.Contains(Strings.Firmware_RecoveryEmpty, model.Firmware.StepText);
            Assert.Empty(LayoutScenarios.FindOverflow(host));
            model.Firmware.CloseCommand.Execute(null);
            Assert.False(model.Firmware.IsDialogOpen);
        }
        finally { model.Connect.StopWatching(); window.Close(); }
    }

    [AvaloniaFact]
    public async Task CorruptRecoveryTaskIsShownWithoutErasingIt()
    {
        var store = new MemoryKeyValueStore(); store.Write("firmware-recovery", "{");
        var device = new DeviceService([]);
        var vm = new FirmwareUpdateViewModel(device, new(device, [new Locator()], recovery: new(store)));
        await ((ReactiveCommand<RxVoid, RxVoid>)vm.OpenRecoveryCommand).Execute();
        Assert.Equal(Strings.Firmware_RecoveryInvalid, vm.Error);
        Assert.False(vm.CanRetry);
        Assert.Equal("{", store.Read("firmware-recovery"));
    }

    [AvaloniaFact]
    public async Task InvalidBackupPasswordLeavesInstallAtConfirmationBeforeAnyDeviceWork()
    {
        var store = new MemoryKeyValueStore(); var device = new DeviceService([]);
        var vm = new FirmwareUpdateViewModel(device, new(device, []), new(device, store))
        { BackupPassword = "too short", BackupPasswordRepeat = "different" };
        await ((ReactiveCommand<RxVoid, RxVoid>)vm.ConfirmCommand).Execute();
        Assert.True(vm.IsConfirming);
        Assert.Equal(Strings.Backup_PasswordHint, vm.Error);
        Assert.Null(store.Read("firmware-recovery"));
        Assert.Null(store.Read("device-backup"));
        vm.CloseCommand.Execute(null);
        Assert.Equal("", vm.BackupPassword);
        Assert.Equal("", vm.BackupPasswordRepeat);
    }

    [AvaloniaFact]
    public async Task BackupViewWithoutFileCannotRestoreAndClearsPasswordOnClose()
    {
        var device = new DeviceService([]);
        var vm = new DeviceBackupViewModel(new(device, new MemoryKeyValueStore()), device);
        await ((ReactiveCommand<RxVoid, RxVoid>)vm.OpenCommand).Execute();
        Assert.True(vm.IsOpen);
        Assert.False(vm.HasFile);
        Assert.False(vm.CanRestore);
        vm.Password = "synthetic password";
        vm.CloseCommand.Execute(null);
        Assert.Equal("", vm.Password);
        Assert.False(vm.IsOpen);
    }

    private sealed class Locator : IDfuLocator
    {
        public TransportKind Kind => TransportKind.Serial;
        public IDfuTarget Prepare(TransportCandidate device, DeviceInfo info) => throw new System.NotSupportedException();
    }
}

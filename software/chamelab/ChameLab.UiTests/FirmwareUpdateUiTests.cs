using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ChameLab.Localization;
using ChameLab.Models;
using ChameLab.Services;
using ChameLab.Transport;
using ChameLab.ViewModels;
using ChameLab.Views;
using ReactiveUI;
using ReactiveUI.Primitives;
using Xunit;

namespace ChameLab.UiTests;

public class FirmwareUpdateUiTests
{
    [AvaloniaTheory]
    [InlineData("v2.3.17-cl", true)]
    [InlineData("v2.3.18-cl", false)]
    [InlineData("v2.3.18-cl-1-gabcdef0", false)]
    [InlineData("v2.3.19-cl", false)]
    public async Task LatestInstallRequiresANewerFirmwareVersion(string installed, bool available)
    {
        var device = ConnectedDevice(installed);
        using var handler = new ReleaseHandler();
        using var http = new HttpClient(handler);
        var vm = new FirmwareUpdateViewModel(device, new(device, [new Locator()], http));

        await vm.OnConnectedAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(available ? FirmwareCheckState.Available : FirmwareCheckState.UpToDate, vm.CheckState);
        Assert.Equal(available, vm.CanInstallLatest);
        Assert.Equal(available, vm.InstallLatestCommand.CanExecute(null));
        Assert.True(vm.CanInstall); // A deliberately selected file remains installable.
        vm.InstallLatestCommand.Execute(null);
        Assert.Equal(available, vm.IsConfirming);
    }

    [AvaloniaFact]
    public async Task ReconnectAfterUpdateHidesTheButtonUsingTheCachedRelease()
    {
        var device = ConnectedDevice("v2.3.17-cl");
        using var handler = new ReleaseHandler();
        using var http = new HttpClient(handler);
        var vm = new FirmwareUpdateViewModel(device, new(device, [new Locator()], http));
        var view = new ChameleonView
        {
            DataContext = new ChameleonViewModel(device, new CardLibrary(new MemoryKeyValueStore()), vm),
        };
        var window = new Window { Width = 360, Height = 800, Content = view };
        window.Show();
        try
        {
            await vm.OnConnectedAsync();
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var button = Assert.Single(view.GetVisualDescendants().OfType<Button>(), b => b.Command == vm.InstallLatestCommand);
            Assert.True(button.IsVisible);
            Assert.True(button.IsEffectivelyEnabled);

            SetVersion(device, "v2.3.18-cl");
            await vm.OnConnectedAsync();
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();

            Assert.Equal(1, handler.Requests);
            Assert.Equal("v2.3.18-cl", vm.CurrentVersion);
            Assert.Equal(FirmwareCheckState.UpToDate, vm.CheckState);
            Assert.Equal(string.Format(Strings.Firmware_UpToDate, vm.Latest!.Version), vm.CheckMessage);
            Assert.False(button.IsVisible);
            Assert.False(button.IsEffectivelyEnabled);
            vm.InstallLatestCommand.Execute(null);
            Assert.False(vm.IsDialogOpen);

            // Connecting an older device must make the update available again.
            SetVersion(device, "v2.3.17-cl");
            await vm.OnConnectedAsync();
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Assert.Equal(1, handler.Requests);
            Assert.True(button.IsVisible);
            Assert.True(button.IsEffectivelyEnabled);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task FailedRecheckDoesNotAllowInstallingTheStaleRelease()
    {
        var device = ConnectedDevice("v2.3.17-cl");
        using var handler = new ReleaseHandler();
        using var http = new HttpClient(handler);
        var vm = new FirmwareUpdateViewModel(device, new(device, [new Locator()], http));
        await vm.OnConnectedAsync();
        Assert.True(vm.CanInstallLatest);

        handler.Fail = true;
        await ((ReactiveCommand<RxVoid, RxVoid>)vm.CheckCommand).Execute();
        Dispatcher.UIThread.RunJobs();

        Assert.NotNull(vm.Latest);
        Assert.Equal(FirmwareCheckState.Failed, vm.CheckState);
        Assert.False(vm.CanInstallLatest);
        Assert.False(vm.InstallLatestCommand.CanExecute(null));
        vm.InstallLatestCommand.Execute(null);
        Assert.False(vm.IsDialogOpen);
    }

    [AvaloniaTheory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task NewerReleaseNeedsBothPackageAndSupportedTransport(bool package, bool transport)
    {
        var device = ConnectedDevice("v2.3.17-cl");
        using var handler = new ReleaseHandler { IncludePackage = package };
        using var http = new HttpClient(handler);
        var vm = new FirmwareUpdateViewModel(device, new(device, transport ? [new Locator()] : [], http));
        await vm.OnConnectedAsync();
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(FirmwareCheckState.Available, vm.CheckState);
        Assert.False(vm.CanInstallLatest);
        Assert.False(vm.InstallLatestCommand.CanExecute(null));
        vm.InstallLatestCommand.Execute(null);
        Assert.False(vm.IsDialogOpen);
    }

    // Only connection metadata is needed: these tests never send commands to hardware.
    private static DeviceService ConnectedDevice(string version)
    {
        var device = new DeviceService([]);
        typeof(DeviceService).GetProperty(nameof(DeviceService.Candidate))!.SetValue(device,
            new TransportCandidate(TransportKind.Ble, "synthetic", "Synthetic Chameleon"));
        SetVersion(device, version);
        return device;
    }

    private static void SetVersion(DeviceService device, string version) =>
        typeof(DeviceService).GetProperty(nameof(DeviceService.DeviceInfo))!.SetValue(device, new DeviceInfo
        {
            FirmwareVersion = "2.3", GitVersion = version, Model = DeviceModel.Ultra,
            ChipId = "synthetic", Address = "synthetic",
        });

    private sealed class Locator : IDfuLocator
    {
        public TransportKind Kind => TransportKind.Ble;
        public IDfuTarget Prepare(TransportCandidate device, DeviceInfo info) => throw new NotSupportedException();
    }

    private sealed class ReleaseHandler : HttpMessageHandler
    {
        public int Requests { get; private set; }
        public bool Fail { get; set; }
        public bool IncludePackage { get; init; } = true;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            var assets = IncludePackage
                ? """[{"name":"ultra-dfu-app.zip","browser_download_url":"https://example.invalid/firmware.zip","size":100}]"""
                : "[]";
            return Task.FromResult(new HttpResponseMessage(Fail ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
            {
                Content = new StringContent($$"""
                    {"tag_name":"v1.5.2","body":"ChameLab v1.5.2 with firmware v2.3.18-cl",
                     "html_url":"https://example.invalid/release","assets":{{assets}}}
                    """),
            });
        }
    }
}

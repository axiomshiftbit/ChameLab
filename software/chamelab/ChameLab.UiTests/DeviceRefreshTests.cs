using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ChameLab.Localization;
using ChameLab.Models;
using ChameLab.Protocol;
using ChameLab.Services;
using ChameLab.Transport;
using ChameLab.UiTesting;
using ChameLab.ViewModels;
using ChameLab.Views;
using ReactiveUI;
using ReactiveUI.Primitives;
using Xunit;

namespace ChameLab.UiTests;

public class DeviceRefreshTests
{
    [AvaloniaTheory]
    [InlineData("firmware")]
    [InlineData("backup")]
    public async Task OpeningDeviceOperationCancelsAnAlreadyRunningResumeProbe(string operation)
    {
        var transport = new DeviceTransport();
        var device = new DeviceService([new Scanner(transport)]);
        var store = new MemoryKeyValueStore();
        var model = new MainViewModel(device, new CardLibrary(store), new DictionaryLibrary(store),
            new FirmwareUpdateService(device, [], recovery: new(store)), new(device, store));
        try
        {
            model.SelectedNavItem = model.Cards;
            await device.ConnectAsync(new(TransportKind.Ble, "synthetic", "Test device"));
            Dispatcher.UIThread.RunJobs();
            var client = device.Client;
            transport.HoldVersion = true;
            await model.SetForegroundAsync(false);
            var resume = model.SetForegroundAsync(true);
            await transport.VersionHeld.Task.WaitAsync(TimeSpan.FromSeconds(2));
            var open = operation == "firmware" ? model.Firmware.OpenRecoveryCommand : model.Backups.OpenCommand;
            await ((ReactiveCommand<RxVoid, RxVoid>)open).Execute();
            await resume.WaitAsync(TimeSpan.FromSeconds(1));
            Assert.Same(client, device.Client);
            Assert.Equal(ConnectionState.Connected, device.State);
        }
        finally { model.Connect.StopWatching(); await device.DisconnectAsync(); }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ForegroundRecoveryPreservesCardEditsAndHonorsManualDisconnect(bool disconnect)
    {
        var transport = new DeviceTransport();
        var device = new DeviceService([new Scanner(transport)]);
        var store = new MemoryKeyValueStore();
        var model = new MainViewModel(device, new CardLibrary(store), new DictionaryLibrary(store), new FirmwareUpdateService(device, []));
        try
        {
            model.SelectedNavItem = model.Cards;
            model.Cards.AddCommand.Execute(null);
            model.Cards.ManualCommand.Execute(null);
            var form = model.Cards.Form!;
            form.Name = "Unsaved synthetic card";
            await device.ConnectAsync(new(TransportKind.Ble, "synthetic", "Test device"));
            Dispatcher.UIThread.RunJobs();
            var client = device.Client;
            var reads = transport.Requests.Count(r => r.Command == Command.GetAppVersion);
            await model.SetForegroundAsync(false);
            if (disconnect) await ((ReactiveCommand<RxVoid, RxVoid>)model.DisconnectCommand).Execute();
            await model.SetForegroundAsync(true);
            Dispatcher.UIThread.RunJobs();
            Assert.Same(model.Cards, model.CurrentPage);
            Assert.Same(form, model.Cards.Form);
            Assert.Equal("Unsaved synthetic card", form.Name);
            Assert.Equal(reads + (disconnect ? 0 : 1), transport.Requests.Count(r => r.Command == Command.GetAppVersion));
            if (disconnect) Assert.Null(device.Client);
            else Assert.Same(client, device.Client);
        }
        finally { model.Connect.StopWatching(); await device.DisconnectAsync(); }
    }

    [AvaloniaTheory]
    [InlineData(320, 480, 1.0)]
    [InlineData(320, 480, 1.5)]
    [InlineData(360, 560, 1.0)]
    [InlineData(430, 640, 1.5)]
    public async Task ScannedCardLeavesRoomToReadAndKeepsSaveButtonsInsidePhone(int width, int height, double scale)
    {
        LocalizationService.Apply("ru"); LocalizationService.UseOnThisThread();
        var device = new DeviceService([new Scanner(new DeviceTransport())]);
        await device.ConnectAsync(new(TransportKind.Ble, "synthetic", "Test device"));
        var store = new MemoryKeyValueStore();
        var model = new CardsViewModel(device, new CardLibrary(store), new DictionaryLibrary(store));
        var view = new CardsView { DataContext = model };
        var root = new Border { Child = view, Classes = { "compact" } };
        // Available page height after the application's navigation/status header.
        var window = new Window { Width = width, Height = height, Content = root };
        try
        {
            await model.OnActivatedAsync(); Dispatcher.UIThread.RunJobs();
            model.AddCommand.Execute(null);
            await ((ReactiveCommand<RxVoid, RxVoid>)model.ScanHfCommand).Execute();
            LocalizationService.UseOnThisThread();
            Dispatcher.UIThread.RunJobs(); window.Show();
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            LayoutScenarios.ScaleText(root, scale); window.UpdateLayout();
            Assert.True(model.NeedsDump);
            Assert.Equal(string.Format(Strings.Cards_Generation, Strings.Cards_GenerationUnknown), model.ScanGeneration);
            Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), b => b.Text == Strings.Cards_CardDetailsHeader);
            Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), b => b.Text == Strings.Cards_SaveUidOnly);
            var scroll = view.FindControl<ScrollViewer>("PaneScroll")!;
            Assert.True(scroll.Viewport.Height >= height * .65,
                $"Only {scroll.Viewport.Height} of {height} pixels remain for card details.");
            var discard = view.GetVisualDescendants().OfType<Button>().Single(b => b.Command == model.CancelAddCommand);
            var save = view.GetVisualDescendants().OfType<Button>().Single(b => b.Command == model.SaveCommand && b.IsEffectivelyVisible);
            var discardRect = new Rect(discard.TranslatePoint(default, root)!.Value, discard.Bounds.Size);
            var saveRect = new Rect(save.TranslatePoint(default, root)!.Value, save.Bounds.Size);
            Assert.True(new Rect(root.Bounds.Size).Contains(discardRect), $"Discard outside page: {discardRect}");
            Assert.True(new Rect(root.Bounds.Size).Contains(saveRect), $"Save outside page: {saveRect}");
            Assert.False(discardRect.Intersects(saveRect));
            Assert.Empty(LayoutScenarios.FindOverflow(root));
            scroll.ScrollToEnd(); window.UpdateLayout();
            Assert.True(scroll.Offset.Y > 0);
            Assert.Equal(saveRect, new Rect(save.TranslatePoint(default, root)!.Value, save.Bounds.Size));
            Assert.Equal(discardRect, new Rect(discard.TranslatePoint(default, root)!.Value, discard.Bounds.Size));
        }
        finally { window.Close(); await device.DisconnectAsync(); }
    }

    [AvaloniaFact]
    public async Task ReportedMissingHfCommandsDisableScanButUnknownFirmwareDoesNot()
    {
        var transport = new DeviceTransport { SupportedCommands = [Command.ChangeDeviceMode, Command.Em410xScan] };
        var device = new DeviceService([new Scanner(transport)]);
        await device.ConnectAsync(new(TransportKind.Ble, "synthetic", "Test device"));
        try
        {
            var store = new MemoryKeyValueStore();
            var model = new CardsViewModel(device, new CardLibrary(store), new DictionaryLibrary(store));
            Dispatcher.UIThread.RunJobs();
            Assert.False(model.CanScanHf);
            Assert.False(model.ScanHfCommand.CanExecute(null));
            Assert.True(model.CanScanLf);
            Assert.Equal(Strings.Capabilities_ScanLimited, model.ScanCapabilitiesHint);
            Assert.DoesNotContain(transport.Requests, r => r.Command == Command.Hf14aScan);
        }
        finally { await device.DisconnectAsync(); }
    }

    [AvaloniaFact]
    public async Task DashboardReadsBatteryAgainWhenFirstMeasurementIsNotReady()
    {
        var transport = new DeviceTransport();
        transport.BatterySamples.Enqueue([0, 0, 0, 0]);
        var device = new DeviceService([new Scanner(transport)]);
        await device.ConnectAsync(new(TransportKind.Ble, "synthetic", "Test device"));
        var model = new ChameleonViewModel(device, new CardLibrary(new MemoryKeyValueStore()),
            new FirmwareUpdateViewModel(device, new FirmwareUpdateService(device, [])));
        try
        {
            await model.OnActivatedAsync(); Dispatcher.UIThread.RunJobs();
            for (var i = 0; i < 30 && model.Battery?.Millivolts != 4000; i++)
            {
                await Task.Delay(100); Dispatcher.UIThread.RunJobs();
            }
            Assert.Equal(4000, model.Battery?.Millivolts);
            Assert.Equal(80, model.Battery?.Percent);
            Assert.True(model.HasBatteryMeasurement);
            Assert.False(model.IsBatteryPending);
            Assert.Equal(2, transport.Requests.Count(r => r.Command == Command.GetBatteryInfoEx));
            Assert.Single(transport.Requests, r => r.Command == Command.GetSlotInfo);
        }
        finally { await model.OnDeactivatedAsync(); await device.DisconnectAsync(); }
    }

    [AvaloniaTheory]
    [InlineData(0)]
    [InlineData(3000)]
    public async Task UnmeasuredBatteryIsNotShownAsEmptyAndLeavingStopsRetries(int millivolts)
    {
        var transport = new DeviceTransport { DefaultBatterySample = [(byte)(millivolts >> 8), (byte)millivolts, 0, 0] };
        var device = new DeviceService([new Scanner(transport)]);
        await device.ConnectAsync(new(TransportKind.Ble, "synthetic", "Test device"));
        var model = new ChameleonViewModel(device, new CardLibrary(new MemoryKeyValueStore()),
            new FirmwareUpdateViewModel(device, new FirmwareUpdateService(device, [])));
        var window = new Window { Width = 360, Height = 800, Content = new ChameleonView { DataContext = model } };
        try
        {
            await model.OnActivatedAsync(); Dispatcher.UIThread.RunJobs();
            window.Show(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            Assert.Equal(millivolts > 0, model.HasBatteryMeasurement);
            Assert.Equal(millivolts == 0, model.IsBatteryPending);
            Assert.Equal(millivolts > 0, window.GetVisualDescendants().OfType<TextBlock>()
                .Any(b => b.Text == "0%" && b.IsEffectivelyVisible));
            await model.OnDeactivatedAsync();
            var reads = transport.Requests.Count(r => r.Command == Command.GetBatteryInfoEx);
            await Task.Delay(1200); Dispatcher.UIThread.RunJobs();
            Assert.Equal(reads, transport.Requests.Count(r => r.Command == Command.GetBatteryInfoEx));
        }
        finally { window.Close(); await model.OnDeactivatedAsync(); await device.DisconnectAsync(); }
    }

    [AvaloniaFact]
    public async Task DashboardRefreshKeepsRowsAndReusesHandshakeIdentity()
    {
        var transport = new DeviceTransport();
        var device = new DeviceService([new Scanner(transport)]);
        await device.ConnectAsync(new(TransportKind.Ble, "synthetic", "Test device"));
        var model = new ChameleonViewModel(device, new CardLibrary(new MemoryKeyValueStore()),
            new FirmwareUpdateViewModel(device, new FirmwareUpdateService(device, [])));
        try
        {
            await model.OnActivatedAsync(); Dispatcher.UIThread.RunJobs();
            var rows = model.Slots.ToArray();
            Assert.Equal(8, rows.Length);
            var changes = 0;
            model.Slots.CollectionChanged += (_, _) => changes++;
            transport.Mode = DeviceMode.Reader;
            await model.OnActivatedAsync(); Dispatcher.UIThread.RunJobs();
            Assert.Equal(0, changes);
            for (var i = 0; i < rows.Length; i++) Assert.Same(rows[i], model.Slots[i]);
            Assert.True(model.ReaderMode);
            Assert.Single(transport.Requests, r => r.Command == Command.GetAppVersion);
            Assert.Single(transport.Requests, r => r.Command == Command.GetDeviceChipId);
            Assert.Equal(3, transport.Requests.Count(r => r.Command == Command.GetDeviceMode));
        }
        finally { await device.DisconnectAsync(); }
    }

    [AvaloniaTheory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(4)]
    public async Task ScanShowsOnlyConfirmedGenerationWithoutWritingCard(int generation)
    {
        var transport = new DeviceTransport { Generation = generation };
        var device = new DeviceService([new Scanner(transport)]);
        await device.ConnectAsync(new(TransportKind.Ble, "synthetic", "Test device"));
        var store = new MemoryKeyValueStore();
        var model = new CardsViewModel(device, new CardLibrary(store), new DictionaryLibrary(store));
        try
        {
            await model.OnActivatedAsync(); Dispatcher.UIThread.RunJobs();
            model.AddCommand.Execute(null);
            await ((ReactiveCommand<RxVoid, RxVoid>)model.ScanHfCommand).Execute();
            Dispatcher.UIThread.RunJobs();
            Assert.NotNull(model.Form);
            Assert.Contains(generation switch { 1 => "Gen1a", 4 => "Gen4", _ => Strings.Cards_GenerationUnknown }, model.ScanGeneration);
            Assert.DoesNotContain("\n", model.ScanGeneration);
            Assert.DoesNotContain(transport.Requests, r => r.Command == Command.Mf1WriteOneBlock);
            Assert.DoesNotContain(transport.Requests.Where(r => r.Command == Command.Hf14aRaw && r.Data.Length > 5),
                r => r.Data[5] is 0xA0 or 0xA2 or 0x90 || r.Data[5] == 0xCF && r.Data[10] != 0xC6);
            var view = new CardsView { DataContext = model };
            var root = new Border { Child = view, Classes = { "compact" } };
            var window = new Window { Width = 360, Height = 800, Content = root };
            window.Show();
            try
            {
                Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
                var label = Assert.Single(view.GetVisualDescendants().OfType<TextBlock>(), b => b.Text == model.ScanGeneration);
                Assert.True(label.IsEffectivelyVisible);
                Assert.Empty(LayoutScenarios.FindOverflow(root));
                model.SaveCommand.Execute(null); Dispatcher.UIThread.RunJobs();
                Assert.Null(model.ScanGeneration);
            }
            finally { window.Close(); }
        }
        finally { await device.DisconnectAsync(); }
    }

    private sealed class Scanner(DeviceTransport transport) : ITransportScanner
    {
        public TransportKind Kind => TransportKind.Ble;
        public string DisplayName => "Synthetic scanner";
        public Task<IReadOnlyList<TransportCandidate>> ScanAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TransportCandidate>>([]);
        public ITransport Create(TransportCandidate candidate) => transport;
    }

    private sealed class DeviceTransport : ITransport
    {
        private readonly FrameParser _parser = new();
        private readonly Channel<byte[]> _replies = Channel.CreateUnbounded<byte[]>();
        public List<ChameleonFrame> Requests { get; } = new();
        public DeviceMode Mode { get; set; }
        public int Generation { get; init; }
        public Command[] SupportedCommands { get; init; } = [];
        public bool HoldVersion;
        public TaskCompletionSource VersionHeld { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Queue<byte[]> BatterySamples { get; } = new();
        public byte[] DefaultBatterySample { get; init; } = [0x0F, 0xA0, 80, 4];
        public TransportKind Kind => TransportKind.Ble;
        public string DisplayName => "Synthetic device";
        public bool IsConnected => true;
        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
        {
            foreach (var request in _parser.Push(data))
            {
                Requests.Add(request);
                if (request.Command == Command.GetAppVersion && HoldVersion)
                {
                    VersionHeld.TrySetResult();
                    continue;
                }
                var status = Status.Success;
                byte[] payload = request.Command switch
                {
                    Command.GetAppVersion => [2, 3],
                    Command.GetDeviceCapabilities => SupportedCommands.SelectMany(c => new[] { (byte)((ushort)c >> 8), (byte)c }).ToArray(),
                    Command.GetDeviceMode => [(byte)Mode],
                    Command.GetBatteryInfoEx => BatterySamples.TryDequeue(out var sample) ? sample : DefaultBatterySample,
                    Command.GetSlotInfo => new byte[32],
                    Command.GetEnabledSlots => new byte[16],
                    Command.GetAllSlotNicks => new byte[16],
                    Command.GetAllSlotColors => new byte[48],
                    Command.GetActiveSlot => [0],
                    _ => [],
                };
                if (request.Command == Command.Hf14aScan)
                { status = Status.HfTagOk; payload = [4, 1, 2, 3, 4, 4, 0, 8, 0]; }
                if (request.Command == Command.Mf1DetectSupport) status = Status.HfTagOk;
                if (request.Command == Command.Hf14aRaw)
                {
                    status = Status.HfTagOk;
                    if (request.Data.Length > 5)
                        payload = request.Data[5] switch
                        {
                            0x40 or 0x43 when Generation == 1 => [0x0A],
                            0xCF when Generation == 4 => new byte[30],
                            _ => [],
                        };
                }
                _replies.Writer.TryWrite(FrameCodec.Encode(request.Command, payload, (ushort)status));
            }
            return Task.CompletedTask;
        }
        public async Task<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            var reply = await _replies.Reader.ReadAsync(ct);
            reply.CopyTo(buffer);
            return reply.Length;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

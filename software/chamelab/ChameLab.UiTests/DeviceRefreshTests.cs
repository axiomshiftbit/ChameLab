using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
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
        public TransportKind Kind => TransportKind.Ble;
        public string DisplayName => "Synthetic device";
        public bool IsConnected => true;
        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
        {
            foreach (var request in _parser.Push(data))
            {
                Requests.Add(request);
                var status = Status.Success;
                byte[] payload = request.Command switch
                {
                    Command.GetAppVersion => [2, 3],
                    Command.GetDeviceMode => [(byte)Mode],
                    Command.GetBatteryInfoEx => [0x0F, 0xA0, 80, 4],
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

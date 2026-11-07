using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ChameLab.Controls;
using ChameLab.Localization;
using ChameLab.Protocol;
using ChameLab.Services;
using ChameLab.Transport;
using ChameLab.ViewModels;
using ChameLab.Views;
using Xunit;

namespace ChameLab.UiTests;

public class FeedbackTests
{
    [AvaloniaFact]
    public async Task HiddenJournalDoesNotReformatTrafficAndCatchesUpWhenShown()
    {
        var log = new DeviceJournal();
        var model = new JournalViewModel(log);
        var view = new JournalView { DataContext = model };
        var window = new Window { Width = 360, Height = 700, Content = view };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            log.Add("INFO", "Before hiding");
            await Task.Delay(450);
            Assert.Contains("Before hiding", model.Text);
            view.IsVisible = false;
            var previous = model.Text;
            await Task.Run(() => { for (var i = 0; i < 500; i++) log.Add("INFO", $"Synthetic progress {i}"); });
            await Task.Delay(450);
            Assert.Equal(previous, model.Text);
            view.IsVisible = true;
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("Synthetic progress 499", model.Text);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData("hf", false)]
    [InlineData("hf", true)]
    [InlineData("memory", false)]
    [InlineData("memory", true)]
    [InlineData("lf", false)]
    [InlineData("lf", true)]
    public async Task ScanResultRemainsHighlightedAndDoesNotEnableUnconfirmedWrites(string action, bool found)
    {
        var scanner = new ScanFixture { Found = found };
        var device = new DeviceService([scanner]);
        await device.ConnectAsync(new(TransportKind.Tcp, "fixture", "UI test"));
        var store = new MemoryKeyValueStore();
        var model = new CardsViewModel(device, new CardLibrary(store), new DictionaryLibrary(store));
        var view = new ScanResultView { DataContext = model };
        var window = new Window { Width = 320, Height = 500, Content = view };
        window.Show();
        try
        {
            var command = action == "lf" ? model.TestScanLfCommand
                : action == "memory" ? model.InspectBlankCommand : model.DetectCommand;
            Assert.True(command.CanExecute(null));
            command.Execute(null);
            for (var i = 0; i < 250 && model.TargetState is TargetScanState.Idle or TargetScanState.Scanning; i++)
                await Task.Delay(20);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var expected = !found ? TargetScanState.Error : action == "memory" ? TargetScanState.Warning : TargetScanState.Found;
            Assert.Equal(expected, model.TargetState);
            Assert.False(model.TargetConfirmed);
            Assert.False(model.WriteNowCommand.CanExecute(null));
            var border = view.GetVisualDescendants().OfType<Border>().Single(b => b.Classes.Contains("scanresult"));
            Assert.Contains(!found ? "error" : action == "memory" ? "warning" : "success", border.Classes);
            Assert.Equal(model.TargetSummary, view.GetVisualDescendants().OfType<TextBlock>().Single().Text);
            if (!found) Assert.Equal(Strings.Writer_NotFound, model.TargetSummary);
            Assert.DoesNotContain(Command.Mf1WriteOneBlock, scanner.Commands);
            Assert.DoesNotContain(Command.Em410xWriteToT55xx, scanner.Commands);
            Assert.Equal(DeviceMode.Tag, await device.Client!.GetDeviceModeAsync());
        }
        finally { window.Close(); await device.DisconnectAsync(); }
    }

    [AvaloniaTheory]
    [InlineData(320)]
    [InlineData(360)]
    public async Task WriterJournalHasVisibleViewportAndUpdatesWhileOpen(int width)
    {
        LocalizationService.Apply("ru");
        LocalizationService.UseOnThisThread();
        var device = new DeviceService([]);
        var store = new MemoryKeyValueStore();
        var model = new WriterViewModel(device, new CardLibrary(store), new DictionaryLibrary(store));
        var view = new WriterView { DataContext = model };
        var window = new Window { Width = width, Height = 700, Content = view };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            view.GetVisualDescendants().OfType<Expander>().Single().IsExpanded = true;
            await Task.Delay(250);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            var journal = view.GetVisualDescendants().OfType<JournalView>().Single();
            var display = journal.GetVisualDescendants().OfType<TextBox>().Single();
            Assert.True(display.Bounds.Height >= 180, $"Journal viewport: {display.Bounds.Height}");
            await Task.Run(() => device.Journal.Add("INFO", "Проверка отображения журнала"));
            await Task.Delay(450);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.Contains("Проверка отображения журнала", display.Text);
            Assert.Equal(display.Text!.Length, display.CaretIndex);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void NarrowHeaderDoesNotToggleItsDetailsOnEveryLayoutPass()
    {
        var bar = new AppBarGrid { ColumnDefinitions = new("Auto,*,Auto") };
        bar.Children.Add(new Button { Content = "ChameLab", Width = 120 });
        var summary = new TextBlock { Text = "Chameleon Ultra — connected", Classes = { "header-summary" }, Width = 240 };
        Grid.SetColumn(summary, 1);
        bar.Children.Add(summary);
        var disconnect = new Button { Content = "Отключить", Width = 120 };
        Grid.SetColumn(disconnect, 2);
        bar.Children.Add(disconnect);
        var window = new Window { Width = 320, Height = 300, Content = bar };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.Contains("hide-summary", bar.Classes);
            var changes = 0;
            bar.Classes.CollectionChanged += (_, _) => changes++;
            for (var i = 0; i < 5; i++)
            {
                bar.InvalidateMeasure();
                window.UpdateLayout();
            }
            Assert.Equal(0, changes);
        }
        finally { window.Close(); }
    }

    private sealed class ScanFixture : ITransportScanner, ITransport
    {
        private readonly Channel<byte[]> _replies = Channel.CreateUnbounded<byte[]>();
        private readonly FrameParser _parser = new();
        private byte _mode;
        public bool Found { get; init; }
        public List<Command> Commands { get; } = new();
        public TransportKind Kind => TransportKind.Tcp;
        public string DisplayName => "Synthetic UI device";
        public bool IsConnected => true;
        public Task<IReadOnlyList<TransportCandidate>> ScanAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TransportCandidate>>([]);
        public ITransport Create(TransportCandidate candidate) => this;
        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
        {
            foreach (var request in _parser.Push(data))
            {
                Commands.Add(request.Command);
                if (request.Command == Command.ChangeDeviceMode) _mode = request.Data[0];
                (Status status, byte[] payload) reply = request.Command switch
                {
                    Command.GetAppVersion => (Status.Success, [2, 3]),
                    Command.GetDeviceMode => (Status.Success, [_mode]),
                    Command.Hf14aScan => (Found ? Status.HfTagOk : Status.HfTagNo, Found ? [4, 1, 2, 3, 4, 4, 0, 9, 0] : []),
                    Command.Em410xScan => (Found ? Status.LfTagOk : Status.LfTagNoFound, Found ? [1, 2, 3, 4, 5] : []),
                    Command.Mf1DetectSupport or Command.Hf14aRaw => (Status.HfTagOk, []),
                    Command.Mf1AuthOneKeyBlock => (Status.MfErrAuth, []),
                    _ => (Status.Success, []),
                };
                _replies.Writer.TryWrite(FrameCodec.Encode(request.Command, reply.payload, (ushort)reply.status));
            }
            return Task.CompletedTask;
        }
        public async Task<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            var data = await _replies.Reader.ReadAsync(ct);
            data.CopyTo(buffer);
            return data.Length;
        }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

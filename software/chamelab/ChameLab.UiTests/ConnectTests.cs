using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ChameLab.Services;
using ChameLab.Transport;
using ChameLab.ViewModels;
using ChameLab.Views;
using Xunit;

namespace ChameLab.UiTests;

public class ConnectTests
{
    [AvaloniaFact]
    public async Task BluetoothWatchingStopsDuringConnectionAndResumesAfterFailure()
    {
        var scanner = new WaitingBleScanner();
        var device = new DeviceService([scanner]);
        var store = new MemoryKeyValueStore();
        var model = new MainViewModel(device, new CardLibrary(store), new DictionaryLibrary(store), new FirmwareUpdateService(device, []));
        var states = new List<ConnectionState>();
        device.StateChanged += states.Add;
        try
        {
            Assert.True(scanner.Watching);
            var connect = device.ConnectAsync(new(TransportKind.Ble, "synthetic", "Test Chameleon"));
            Dispatcher.UIThread.RunJobs();
            Assert.False(scanner.Watching);
            scanner.Completion.SetException(new InvalidOperationException("Synthetic failed connection"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => connect);
            Dispatcher.UIThread.RunJobs();
            Assert.True(scanner.Watching);
            await device.DisconnectAsync();
            Assert.Equal(new[] { ConnectionState.Connecting, ConnectionState.Disconnected }, states);
        }
        finally { model.Connect.StopWatching(); }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void SingleClickSelectsAndExplicitActionConnects(bool doubleClick)
    {
        var scanner = new Scanner();
        var vm = new ConnectViewModel(new DeviceService([scanner]));
        var view = new ConnectView { DataContext = vm };
        var window = new Window { Width = 700, Height = 1000, Content = view };
        window.Show();
        try
        {
            vm.ScanCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.Equal(2, vm.Candidates.Count);
            Assert.Equal(0, scanner.Attempts);
            var list = view.FindControl<ListBox>("DeviceList")!;
            var row = list.GetVisualDescendants().OfType<ListBoxItem>().Last();
            var point = row.TranslatePoint(new Point(row.Bounds.Width / 2, row.Bounds.Height / 2), window)!.Value;
            Click(window, point);
            Assert.Equal(vm.Candidates[1], vm.Selected);
            Assert.Equal(0, scanner.Attempts);
            if (doubleClick) Click(window, point);
            else
            {
                var button = view.GetVisualDescendants().OfType<Button>().Single(b => b.Command == vm.ConnectCommand);
                Click(window, button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value);
            }
            Assert.Equal(1, scanner.Attempts);
            Assert.Equal(vm.Candidates[1], scanner.LastCandidate);
        }
        finally { window.Close(); }
    }

    private static void Click(Window window, Point point)
    {
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    private sealed class Scanner : ITransportScanner
    {
        public TransportKind Kind => TransportKind.Serial;
        public string DisplayName => "UI fixture";
        public int Attempts { get; private set; }
        public TransportCandidate? LastCandidate { get; private set; }
        public Task<IReadOnlyList<TransportCandidate>> ScanAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TransportCandidate>>([
                new(Kind, "test-a", "Chameleon A"), new(Kind, "test-b", "Chameleon B")]);
        public ITransport Create(TransportCandidate candidate)
        {
            Attempts++;
            LastCandidate = candidate;
            throw new InvalidOperationException("Synthetic connection attempt.");
        }
    }

    private sealed class WaitingBleScanner : ITransportScanner, ITransportWatcher, ITransport
    {
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Watching { get; private set; }
        public TransportKind Kind => TransportKind.Ble;
        public string DisplayName => "Synthetic BLE";
        public bool IsConnected => false;
        public void StartWatching(Action<IReadOnlyList<TransportCandidate>> changed) => Watching = true;
        public void StopWatching() => Watching = false;
        public Task<IReadOnlyList<TransportCandidate>> ScanAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<TransportCandidate>>([]);
        public ITransport Create(TransportCandidate candidate) => this;
        public Task ConnectAsync(CancellationToken ct = default) => Completion.Task;
        public Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default) => throw new InvalidOperationException("No connection");
        public Task<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => throw new InvalidOperationException("No connection");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

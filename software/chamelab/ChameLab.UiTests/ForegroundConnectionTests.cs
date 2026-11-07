using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using ChameLab.Services;
using ChameLab.Transport;
using ChameLab.ViewModels;
using Xunit;

namespace ChameLab.UiTests;

public class ForegroundConnectionTests
{
    [AvaloniaFact]
    public async Task HiddenApplicationStopsDiscoveryAndResumesItOnlyWhenVisible()
    {
        var scanner = new Scanner(); var device = new DeviceService([scanner]);
        var store = new MemoryKeyValueStore();
        var model = new MainViewModel(device, new CardLibrary(store), new DictionaryLibrary(store), new FirmwareUpdateService(device, []));
        try
        {
            Assert.Equal(1, scanner.Starts);
            await model.SetForegroundAsync(false);
            Assert.False(scanner.Watching);
            model.SelectedNavItem = model.Settings;
            model.SelectedNavItem = model.Connect;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(1, scanner.Starts);
            await model.SetForegroundAsync(true);
            Assert.True(scanner.Watching);
            Assert.Equal(2, scanner.Starts);
            await model.SetForegroundAsync(true);
            Assert.Equal(2, scanner.Starts);
        }
        finally { model.Connect.StopWatching(); }
    }

    [AvaloniaFact]
    public async Task ReturningKeepsTheSelectedApplicationPage()
    {
        var device = new DeviceService([]); var store = new MemoryKeyValueStore();
        var model = new MainViewModel(device, new CardLibrary(store), new DictionaryLibrary(store), new FirmwareUpdateService(device, []));
        try
        {
            model.SelectedNavItem = model.Dictionaries;
            await model.SetForegroundAsync(false);
            await model.SetForegroundAsync(true);
            Assert.Same(model.Dictionaries, model.CurrentPage);
        }
        finally { model.Connect.StopWatching(); }
    }

    private sealed class Scanner : ITransportScanner, ITransportWatcher
    {
        public int Starts; public bool Watching;
        public TransportKind Kind => TransportKind.Ble;
        public string DisplayName => "Synthetic";
        public void StartWatching(Action<IReadOnlyList<TransportCandidate>> changed) { Starts++; Watching = true; }
        public void StopWatching() => Watching = false;
        public Task<IReadOnlyList<TransportCandidate>> ScanAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<TransportCandidate>>([]);
        public ITransport Create(TransportCandidate candidate) => throw new NotSupportedException();
    }
}

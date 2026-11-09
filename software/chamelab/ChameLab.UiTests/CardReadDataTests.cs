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

public class CardReadDataTests
{
    private static CardSave PartialCard() => new()
    {
        Name = "Synthetic card", Tag = TagSpecificType.MifareMini, Uid = [1, 2, 3, 4], Sak = 9, Atqa = [4, 0],
        Data = [[], [], [], Convert.FromHexString("A1A2A3A4A5A6FF078069000000000000")],
        Extra = new() { UnknownKeyB = [0] },
    };

    private static (CardsViewModel Model, DictionaryLibrary Dictionaries) Open(DeviceService device, CardSave card)
    {
        var store = new MemoryKeyValueStore();
        var cards = new CardLibrary(store); cards.Add(card);
        var dictionaries = new DictionaryLibrary(store);
        var model = new CardsViewModel(device, cards, dictionaries);
        model.OpenCard(card.Id);
        Dispatcher.UIThread.RunJobs();
        return (model, dictionaries);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FoundKeysExportOmitsUnknownFieldsAndKeepsRealZeroKeys(bool zeroKey)
    {
        var card = PartialCard();
        if (zeroKey) Array.Clear(card.Data[3], 0, 6);
        // The same known key in another sector must appear only once.
        while (card.Data.Count < 8) card.Data.Add([]);
        card.Data[7] = card.Data[3].ToArray();
        card.Extra.UnknownKeyB.Add(1);
        var (model, dictionaries) = Open(new DeviceService([]), card);

        Assert.True(model.CanExportFoundKeys);
        Assert.False(model.CanReadCardData);
        Assert.Equal(1, model.FoundKeyCount);
        await ((ReactiveCommand<RxVoid, RxVoid>)model.SaveFoundKeysCommand).Execute();
        Dispatcher.UIThread.RunJobs();
        var exported = Assert.Single(dictionaries.Dictionaries);
        Assert.Equal(card.Name, exported.Name);
        Assert.Equal(card.Data[3][..6], Assert.Single(exported.Keys));
        Assert.Equal(exported.Keys[0], Assert.Single(KeyDictionary.FromText("roundtrip", exported.ToText()).Keys));

        await ((ReactiveCommand<RxVoid, RxVoid>)model.SaveFoundKeysCommand).Execute();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Synthetic card (2)", dictionaries.Dictionaries[1].Name);
        model.CloseCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.False(model.CanExportFoundKeys);
    }

    [AvaloniaFact]
    public async Task AddFoundKeysPreservesTheDictionaryAndDoesNotDuplicateKeys()
    {
        var (model, dictionaries) = Open(new DeviceService([]), PartialCard());
        var existing = new KeyDictionary
        {
            Name = "Existing", Color = "#123456", Folder = "Folder", FolderId = "synthetic-folder",
            Keys = [Convert.FromHexString("010203040506")],
        };
        var wrongSize = new KeyDictionary { Name = "Four bytes", KeySize = 4, Keys = [[1, 2, 3, 4]] };
        dictionaries.AddRange([existing, wrongSize]);
        Assert.Equal(existing.Id, Assert.Single(model.KeyExportDictionaries).Id);

        var command = (ReactiveCommand<KeyDictionary, RxVoid>)model.AddFoundKeysCommand;
        await command.Execute(existing); Dispatcher.UIThread.RunJobs();
        var updated = dictionaries.Dictionaries.Single(d => d.Id == existing.Id);
        Assert.Equal(2, updated.Count);
        Assert.Equal(existing.Color, updated.Color);
        Assert.Equal(existing.Folder, updated.Folder);
        Assert.Equal(existing.FolderId, updated.FolderId);
        await command.Execute(updated); Dispatcher.UIThread.RunJobs();
        Assert.Equal(2, dictionaries.Dictionaries.Single(d => d.Id == existing.Id).Count);
        await command.Execute(wrongSize); Dispatcher.UIThread.RunJobs();
        Assert.Single(wrongSize.Keys);
    }

    [AvaloniaFact]
    public void UnknownKeysDoNotEnableReadOrExport()
    {
        var card = PartialCard(); card.Extra.UnknownKeyA.Add(0);
        var (model, _) = Open(new DeviceService([]), card);
        Assert.Equal(0, model.FoundKeyCount);
        Assert.False(model.CanReadCardData);
        Assert.False(model.CanExportFoundKeys);
        Assert.False(model.ReadCardDataCommand.CanExecute(null));
        Assert.False(model.SaveFoundKeysCommand.CanExecute(null));
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadDataUsesOnlyCardKeysAndRejectsADifferentCard(bool changed)
    {
        var transport = new CardTransport { ChangedCard = changed };
        var device = new DeviceService([new Scanner(transport)]);
        await device.ConnectAsync(new(TransportKind.Ble, "synthetic", "Synthetic Chameleon"));
        try
        {
            var card = PartialCard();
            card.Data[0] = Enumerable.Repeat((byte)0xAA, 16).ToArray();
            var (model, dictionaries) = Open(device, card);
            dictionaries.Add(new KeyDictionary { Name = "Unrelated", Keys = [Convert.FromHexString("B1B2B3B4B5B6")] });
            Assert.True(model.CanReadCardData);
            await ((ReactiveCommand<RxVoid, RxVoid>)model.ReadCardDataCommand).Execute();
            Dispatcher.UIThread.RunJobs();
            var result = model.Form!.Build();
            Assert.Equal(card.Data[0], result.Data[0]);
            Assert.Equal(card.Data[3], result.Data[3]);
            if (changed)
            {
                Assert.NotNull(model.Error);
                Assert.DoesNotContain(transport.Requests, r => r.Command is Command.Mf1AuthOneKeyBlock or Command.Mf1ReadOneBlock);
                Assert.Empty(result.Data[1]);
            }
            else
            {
                Assert.Null(model.Error);
                Assert.Equal(16, result.Data[1].Length);
                Assert.Equal(16, result.Data[2].Length);
                Assert.Contains(transport.Requests, r => r.Command == Command.Mf1ReadOneBlock);
                Assert.All(transport.Requests.Where(r => r.Command is Command.Mf1AuthOneKeyBlock or Command.Mf1ReadOneBlock),
                    r => Assert.Equal(CardTransport.KnownKey, r.Data[2..8]));
                Assert.DoesNotContain(transport.Requests, r => r.Command == Command.Mf1ReadOneBlock && r.Data[1] is 0 or 3);
            }
            Assert.DoesNotContain(transport.Requests, r => r.Command == Command.Mf1WriteOneBlock);
        }
        finally { await device.DisconnectAsync(); }
    }

    [AvaloniaFact]
    public async Task StoppedReadKeepsPartialDataAndCanContinue()
    {
        var transport = new CardTransport();
        var device = new DeviceService([new Scanner(transport)]);
        await device.ConnectAsync(new(TransportKind.Ble, "synthetic", "Synthetic Chameleon"));
        try
        {
            var (model, _) = Open(device, PartialCard());
            transport.AfterRead = () => model.CancelDumpCommand.Execute(null);
            await ((ReactiveCommand<RxVoid, RxVoid>)model.ReadCardDataCommand).Execute();
            Dispatcher.UIThread.RunJobs();
            Assert.Null(model.Error);
            var kept = model.Form!.Build();
            Assert.False(model.IsDumpRunning);
            Assert.True(model.CanReadCardData);
            Assert.Contains(kept.Data.Take(3), data => data.Length == 0);
            Assert.Equal(16, kept.Data[3].Length);

            transport.AfterRead = null;
            await ((ReactiveCommand<RxVoid, RxVoid>)model.ReadCardDataCommand).Execute();
            Dispatcher.UIThread.RunJobs();
            Assert.Null(model.Error);
            Assert.All(model.Form.Build().Data.Take(4), data => Assert.Equal(16, data.Length));
        }
        finally { await device.DisconnectAsync(); }
    }

    [AvaloniaTheory]
    [InlineData("ru", 320, 1.5)]
    [InlineData("en", 360, 1.0)]
    [InlineData("de", 320, 1.5)]
    public void ReadAndExportActionsFitThePhone(string language, int width, double scale)
    {
        LocalizationService.Apply(language); LocalizationService.UseOnThisThread();
        var (model, dictionaries) = Open(new DeviceService([]), PartialCard());
        dictionaries.Add(new KeyDictionary { Name = "A long synthetic dictionary name for layout verification" });
        var view = new CardsView { DataContext = model };
        var window = new Window { Width = width, Height = 640, Content = view };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            LayoutScenarios.ScaleText(view, scale); window.UpdateLayout();
            var read = Assert.Single(view.GetVisualDescendants().OfType<Button>(), b => b.Command == model.ReadCardDataCommand);
            Assert.True(read.IsVisible);
            Assert.False(read.IsEffectivelyEnabled);
            Assert.Empty(LayoutScenarios.FindOverflow(view));
            var export = Assert.Single(view.GetVisualDescendants().OfType<Button>(),
                b => b.Content is TextBlock text && text.Text == Strings.Cards_ExportFoundKeys);
            var flyout = Assert.IsType<Flyout>(export.Flyout);
            flyout.ShowAt(export);
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var content = Assert.IsAssignableFrom<Control>(flyout.Content);
            LayoutScenarios.ScaleText(content, scale); content.UpdateLayout();
            Assert.Empty(LayoutScenarios.FindOverflow(content));
            var add = Assert.Single(content.GetVisualDescendants().OfType<Button>(), b => b.Command == model.AddFoundKeysCommand);
            Assert.Same(dictionaries.Dictionaries[0], add.CommandParameter);
            Assert.True(add.IsEffectivelyEnabled);
            flyout.Hide();
        }
        finally { window.Close(); }
    }

    private sealed class Scanner(CardTransport transport) : ITransportScanner
    {
        public TransportKind Kind => TransportKind.Ble;
        public string DisplayName => "Synthetic scanner";
        public Task<IReadOnlyList<TransportCandidate>> ScanAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TransportCandidate>>([]);
        public ITransport Create(TransportCandidate candidate) => transport;
    }

    private sealed class CardTransport : ITransport
    {
        public static readonly byte[] KnownKey = Convert.FromHexString("A1A2A3A4A5A6");
        private readonly FrameParser _parser = new();
        private readonly Channel<byte[]> _replies = Channel.CreateUnbounded<byte[]>();
        public bool ChangedCard { get; init; }
        public Action? AfterRead { get; set; }
        public List<ChameleonFrame> Requests { get; } = new();
        public TransportKind Kind => TransportKind.Ble;
        public string DisplayName => "Synthetic card";
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
                    Command.GetDeviceMode => [0],
                    Command.Hf14aScan => [4, 1, 2, 3, (byte)(ChangedCard ? 5 : 4), 4, 0, 9, 0],
                    _ => [],
                };
                if (request.Command == Command.Hf14aScan) status = Status.HfTagOk;
                if (request.Command is Command.Mf1AuthOneKeyBlock or Command.Mf1ReadOneBlock)
                {
                    var match = request.Data[1] < 4 && request.Data[0] == 0x60 && request.Data[2..8].SequenceEqual(KnownKey);
                    status = match ? Status.HfTagOk : Status.MfErrAuth;
                    if (match && request.Command == Command.Mf1ReadOneBlock)
                        payload = Enumerable.Repeat(request.Data[1], 16).ToArray();
                }
                _replies.Writer.TryWrite(FrameCodec.Encode(request.Command, payload, (ushort)status));
                if (request.Command == Command.Mf1ReadOneBlock) AfterRead?.Invoke();
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

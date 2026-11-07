using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using ChameLab.Localization;
using ChameLab.Models;
using ChameLab.Protocol;
using ChameLab.Services;
using ReactiveUI;

namespace ChameLab.ViewModels;

public sealed class WriterViewModel : PageViewModelBase
{
    private readonly CardLibrary _cards;
    public CardsViewModel Engine { get; }
    public ObservableCollection<CardSave> Sources { get; } = new();
    public WriterViewModel(DeviceService device, CardLibrary cards, DictionaryLibrary dictionaries)
        : base(() => Strings.Nav_Writer, "upload")
    {
        _cards = cards; Engine = new(device, cards, dictionaries) { IsStandaloneWriter = true };
        ImportCommand = ReactiveCommand.CreateFromTask(ImportAsync, Engine.WhenAnyValue(x => x.IsBusy, busy => !busy));
        cards.Changed += Refresh;
        Refresh();
    }
    public CardSave? Source
    {
        get;
        set
        {
            if (Engine.IsBusy || ReferenceEquals(field, value)) return;
            this.RaiseAndSetIfChanged(ref field, value);
            if (value is not null) Engine.SetWriteSource(value);
        }
    }
    public ICommand ImportCommand { get; }
    private void Refresh()
    {
        var selected = Source;
        Sources.Clear();
        foreach (var card in _cards.Cards.Where(c => c.Tag.CanWriteToTag())) Sources.Add(card);
        if (selected is not null && !Sources.Contains(selected)) Sources.Add(selected);
    }
    private Task ImportAsync() => RunAsync(async () =>
    {
        var file = await StorageService.OpenAsync(Strings.Cards_ImportCardTitle, CardFormats.ImportExtensions);
        if (file is null) return;
        var cards = CardFormats.Import(file.Value.Name, file.Value.Data).Where(c => c.Tag.CanWriteToTag()).ToArray();
        if (cards.Length == 0) throw new FormatException(Strings.Writer_NoSource);
        foreach (var card in cards) Sources.Add(card);
        Source = cards[0];
    });
    public override Task OnActivatedAsync()
    {
        if (Source is not null && !Engine.IsWriteOpen) Engine.SetWriteSource(Source);
        return Engine.OnActivatedAsync();
    }
    public override Task OnDeactivatedAsync() => Engine.OnDeactivatedAsync();
}

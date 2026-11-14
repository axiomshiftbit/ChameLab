using System;
using System.Collections.Generic;
using System.Linq;
using System.Reactive.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using ChameLab.Localization;
using ChameLab.Models;
using ChameLab.Services;
using ReactiveUI;

namespace ChameLab.ViewModels;

public sealed partial class CardsViewModel
{
    public ICommand ReadCardDataCommand { get; private set; } = null!;
    public ICommand ExportFoundKeysCommand { get; private set; } = null!;
    public ICommand SaveFoundKeysCommand { get; private set; } = null!;
    public ICommand AddFoundKeysCommand { get; private set; } = null!;

    public bool ShowReadDataActions => _classicDump is not null;
    public bool CanReadCardData => ShowReadDataActions && !IsDumpComplete && IsConnected && !IsBusy && FoundKeyCount > 0;
    public bool CanExportFoundKeys => ShowReadDataActions && !IsBusy && FoundKeyCount > 0;
    public int FoundKeyCount => CardKeys().Count;
    public string FoundKeyCountText => Loc.Plural(Strings.Plural_UniqueKeys, FoundKeyCount);
    public IReadOnlyList<KeyDictionary> KeyExportDictionaries => _dictionaries.Dictionaries
        .Where(d => d.KeySize == 6).OrderBy(d => d.Name, NaturalStringComparer.Instance).ToArray();

    private void InitializeReadDataActions()
    {
        ReadCardDataCommand = ReactiveCommand.CreateFromTask(ReadCardDataAsync,
            this.WhenAnyValue(x => x.CanReadCardData));
        ExportFoundKeysCommand = ReactiveCommand.CreateFromTask(ExportFoundKeysAsync,
            this.WhenAnyValue(x => x.CanExportFoundKeys));
        SaveFoundKeysCommand = ReactiveCommand.CreateFromTask(() => SaveFoundKeysAsync(null),
            this.WhenAnyValue(x => x.CanExportFoundKeys));
        AddFoundKeysCommand = ReactiveCommand.CreateFromTask<KeyDictionary>(SaveFoundKeysAsync,
            this.WhenAnyValue(x => x.CanExportFoundKeys));
        this.WhenAnyValue(x => x.IsConnected, x => x.IsBusy, x => x.IsDumpComplete)
            .Subscribe(_ => RaiseReadDataActions());
    }

    private void RaiseReadDataActions()
    {
        this.RaisePropertyChanged(nameof(ShowReadDataActions));
        this.RaisePropertyChanged(nameof(FoundKeyCount));
        this.RaisePropertyChanged(nameof(FoundKeyCountText));
        this.RaisePropertyChanged(nameof(CanReadCardData));
        this.RaisePropertyChanged(nameof(CanExportFoundKeys));
    }

    // Authenticated keys may exist before any trailer can be read. Saved trailers contribute
    // only their known fields: masked zero bytes must never become an exported key.
    private IReadOnlyList<byte[]> CardKeys()
    {
        if (_classicDump is not { } dump) return Array.Empty<byte[]>();
        return dump.KeyA.Concat(dump.KeyB).OfType<byte[]>()
            .Concat(Enumerable.Range(0, dump.SectorCount).SelectMany(dump.TrailerKeys))
            .Where(k => k.Length == 6).DistinctBy(Convert.ToHexString).Select(k => k.ToArray()).ToArray();
    }

    private Task ReadCardDataAsync()
    {
        if (!CanReadCardData) return Task.CompletedTask;
        var keys = CardKeys();
        return RunAwakeAsync(async () =>
        {
            if (_device.Client is not { } client || _classicDump is null) return;
            using var cts = new CancellationTokenSource();
            _dumpCts = cts;
            try
            {
                // The existing reader checks the UID, keeps read blocks and supports Stop.
                // Do not use BuildKeyDictionary here: this action is independent of its checkboxes.
                await MifareDumpBodyAsync(client, keys, _classicDump.Type, cts);
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
        });
    }

    private string CardKeysName => string.IsNullOrWhiteSpace(Form?.Name) ? Strings.Search_FoundDictionary : Form.Name;

    private Task ExportFoundKeysAsync()
    {
        if (!CanExportFoundKeys) return Task.CompletedTask;
        var dictionary = new KeyDictionary { Name = CardKeysName, Keys = CardKeys().ToList() };
        return RunAsync(async () =>
        {
            var file = await StorageService.SaveAsync(Strings.Cards_ExportFoundKeys,
                $"{SafeFileName(dictionary.Name)}.dic", Encoding.UTF8.GetBytes(dictionary.ToText()), "dic");
            if (file is not null) Notify(string.Format(Strings.Cards_KeysSaved, dictionary.Count, file));
        });
    }

    private Task SaveFoundKeysAsync(KeyDictionary? target)
    {
        if (!CanExportFoundKeys || (target is not null &&
            (target.KeySize != 6 || !_dictionaries.Dictionaries.Contains(target)))) return Task.CompletedTask;
        var keys = CardKeys();
        return RunAsync(() =>
        {
            if (target is null)
            {
                var baseName = CardKeysName;
                var name = baseName;
                for (var n = 2; _dictionaries.Dictionaries.Any(d => d.Name == name); n++) name = $"{baseName} ({n})";
                _dictionaries.Add(new KeyDictionary { Name = name, Keys = keys.ToList() });
                Notify(string.Format(Strings.Cards_KeysSaved, keys.Count, name));
            }
            else
            {
                // Keep its identity, folder, color and existing keys; append each new key once.
                var updated = KeyDictionary.FromJson(target.ToJson());
                var seen = updated.Keys.Select(Convert.ToHexString).ToHashSet(StringComparer.Ordinal);
                updated.Keys.AddRange(keys.Where(k => seen.Add(Convert.ToHexString(k))));
                _dictionaries.Update(updated);
                Notify(string.Format(Strings.Cards_KeysSaved, updated.Count - target.Count, target.Name));
            }
            return Task.CompletedTask;
        });
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Threading;
using ChameLab.Localization;
using ChameLab.Models;
using ChameLab.Protocol;
using ChameLab.Services;
using ReactiveUI;

namespace ChameLab.ViewModels;

public sealed record KeySearchOption(KeySearchKind Kind, string Name);

public sealed class KeySearchViewModel : PageViewModelBase
{
    private readonly DeviceService _device;
    private readonly DictionaryLibrary _dictionaries;
    private CancellationTokenSource? _cts;
    private Task? _running;
    private KeySearch? _search;
    private KeySearchTarget? _target;
    private string? _fingerprint;

    public KeySearchViewModel(DeviceService device, DictionaryLibrary dictionaries) : base(() => Strings.Search_Title, "tools")
    {
        _device = device; _dictionaries = dictionaries;
        var idle = this.WhenAnyValue(x => x.Working, working => !working);
        InspectCommand = ReactiveCommand.CreateFromTask(() => ExecuteAsync(false), idle);
        StartCommand = ReactiveCommand.CreateFromTask(() => ExecuteAsync(true), idle);
        StopCommand = ReactiveCommand.Create(() => _cts?.Cancel());
        ResetCommand = ReactiveCommand.Create(() => { _search = null; _target = null; Progress = ""; FoundKey = ""; }, idle);
        SaveKeyCommand = ReactiveCommand.Create(() =>
        {
            if (_search?.Found is not { } key) return;
            if (_dictionaries.LoadError is { } error) { ReportError(error); return; }
            _dictionaries.Add(new KeyDictionary { Name = Strings.Search_FoundDictionary, KeySize = key.Length, Keys = [key.ToArray()] });
            Notify(Strings.Search_KeySaved);
        }, idle);
    }
    public IReadOnlyList<KeySearchOption> Kinds => [new(KeySearchKind.MifareClassic, "MIFARE Classic"), new(KeySearchKind.NtagPassword, "NTAG / Ultralight EV1"), new(KeySearchKind.UltralightC, "Ultralight C (3DES)"), new(KeySearchKind.Em4x05, "EM4205 / EM4305")];
    public KeySearchOption Kind
    {
        get => field ??= Kinds[0];
        set
        {
            if (value is null || value.Kind == field?.Kind) return;
            this.RaiseAndSetIfChanged(ref field, value);
            this.RaisePropertyChanged(nameof(IsClassic)); this.RaisePropertyChanged(nameof(IsEm)); this.RaisePropertyChanged(nameof(Dictionaries));
            Dictionary = null; _search = null; _target = null; Target = ""; FoundKey = ""; Progress = "";
            First = new string('0', KeySize * 2); Last = new string('0', KeySize * 2 - 2) + "FF";
        }
    }
    public bool IsClassic => Kind.Kind == KeySearchKind.MifareClassic;
    public bool IsEm => Kind.Kind == KeySearchKind.Em4x05;
    private int KeySize => IsClassic ? 6 : Kind.Kind == KeySearchKind.UltralightC ? 16 : 4;
    public IReadOnlyList<string> Modes => [Strings.Search_Dictionary, Strings.Search_Range];
    public int Mode
    {
        get;
        set { this.RaiseAndSetIfChanged(ref field, value); this.RaisePropertyChanged(nameof(IsRange)); }
    }
    public bool IsRange => Mode == 1;
    public IReadOnlyList<KeyDictionary> Dictionaries => _dictionaries.Dictionaries.Where(d => d.KeySize == KeySize).ToArray();
    public KeyDictionary? Dictionary { get; set => this.RaiseAndSetIfChanged(ref field, value); }
    public bool IncludeDefaults { get; set; } = true;
    public string First { get; set => this.RaiseAndSetIfChanged(ref field, value); } = "000000000000";
    public string Last { get; set => this.RaiseAndSetIfChanged(ref field, value); } = "0000000000FF";
    public int Block { get; set; }
    public IReadOnlyList<string> KeyTypes => ["A", "B"];
    public int KeyTypeIndex { get; set; }
    public int AttemptBudget { get; set; } = 10000;
    public bool Working { get; private set => this.RaiseAndSetIfChanged(ref field, value); }
    public string Target { get; private set => this.RaiseAndSetIfChanged(ref field, value); } = "";
    public string Progress { get; private set => this.RaiseAndSetIfChanged(ref field, value); } = "";
    public double Percent { get; private set => this.RaiseAndSetIfChanged(ref field, value); }
    public string FoundKey { get; private set => this.RaiseAndSetIfChanged(ref field, value); } = "";
    public bool ShowKey { get; set { this.RaiseAndSetIfChanged(ref field, value); this.RaisePropertyChanged(nameof(KeyMask)); } }
    public char KeyMask => ShowKey ? '\0' : '●';
    public ICommand InspectCommand { get; }
    public ICommand StartCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand ResetCommand { get; }
    public ICommand SaveKeyCommand { get; }

    private Task ExecuteAsync(bool search) => _running = RunAsync(async () =>
    {
        using var cancellation = new CancellationTokenSource(); _cts = cancellation; Working = true;
        try
        {
            var client = _device.Client ?? throw new InvalidOperationException(Strings.Tool_device_required);
            if (Kind.Kind == KeySearchKind.UltralightC) _device.DeviceInfo?.Capabilities.Require(FirmwareFeature.UltralightCKeySearch);
            if (Kind.Kind == KeySearchKind.MifareClassic) _device.DeviceInfo?.Capabilities.Require(FirmwareFeature.ClassicKeySearch);
            var mode = await client.GetDeviceModeAsync(cancellation.Token);
            try
            {
                var target = await client.PrepareKeySearchAsync(Kind.Kind, Block, KeyTypeIndex == 0 ? MfKeyType.A : MfKeyType.B, cancellation.Token);
                if (_target is not null && !_target.Identity.AsSpan().SequenceEqual(target.Identity))
                { _search = null; FoundKey = ""; _target = null; throw new InvalidOperationException(Strings.Recovery_CardChanged); }
                _target = target;
                Target = string.Format(Strings.Search_Ready, Kind.Name, KeySize * 8);
                _device.Journal.Add("INFO", "Key search: target detected; " + Kind.Kind);
                if (!search) return;
                KeySearchCandidates candidates;
                string fingerprint;
                if (IsRange)
                {
                    var first = Hex.Parse(First); var last = Hex.Parse(Last);
                    if (first.Length != KeySize || last.Length != KeySize) throw new FormatException(Strings.Search_KeySize);
                    candidates = KeySearchCandidates.Range(first, last);
                    fingerprint = "range:" + Convert.ToHexString(first) + ":" + Convert.ToHexString(last);
                }
                else
                {
                    var keys = (Dictionary?.Keys ?? []).Concat(IsClassic && IncludeDefaults ? MifareClassic.DefaultKeys : []);
                    candidates = KeySearchCandidates.Dictionary(keys, KeySize);
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    for (ulong i = 0; i < candidates.Count; i++) hash.AppendData(candidates.At(i));
                    fingerprint = Convert.ToHexString(hash.GetHashAndReset());
                }
                fingerprint += $":{Kind.Kind}:{Block}:{KeyTypeIndex}";
                if (_search is null || _fingerprint != fingerprint) { _search = new KeySearch(candidates); FoundKey = ""; Percent = 0; }
                _fingerprint = fingerprint;
                _device.Journal.Add("INFO", $"Key search: start; completed={_search.Completed}; total={candidates.Count}; budget={AttemptBudget}");
                var progress = new Progress<KeySearchProgress>(p =>
                {
                    Progress = string.Format(Strings.Search_Progress, p.Completed, p.Total, p.AttemptsPerSecond, p.Elapsed);
                    Percent = p.Total == 0 ? 0 : (double)p.Completed / p.Total * 100;
                });
                await _search.RunAsync((key, ct) => client.CheckSearchCandidateAsync(target, key, ct), AttemptBudget, progress, cancellation.Token);
                if (_search.Found is { } found)
                {
                    FoundKey = Convert.ToHexString(found);
                    Target = Strings.Search_Found;
                    _device.Journal.Add("INFO", $"Key search: authentication verified; attempts={_search.Completed}");
                }
                else
                {
                    Target = _search.Finished ? Strings.Search_NotFound : Strings.Search_BudgetReached;
                    _device.Journal.Add("INFO", $"Key search: {(_search.Finished ? "exhausted" : "paused")}; attempts={_search.Completed}");
                }
            }
            finally
            {
                if (!client.Connection.IsClosed) await client.SetDeviceModeAsync(mode);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        { Target = Strings.Search_Stopped; _device.Journal.Add("INFO", $"Key search: cancelled; attempts={_search?.Completed ?? 0}"); }
        catch (Exception ex)
        { _device.Journal.Add("ERROR", "Key search: " + ex.GetType().Name); throw; }
        finally { _cts = null; Working = false; }
    });
    public override async Task OnDeactivatedAsync()
    {
        _cts?.Cancel();
        if (_running is not null) await _running;
    }
}

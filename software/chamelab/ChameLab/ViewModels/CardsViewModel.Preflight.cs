using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using ChameLab.Models;
using ChameLab.Protocol;
using ChameLab.Localization;
using ChameLab.Services;
using ReactiveUI;

namespace ChameLab.ViewModels;

public sealed partial class CardsViewModel
{
    public bool IsStandaloneWriter { get; set; }
    private WriteTargetInspection? _writeInspection;
    private byte[]? _lfTarget;
    private CancellationTokenSource? _inspectCts;
    private Task? _writeTask;
    private Task? _inspectTask;
    public JournalViewModel Journal { get; private set; } = null!;
    public ICommand TestScanLfCommand { get; private set; } = null!;
    public ICommand InspectBlankCommand { get; private set; } = null!;
    public ICommand StopInspectionCommand { get; private set; } = null!;
    public string TargetSummary { get; private set => this.RaiseAndSetIfChanged(ref field, value); } = Strings.Writer_ScanFirst;
    public bool TargetConfirmed { get; set => this.RaiseAndSetIfChanged(ref field, value); }
    public bool HasWriteTarget { get; private set => this.RaiseAndSetIfChanged(ref field, value); }
    public bool HasWriterSource => ActionTag is not null;
    public bool CanConfirmTarget => (HasWriteTarget || IsLfWrite && LfBlankAcknowledged) && !IsBusy;
    public bool IsInspecting { get; private set => this.RaiseAndSetIfChanged(ref field, value); }
    public bool LfBlankAcknowledged { get; set { this.RaiseAndSetIfChanged(ref field, value); this.RaisePropertyChanged(nameof(CanConfirmTarget)); } }
    public string? Gen4Password
    {
        get;
        set
        {
            if (field == value) return;
            this.RaiseAndSetIfChanged(ref field, value);
            ResetTargetInspection();
        }
    }
    private byte[] ParseGen4Password()
    {
        if (string.IsNullOrWhiteSpace(Gen4Password)) return new byte[4];
        if (!Hex.TryParse(Gen4Password, out var password) || password.Length != 4)
            throw new FormatException(Strings.Common_Password4Bytes);
        return password;
    }

    private void ResetTargetInspection()
    {
        _writeInspection = null; _lfTarget = null;
        TargetConfirmed = HasWriteTarget = LfBlankAcknowledged = false;
        TargetSummary = Strings.Writer_ScanFirst;
    }

    public void SetWriteSource(CardSave card)
    {
        if (IsBusy) return;
        Action = CardAction.None;
        _pane = CardsPane.Add;
        Form = new CardEditorViewModel(card.Clone());
        RaiseCardInfo();
        Toggle(CardAction.Write);
        this.RaisePropertyChanged(nameof(HasWriterSource));
    }

    private Task TestScanLfAsync() => RunAsync(async () =>
    {
        if (_device.Client is not { } client) return;
        TargetSummary = Strings.Writer_ScanFirst;
        var previous = await client.GetDeviceModeAsync();
        try
        {
            await client.SetDeviceModeAsync(DeviceMode.Reader);
            if (LfProtocol.IsEm410x()) await client.Em410xScanAsync(); else await client.LfScanAsync(LfProtocol);
            TargetSummary = Strings.Writer_LfFound;
            _device.Journal.Add("INFO", Strings.Writer_LfFound);
            ScanFeedback.Found();
        }
        catch (ChameleonException ex) when (ex.Status is Status.LfTagNoFound or Status.HfTagNo)
        { TargetSummary = Strings.Writer_NotFound; ScanFeedback.Missed(); }
        finally { if (!client.Connection.IsClosed) await client.SetDeviceModeAsync(previous); }
    });

    private Task InspectTargetAsync(bool memory) => _inspectTask = RunAsync(async () =>
    {
        if (_device.Client is not { } client) return;
        ResetTargetInspection();
        using var cts = new CancellationTokenSource();
        var previousMode = await client.GetDeviceModeAsync(cts.Token);
        _inspectCts = cts;
        IsInspecting = true;
        _device.Journal.Add("INFO", Strings.Writer_InspectStarted);
        try
        {
            await client.SetDeviceModeAsync(DeviceMode.Reader, cts.Token);
            if (IsLfWrite && ActionTag is { } lfType)
            {
                _lfTarget = lfType.IsEm410x() ? (await client.Em410xScanAsync(cts.Token)).Id
                    : (await client.LfScanAsync(lfType, cts.Token)).Id;
                HasWriteTarget = true;
                TargetSummary = Strings.Writer_LfFound;
            }
            else
            {
                var password = ParseGen4Password();
                var inspection = await client.InspectWriteTargetAsync(memory, password, cts.Token);
                if (inspection is null) { TargetSummary = Strings.Writer_NotFound; _device.Journal.Add("INFO", Strings.Journal_NoTag); return; }
                if (_writeSession is null)
                {
                    if (inspection.Gen4) SelectedGeneration = MagicGeneration.Gen4;
                    else if (inspection.Generation is { } gen) SelectedGeneration = gen;
                    else if (inspection.Type.IsMifareClassic()) { SelectedGeneration = MagicGeneration.Gen2; WriteGen2Block0 = false; }
                }
                _writeInspection = inspection;
                HasWriteTarget = CompatibleTarget(inspection);
                var kind = inspection.Gen4 ? "Gen4 GTU / Ultimate"
                    : inspection.Generation?.Label() ?? Strings.Writer_GenerationUnknown;
                TargetSummary = $"{Strings.Writer_Found}: {inspection.Type.DisplayName()}\n{kind}";
                if (inspection.Memory is { } dump)
                {
                    TargetSummary += "\n" + string.Format(Strings.Writer_MemoryResult, inspection.ReadableDataBlocks,
                        inspection.TotalDataBlocks, inspection.FactoryKeySectors, dump.SectorCount);
                    TargetSummary += "\n" + (inspection.DataIsZeroed switch
                    { true => Strings.Writer_DataZero, false => Strings.Writer_DataPresent, _ => Strings.Writer_DataUnknown });
                    if (_writeSession is null) WriteRecovery.AcceptTarget(dump);
                }
                else if (memory) TargetSummary += "\n" + Strings.Writer_DataUnknown;
                if (!HasWriteTarget) TargetSummary += "\n" + Strings.Writer_Incompatible;
                _device.Journal.Add("INFO", $"{Strings.Writer_Found}: {inspection.Type.DisplayName()}; {kind}");
            }
            ScanFeedback.Found();
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        { TargetSummary = Strings.Journal_Cancelled; _device.Journal.Add("CANCEL", Strings.Journal_Cancelled); }
        catch (ChameleonException ex) when (ex.Status is Status.HfTagNo or Status.LfTagNoFound)
        { TargetSummary = Strings.Writer_NotFound; ScanFeedback.Missed(); }
        finally
        {
            _inspectCts = null; IsInspecting = false;
            if (!client.Connection.IsClosed) await client.SetDeviceModeAsync(previousMode);
            this.RaisePropertyChanged(nameof(CanConfirmTarget));
        }
    });

    private bool CompatibleTarget(WriteTargetInspection target) => ActionTag switch
    {
        null => true,
        { } type when type.IsMifareClassic() => target.Type.IsMifareClassic()
            && target.Type.MifareClassicBlocks() >= type.MifareClassicBlocks(),
        { } type when type.IsUltralight() => target.Type.IsUltralight()
            && target.Type.UltralightPages() >= type.UltralightPages(),
        _ => false,
    };

    private async Task CheckWriteTargetAsync(ChameleonClient client, CancellationToken ct)
    {
        if (!TargetConfirmed) throw new InvalidOperationException(Strings.Writer_ScanFirst);
        await client.SetDeviceModeAsync(DeviceMode.Reader, ct);
        if (IsLfWrite)
        {
            if (LfBlankAcknowledged) return; // Empty LF tags may not emit a supported ID until programmed.
            if (_lfTarget is null || ActionTag is not { } type) throw new InvalidOperationException(Strings.Writer_ScanFirst);
            var id = type.IsEm410x() ? (await client.Em410xScanAsync(ct)).Id : (await client.LfScanAsync(type, ct)).Id;
            if (!id.AsSpan().SequenceEqual(_lfTarget)) throw new InvalidOperationException(Strings.Write_TargetUidChanged);
            return;
        }
        if (_writeInspection is not { } inspected || !CompatibleTarget(inspected))
            throw new InvalidOperationException(Strings.Writer_ScanFirst);
        var tags = await client.Hf14aScanAsync(ct);
        var expected = _writeSession?.TargetKeys?.Uid ?? inspected.Tag.Uid;
        if (tags.Count != 1 || !tags[0].Uid.AsSpan().SequenceEqual(expected))
        { ResetTargetInspection(); throw new InvalidOperationException(Strings.Write_TargetUidChanged); }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Threading;
using ChameLab.Localization;
using ChameLab.Models;
using ChameLab.Services;
using ReactiveUI;

namespace ChameLab.ViewModels;

public sealed partial class CardsViewModel
{
    public ICommand CompareCommand { get; private set; } = null!;
    public string? ComparisonSummary { get; private set; }
    public IReadOnlyList<ScanResultRow> ComparisonRows { get; private set; } = Array.Empty<ScanResultRow>();

    private void ClearComparison()
    {
        if (ComparisonSummary is null) return;
        ComparisonSummary = null;
        ComparisonRows = Array.Empty<ScanResultRow>();
        this.RaisePropertyChanged(nameof(ComparisonSummary));
        this.RaisePropertyChanged(nameof(ComparisonRows));
    }

    private Task CompareAsync() => RunAsync(async () =>
    {
        var form = _form;
        if (form is null) return;
        ClearComparison();
        var picked = await StorageService.OpenAsync(Strings.Cards_CompareFile, CardFormats.ImportExtensions);
        if (picked is not { } file || !ReferenceEquals(form, _form)) return;
        var cards = CardFormats.Import(file.Name, file.Data);
        if (cards.Count != 1) throw new FormatException(Strings.Cards_CompareSingle);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (!ReferenceEquals(form, _form)) return;
            CompareWithCard(cards[0]);
        });
    });

    public void CompareWithCard(CardSave reference)
    {
        ClearComparison();
        if (_form is null) return;
        // Same validated snapshot as export/write; unknown-key markers follow edits.
        var current = _form.Build();
        if (!MifareDumpComparison.CanCompare(current, reference))
            throw new FormatException(Strings.Cards_CompareType);
        var result = MifareDumpComparison.Compare(current, reference);
        ComparisonSummary = string.Format(Strings.Cards_CompareSummary,
            result.IdentityMatches ? Strings.Cards_CompareIdentitySame : Strings.Cards_CompareIdentityDifferent,
            result.EqualBytes, result.DifferentBytes, result.UnknownBytes);
        ComparisonRows = result.Blocks.Select(block => new ScanResultRow(
            string.Format(Strings.Cards_CompareBlock, block.Block, MifareClassic.SectorOfBlock(block.Block)),
            string.Format(Strings.Cards_CompareBytes, block.DifferentBytes, block.UnknownBytes))).ToArray();
        this.RaisePropertyChanged(nameof(ComparisonSummary));
        this.RaisePropertyChanged(nameof(ComparisonRows));
    }
}

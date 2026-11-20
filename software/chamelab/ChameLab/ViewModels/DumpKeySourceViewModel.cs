using System.Collections.Generic;
using ReactiveUI;
using ChameLab.Localization;

namespace ChameLab.ViewModels;

/// <summary>One key list offered before a dump (the built-in keys or a saved dictionary), with a check box.</summary>
public sealed class DumpKeySourceViewModel(string id, string name, IReadOnlyList<byte[]> keys, bool isSelected,
    string? color = null, bool separatorAfter = false, string folder = "")
    : ReactiveObject
{
    /// <summary>Dictionary id, or <see cref="BuiltInId"/> for the built-in keys; keeps the choice across rebuilds.</summary>
    public string Id { get; } = id;

    public string Name { get; } = name;
    public Avalonia.Media.IBrush? ColorBrush => Converters.HexBrushConverter.Brush(color);
    public bool SeparatorAfter { get; } = separatorAfter;

    /// <summary>The path of the dictionary's folder (see <see cref="Models.FolderPath"/>); empty for none and for the special sources.</summary>
    public string Folder { get; } = folder;
    public bool IsKnownCardKeys => Id == Services.KnownCardKeys.Id;
    public IReadOnlyList<byte[]> Keys { get; } = keys;
    public int KeyCount => Keys.Count;
    public string CountText => Loc.Plural(Strings.Plural_Keys, Keys.Count);

    public bool IsSelected
    {
        get;
        set => this.RaiseAndSetIfChanged(ref field, value);
    } = isSelected;

    public const string BuiltInId = "";
}

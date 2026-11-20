using System;
using System.Linq;
using Avalonia;
using ChameLab.Models;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace ChameLab.Controls;

/// <summary>
/// A block as spaced hex, with the bytes that differ from <see cref="Other"/> on a highlight.
/// Without <see cref="Bytes"/> it shows <see cref="MissingText"/> instead, dimmed.
/// </summary>
public class HexDiffText : SelectableTextBlock
{
    public static readonly StyledProperty<DumpByteState[]?> StatesProperty =
        AvaloniaProperty.Register<HexDiffText, DumpByteState[]?>(nameof(States));
    public DumpByteState[]? States { get => GetValue(StatesProperty); set => SetValue(StatesProperty, value); }

    public static readonly StyledProperty<byte[]?> BytesProperty =
        AvaloniaProperty.Register<HexDiffText, byte[]?>(nameof(Bytes));

    public static readonly StyledProperty<byte[]?> OtherProperty =
        AvaloniaProperty.Register<HexDiffText, byte[]?>(nameof(Other));

    public static readonly StyledProperty<string?> MissingTextProperty =
        AvaloniaProperty.Register<HexDiffText, string?>(nameof(MissingText));

    public static readonly StyledProperty<IBrush?> HighlightProperty =
        AvaloniaProperty.Register<HexDiffText, IBrush?>(nameof(Highlight),
            new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0x98, 0x00)));

    protected override Type StyleKeyOverride => typeof(SelectableTextBlock);

    public HexDiffText()
    {
        FontFamily = AppFonts.Mono;
        TextWrapping = TextWrapping.Wrap;
    }

    public byte[]? Bytes { get => GetValue(BytesProperty); set => SetValue(BytesProperty, value); }
    public byte[]? Other { get => GetValue(OtherProperty); set => SetValue(OtherProperty, value); }
    public string? MissingText { get => GetValue(MissingTextProperty); set => SetValue(MissingTextProperty, value); }
    public IBrush? Highlight { get => GetValue(HighlightProperty); set => SetValue(HighlightProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == StatesProperty || change.Property == BytesProperty || change.Property == OtherProperty
            || change.Property == MissingTextProperty || change.Property == HighlightProperty)
            Rebuild();
    }

    private void Rebuild()
    {
        var inlines = new InlineCollection();
        if (Bytes is not { } bytes)
        {
            inlines.Add(new Run(MissingText ?? "-") { FontStyle = FontStyle.Italic, Foreground = Brushes.Gray });
            Inlines = inlines;
            return;
        }

        for (var i = 0; i < bytes.Length; i++)
        {
            var state = States is { } states && i < states.Length ? states[i]
                : Differs(bytes, Other, i) ? DumpByteState.Different : DumpByteState.Equal;
            if (i > 0) inlines.Add(new Run(" "));
            var run = new Run(state == DumpByteState.Unknown ? "??" : bytes[i].ToString("X2"));
            if (state == DumpByteState.Unknown) run.Foreground = Brushes.Gray;
            else if (state == DumpByteState.Different) { run.Background = Highlight; run.FontWeight = FontWeight.SemiBold; }
            inlines.Add(run);
        }
        Inlines = inlines;
    }

    private static bool Differs(byte[] bytes, byte[]? other, int i) =>
        other is null || i >= other.Length || other[i] != bytes[i];

    private static string Hex(byte[] bytes, int start, int count) =>
        string.Join(' ', Convert.ToHexString(bytes, start, count).Chunk(2).Select(c => new string(c)));
}

using System;
using System.Globalization;
using System.Linq;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ChameLab.Localization;
using ChameLab.Models;

namespace ChameLab.Controls;

/// <summary>Discoverable touch/desktop actions shared by memory and dictionary editors.</summary>
public partial class HexEditorToolbar : UserControl
{
    public static readonly StyledProperty<HexEditor?> EditorProperty =
        AvaloniaProperty.Register<HexEditorToolbar, HexEditor?>(nameof(Editor));
    public HexEditor? Editor { get => GetValue(EditorProperty); set => SetValue(EditorProperty, value); }
    private HexEditor? _observed;
    private HexBuffer? _buffer;
    private bool _attached;

    public HexEditorToolbar() => InitializeComponent();

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == EditorProperty && _attached) Observe();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        Observe();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attached = false;
        Observe();
        base.OnDetachedFromVisualTree(e);
    }

    private void Observe()
    {
        if (_observed is not null)
        {
            _observed.PropertyChanged -= OnEditorChanged;
            _observed.RemoveHandler(KeyDownEvent, OnEditorKeyDown);
        }
        if (_buffer is not null) _buffer.Changed -= OnBufferChanged;
        _observed = _attached ? Editor : null;
        _buffer = _observed?.Buffer;
        if (_observed is not null)
        {
            _observed.PropertyChanged += OnEditorChanged;
            _observed.AddHandler(KeyDownEvent, OnEditorKeyDown, RoutingStrategies.Tunnel);
        }
        if (_buffer is not null) _buffer.Changed += OnBufferChanged;
        Refresh(selectionChanged: true);
    }

    private void OnEditorChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == HexEditor.BufferProperty) Observe();
        else if (e.Property == HexEditor.SelectionStatusProperty) Refresh(selectionChanged: true);
        else if (e.Property == HexEditor.OperationMessageProperty) Refresh();
    }
    private void OnBufferChanged(object? sender, EventArgs e) => Refresh();

    private void Refresh(bool selectionChanged = false)
    {
        var editor = _observed;
        var buffer = _buffer;
        var hasData = buffer is { Length: > 0 };
        CopyButton.IsEnabled = RowButton.IsEnabled = AllButton.IsEnabled = hasData;
        PasteButton.IsVisible = UndoButton.IsVisible = RedoButton.IsVisible = buffer?.IsReadOnly != true;
        PasteButton.IsEnabled = buffer is { IsReadOnly: false, ViewLength: > 0 };
        UndoButton.IsEnabled = editor?.CanUndo == true;
        RedoButton.IsEnabled = editor?.CanRedo == true;
        Navigation.IsEnabled = hasData;
        RangeText.Text = buffer is null ? null : string.Format(Strings.Hex_AddressRange,
            buffer.ByteOffset.ToString("X"), (buffer.ByteOffset + Math.Max(0, buffer.Length - 1)).ToString("X"));
        if (selectionChanged && editor is not null && buffer is not null)
        {
            if (!AddressBox.IsKeyboardFocusWithin) AddressBox.Text = $"0x{buffer.ByteOffset + editor.SelectionStart:X}";
            if (!CountBox.IsKeyboardFocusWithin) CountBox.Text = Math.Max(1, editor.SelectionLength).ToString(CultureInfo.InvariantCulture);
        }
        OperationStatus.Text = editor?.OperationMessage;
        OperationStatus.IsVisible = !string.IsNullOrEmpty(OperationStatus.Text);
        OperationStatus.Classes.Set("error", editor?.OperationFailed == true);
    }

    private async void OnCopy(object? sender, RoutedEventArgs e) { if (Editor is { } editor) await editor.CopyAsync(); }
    private async void OnPaste(object? sender, RoutedEventArgs e)
    {
        if (Editor is { } editor && await editor.PasteAsync()) editor.Focus();
    }
    private void OnUndo(object? sender, RoutedEventArgs e) { Editor?.Undo(); Refresh(); }
    private void OnRedo(object? sender, RoutedEventArgs e) { Editor?.Redo(); Refresh(); }
    private void OnSelectRow(object? sender, RoutedEventArgs e) { Editor?.SelectRow(); }
    private void OnSelectAll(object? sender, RoutedEventArgs e) { Editor?.SelectAll(); }

    private void OnGoTo(object? sender, RoutedEventArgs e)
    {
        if (TryAddress(out var offset)) { Editor!.GoTo(offset); Editor.ReportOperation(null); Editor.Focus(); }
    }
    private bool TryAddress(out long offset)
    {
        offset = 0;
        var text = AddressBox.Text?.Trim() ?? "";
        var hex = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        if (_buffer is { } buffer && long.TryParse(hex ? text[2..] : text,
                hex ? NumberStyles.AllowHexSpecifier : NumberStyles.None, CultureInfo.InvariantCulture, out var address)
            && address >= buffer.ByteOffset && address - buffer.ByteOffset < buffer.Length)
        { offset = address - buffer.ByteOffset; return true; }
        Editor?.ReportOperation(Strings.Hex_InvalidRange, true);
        return false;
    }
    private void OnSelectRange(object? sender, RoutedEventArgs e)
    {
        if (!TryAddress(out var offset)) return;
        if (!int.TryParse(CountBox.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var count)
            || Editor?.SelectRange(offset, count) != true)
        { Editor?.ReportOperation(Strings.Hex_InvalidRange, true); return; }
        Editor.ReportOperation(null);
    }
    private void OnFind(object? sender, RoutedEventArgs e)
    {
        byte[] pattern;
        var text = SearchBox.Text ?? "";
        if (SearchMode.SelectedIndex == 1 && text.Length > 0 && text.All(c => c <= 127)) pattern = Encoding.ASCII.GetBytes(text);
        else if (SearchMode.SelectedIndex == 1 || !Hex.TryParse(text, out pattern) || pattern.Length == 0)
        { Editor?.ReportOperation(Strings.Hex_InvalidPaste, true); return; }
        var found = Editor?.FindNext(pattern) ?? -1;
        Editor?.ReportOperation(found < 0 ? Strings.Hex_NoMatch
            : string.Format(Strings.Hex_MatchAt, ((_buffer?.ByteOffset ?? 0) + found).ToString("X")), found < 0);
    }
    private void OnAddressKeyDown(object? sender, KeyEventArgs e)
    { if (e.Key == Key.Enter) { OnGoTo(sender, e); e.Handled = true; } }
    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    { if (e.Key is Key.Enter or Key.F3) { OnFind(sender, e); e.Handled = true; } }
    private void OnEditorKeyDown(object? sender, KeyEventArgs e)
    {
        var command = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        if (command && e.Key is Key.F or Key.G)
        {
            Navigation.IsExpanded = true;
            var input = e.Key == Key.F ? SearchBox : AddressBox;
            // The expander creates/shows its content after the current input event.
            Dispatcher.UIThread.Post(() =>
            {
                if (!_attached || !Navigation.IsExpanded) return;
                UpdateLayout();
                input.Focus();
                input.BringIntoView();
            }, DispatcherPriority.Loaded);
            e.Handled = true;
        }
        else if (e.Key == Key.F3 && e.KeyModifiers == KeyModifiers.None) { OnFind(sender, e); e.Handled = true; }
    }
}

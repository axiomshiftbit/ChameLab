using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Input.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ChameLab.Controls;
using ChameLab.UiTesting;
using Xunit;

namespace ChameLab.UiTests;

public class HexEditorToolsTests
{
    private sealed class DeferredExpansion : IPageTransition
    {
        private readonly TaskCompletionSource _completion = new();
        private Visual? _content;

        public Task Start(Visual? from, Visual? to, bool forward, CancellationToken cancellationToken)
        {
            _content = to!;
            _content.IsVisible = false;
            return _completion.Task;
        }

        public void Complete()
        {
            _content!.IsVisible = true;
            _completion.SetResult();
        }
    }

    private static Window Show(HexEditor editor)
    {
        var window = new Window { Width = 360, Height = 800, Content = editor };
        window.Show(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
        return window;
    }

    [AvaloniaFact]
    public async Task ExactSelectionAtomicPasteAndUndoWorkWithoutKeyboard()
    {
        var buffer = new HexBuffer([new byte[] { 1, 2, 3, 4 }], 4);
        var editor = new HexEditor { Buffer = buffer };
        var window = Show(editor);
        try
        {
            Assert.True(editor.SelectRange(1, 1));
            Assert.Equal(1, editor.SelectionLength);
            Assert.True(await editor.CopyAsync());
            Assert.Equal("02", await window.Clipboard!.TryGetTextAsync());
            Assert.False(editor.PasteText("AA BB"));
            Assert.True(editor.OperationFailed);
            Assert.False(buffer.CanUndo);
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, buffer.Rows[0]);
            Assert.True(editor.PasteText("AA"));
            Assert.True(editor.CanUndo);
            Assert.Equal(new byte[] { 1, 0xAA, 3, 4 }, buffer.Rows[0]);
            editor.Undo(); Assert.Equal(2, buffer[1L]);
            editor.Redo(); Assert.Equal(0xAA, buffer[1L]);
            editor.GoTo(3);
            Assert.False(editor.PasteText("12 34"));
            Assert.False(editor.PasteText("??"));
            Assert.False(editor.PasteText("A"));
            Assert.False(editor.PasteText("Ж", ascii: true));
            Assert.Equal(4, buffer[3L]);
            editor.Buffer = new HexBuffer([new byte[4]], 4) { IsReadOnly = true };
            Assert.False(editor.PasteText("AA"));
            Assert.False(editor.CanUndo);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData("buffer")]
    [InlineData("selection")]
    [InlineData("edit")]
    public async Task DelayedClipboardNeverPastesIntoChangedTarget(string change)
    {
        var buffer = new HexBuffer([new byte[4]], 4);
        var editor = new HexEditor { Buffer = buffer };
        var window = Show(editor);
        try
        {
            var clipboard = new TaskCompletionSource<string?>();
            var paste = editor.PasteAsync(() => clipboard.Task);
            if (change == "buffer") editor.Buffer = new HexBuffer([new byte[4]], 4);
            if (change == "selection") editor.SelectRange(1, 2);
            if (change == "edit") buffer.Write(3, [9]);
            clipboard.SetResult("A1 B2");
            Assert.False(await paste);
            Assert.True(editor.OperationFailed);
            Assert.Equal(0, editor.Buffer![0L]);
            Assert.Equal(0, editor.Buffer[1L]);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void SelectAllExcludesAppendRowAndFindSupportsSingleByteMatches()
    {
        var editor = new HexEditor { Buffer = new HexBuffer([new byte[] { 1, 2, 1, 2 }], 4) { CanResize = true } };
        var window = Show(editor);
        try
        {
            editor.SelectAll(); Assert.Equal(4, editor.SelectionLength);
            editor.GoTo(0);
            Assert.Equal(0, editor.FindNext([1]));
            Assert.Equal(1, editor.SelectionLength);
            Assert.Equal(2, editor.FindNext([1]));
            Assert.Equal(0, editor.FindNext([1]));
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(Key.F, "SearchBox")]
    [InlineData(Key.G, "AddressBox")]
    public async Task KeyboardNavigationFocusesAfterExpansionCompletes(Key key, string inputName)
    {
        var editor = new HexEditor { Buffer = new HexBuffer([new byte[4]], 4), Height = 200 };
        var toolbar = new HexEditorToolbar { Editor = editor };
        var window = new Window { Width = 360, Height = 800,
            Content = new StackPanel { Children = { toolbar, editor } } };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var navigation = toolbar.FindControl<Expander>("Navigation")!;
            var transition = new DeferredExpansion();
            navigation.ContentTransition = transition;
            var expanded = new TaskCompletionSource();
            navigation.Expanded += (_, _) => expanded.TrySetResult();
            Assert.True(editor.Focus());
            editor.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent,
                Key = key, KeyModifiers = KeyModifiers.Control });
            Dispatcher.UIThread.RunJobs();
            var input = toolbar.FindControl<TextBox>(inputName)!;
            Assert.False(input.IsFocused);
            transition.Complete();
            await expanded.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(input.IsFocused);

            // The shortcut must also work when the panel is already open.
            Assert.True(editor.Focus());
            editor.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent,
                Key = key, KeyModifiers = KeyModifiers.Control });
            Dispatcher.UIThread.RunJobs();
            Assert.True(input.IsFocused);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(320, 1.5, true)]
    [InlineData(360, 1.0, false)]
    [InlineData(800, 1.5, false)]
    public void ToolbarUsesAbsoluteAddressesAndFitsNarrowScreens(int width, double scale, bool dark)
    {
        Application.Current!.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
        var scene = LayoutScenarios.Create("card-access", "ru");
        var view = (ChameLab.Views.CardsView)scene.Child!;
        var window = new Window { Width = width, Height = 800, Content = scene };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var editor = view.GetVisualDescendants().OfType<HexEditor>().Single(c => c.Name == "MemoryEditor");
            var toolbar = view.GetVisualDescendants().OfType<HexEditorToolbar>().Single();
            editor.Focus();
            editor.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.F, KeyModifiers = KeyModifiers.Control });
            Dispatcher.UIThread.RunJobs();
            Assert.True(toolbar.FindControl<Expander>("Navigation")!.IsExpanded);
            var search = toolbar.FindControl<TextBox>("SearchBox")!;
            Assert.True(search.IsFocused, $"Search focus: visible={search.IsEffectivelyVisible}, "
                + $"enabled={search.IsEffectivelyEnabled}, loaded={search.IsLoaded}, bounds={search.Bounds}; "
                + $"focused={window.FocusManager?.GetFocusedElement()?.GetType().Name}");
            window.UpdateLayout();
            LayoutScenarios.ScaleText(scene, scale); window.UpdateLayout();
            Assert.Contains("800", toolbar.FindControl<TextBlock>("RangeText")!.Text);
            var address = toolbar.FindControl<TextBox>("AddressBox")!;
            address.Text = "0x801";
            toolbar.FindControl<TextBox>("CountBox")!.Text = "2";
            // Actual button interaction is exercised via its routed Click event.
            var button = toolbar.FindControl<Button>("SelectRangeButton")!;
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(1, editor.SelectionStart);
            Assert.Equal(2, editor.SelectionLength);
            var find = toolbar.FindControl<Button>("FindButton")!;
            toolbar.FindControl<TextBox>("SearchBox")!.Text = "FF 07 80";
            find.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(15 * 16 + 6, editor.SelectionStart);
            Assert.Equal(3, editor.SelectionLength);
            Assert.False(editor.OperationFailed);
            var status = toolbar.FindControl<TextBlock>("OperationStatus")!;
            var successBrush = status.Foreground;
            address.Text = "-1";
            toolbar.FindControl<Button>("GoToButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(editor.OperationFailed);
            Assert.NotEqual(successBrush, status.Foreground);
            Assert.Equal("-1", address.Text); // Keep bad input available for correction.
            Assert.Empty(LayoutScenarios.FindOverflow(scene));
            editor.Buffer = new HexBuffer([new byte[4]], 4) { IsReadOnly = true };
            Assert.False(toolbar.FindControl<Button>("PasteButton")!.IsVisible);
            Assert.False(toolbar.FindControl<Button>("UndoButton")!.IsVisible);
            Assert.True(toolbar.FindControl<Button>("CopyButton")!.IsEnabled);
        }
        finally { window.Close(); }
    }
}

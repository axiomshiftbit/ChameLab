using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using System.Linq;
using Avalonia.VisualTree;
using Avalonia.Threading;
using ChameLab.Behaviors;
using ChameLab.ViewModels;

namespace ChameLab.Views;

public partial class CardsView : UserControl
{
    private ScrollViewer? _memoryScroll;
    private Border? _memoryCard;
    public CardsView()
    {
        InitializeComponent();
        // Opening cards, and dragging them into place in manual order.
        _ = new ListReorder(CardList, ListScroll);
        _ = new FolderScrollMemory(this, dc => (dc as CardsViewModel)?.FolderTree, ListPaneScroll, ListScroll);
    }

    private void OnMemoryLoaded(object? sender, RoutedEventArgs e)
    {
        if (sender is not Border card || ReferenceEquals(card, _memoryCard)) return;
        _memoryCard = card;
        _memoryScroll = card.GetVisualDescendants().OfType<ScrollViewer>().Single(c => c.Name == "MemoryScroll");
        _memoryScroll.LayoutUpdated += (_, _) => FitMemoryScroll();
        _memoryScroll.AddHandler(RequestBringIntoViewEvent, OnMemoryBringIntoView, handledEventsToo: true);
        var ndefList = card.GetLogicalDescendants().OfType<ListBox>().Single(c => c.Name == "NdefList");
        _ = new ListReorder(ndefList, PaneScroll);
    }

    // ---- Memory editor ---------------------------------------------------------

    /// <summary>
    /// The memory scroll has already brought the caret into view; the request must
    /// not travel on to the pane scroll, which would scroll to the caret's position
    /// as if the inner scroll weren't there (jumping up to the card details). The
    /// pane shows the part of the memory scroll where the caret now is: on a phone
    /// with the keyboard open the pane can be shorter than the memory scroll, and
    /// showing all of it would leave a caret low in it under the keyboard. Once laid
    /// out, so the inner scroll's new offset is in place.
    /// </summary>
    private void OnMemoryBringIntoView(object? sender, RequestBringIntoViewEventArgs e)
    {
        if (e.TargetObject == _memoryScroll) return;
        e.Handled = true;
        if (_memoryScroll is null) return;
        if (e.TargetObject is not { } target)
        {
            _memoryScroll.BringIntoView();
            return;
        }
        var rect = e.TargetRect;
        Dispatcher.UIThread.Post(() =>
        {
            var shown = new Rect(_memoryScroll.Bounds.Size);
            var spot = target.TransformToVisual(_memoryScroll) is { } toScroll
                ? rect.TransformToAABB(toScroll).Intersect(shown)
                : default;
            _memoryScroll.BringIntoView(spot.Height > 0 ? spot : shown);
        }, DispatcherPriority.Background);
    }

    /// <summary>
    /// Caps the memory editor's own scroll area so the whole memory card (header,
    /// legend, status and the scroll area) fits in the pane once scrolled to it.
    /// </summary>
    private void FitMemoryScroll()
    {
        if (_memoryScroll is not { IsEffectivelyVisible: true } scroll || _memoryCard is not { } card || PaneScroll.Viewport.Height <= 0) return;
        if (scroll.TranslatePoint(default, card) is not { } top) return;
        var below = card.Bounds.Height - top.Y - scroll.Bounds.Height;
        var max = Math.Max(160, Math.Floor(PaneScroll.Viewport.Height - top.Y - below));
        if (Math.Abs(scroll.MaxHeight - max) >= 1) scroll.MaxHeight = max;
    }

    // ---- The open folder's title: renamed in place ----

    // A click on the open folder's title starts renaming it (Cards and Dictionaries themselves are not renamed).
    private void OnFolderTitleTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is CardsViewModel vm && vm.FolderTree.IsInFolder) vm.FolderTree.RenameCurrentCommand.Execute(null);
    }

    private void OnFolderTitleKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not CardsViewModel vm) return;
        if (e.Key == Key.Enter) vm.FolderTree.CommitFolderRename();
        else if (e.Key == Key.Escape) vm.FolderTree.CancelFolderRename();
        else return;
        e.Handled = true;
    }

    private void OnFolderTitleLostFocus(object? sender, RoutedEventArgs e) =>
        (DataContext as CardsViewModel)?.FolderTree.CommitFolderRename();

    // Picking a swatch (or a folder) closes the flyout it lives in.
    private void OnFlyoutPickTapped(object? sender, TappedEventArgs e)
    {
        // The flyout's popup is logically parented to the button that opened it.
        if (sender is Control c && c.FindLogicalAncestorOfType<Button>() is { Flyout: { } flyout })
            flyout.Hide();
    }
}

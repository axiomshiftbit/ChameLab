using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using System.Linq;
using Avalonia.VisualTree;
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
    /// pane only needs to show the memory scroll itself; a no-op when it is visible.
    /// </summary>
    private void OnMemoryBringIntoView(object? sender, RequestBringIntoViewEventArgs e)
    {
        if (e.TargetObject == _memoryScroll) return;
        e.Handled = true;
        _memoryScroll?.BringIntoView();
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

    // ---- The open folder's title: renamed in place, deleted after a confirmation ----

    private void OnFolderTitleKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not CardsViewModel vm) return;
        if (e.Key == Key.Enter) vm.CommitFolderRename();
        else if (e.Key == Key.Escape) vm.CancelFolderRename();
        else return;
        e.Handled = true;
    }

    private void OnFolderTitleLostFocus(object? sender, RoutedEventArgs e) =>
        (DataContext as CardsViewModel)?.CommitFolderRename();

    // Confirming closes the flyout first: the folder (and its title) goes away with the delete.
    private void OnFolderDeleteClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control c && c.FindLogicalAncestorOfType<Button>() is { Flyout: { } flyout }) flyout.Hide();
        (DataContext as CardsViewModel)?.DeleteCurrentFolder();
    }

    // Picking a swatch (or a folder) closes the flyout it lives in.
    private void OnFlyoutPickTapped(object? sender, TappedEventArgs e)
    {
        // The flyout's popup is logically parented to the button that opened it.
        if (sender is Control c && c.FindLogicalAncestorOfType<Button>() is { Flyout: { } flyout })
            flyout.Hide();
    }
}

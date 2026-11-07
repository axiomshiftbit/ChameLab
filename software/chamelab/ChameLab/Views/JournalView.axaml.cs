using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ChameLab.ViewModels;
namespace ChameLab.Views;
public partial class JournalView : UserControl
{
    private JournalViewModel? _observed;
    private Visual[] _visibilityAncestors = [];
    public JournalView()
    {
        InitializeComponent();
        Viewport.SizeChanged += (_, _) => SizeEntries();
        Toolbar.SizeChanged += (_, _) => SizeEntries();
        // The inner text scroller follows the caret. Never drag the containing writer page
        // back to the journal while the user is scrolling elsewhere.
        Entries.AddHandler(RequestBringIntoViewEvent, (_, e) => e.Handled = true, RoutingStrategies.Bubble);
        Entries.PropertyChanged += (_, e) =>
        {
            if (e.Property != TextBox.TextProperty) return;
            var scroll = Entries.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
            var follow = scroll is null || scroll.Offset.Y + scroll.Viewport.Height >= scroll.Extent.Height - 2;
            Dispatcher.UIThread.Post(() =>
            {
                // Follow new entries, but let the user keep a selection for copying.
                if (follow && IsEffectivelyVisible && Entries.SelectionStart == Entries.SelectionEnd)
                    Entries.CaretIndex = Entries.Text?.Length ?? 0;
            }, DispatcherPriority.Background);
        };
    }

    protected override void OnDataContextChanged(EventArgs e)
    { base.OnDataContextChanged(e); UpdateSubscription(); }
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _visibilityAncestors = this.GetVisualAncestors().Prepend(this).ToArray();
        foreach (var visual in _visibilityAncestors) visual.PropertyChanged += VisibilityChanged;
        UpdateSubscription();
    }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        foreach (var visual in _visibilityAncestors) visual.PropertyChanged -= VisibilityChanged;
        _visibilityAncestors = [];
        base.OnDetachedFromVisualTree(e);
        _observed?.SetVisible(false);
        _observed = null;
    }

    private void VisibilityChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    { if (e.Property == IsVisibleProperty) UpdateSubscription(); }

    private void UpdateSubscription()
    {
        var model = VisualRoot is not null && _visibilityAncestors.All(v => v.IsVisible)
            ? DataContext as JournalViewModel : null;
        if (ReferenceEquals(model, _observed)) return;
        _observed?.SetVisible(false);
        _observed = model;
        _observed?.SetVisible(true);
    }

    private void SizeEntries()
    {
        // Fill a standalone page, or keep a stable viewport inside the writer. On short
        // screens the outer scroller makes both the toolbar and the log accessible.
        var height = Classes.Contains("embedded") ? 240 : Math.Max(240,
            Viewport.Bounds.Height - Toolbar.Bounds.Height - JournalLayout.Margin.Top
            - JournalLayout.Margin.Bottom - JournalLayout.RowSpacing);
        if (Math.Abs(Entries.Height - height) > 0.5) Entries.Height = height;
    }
}

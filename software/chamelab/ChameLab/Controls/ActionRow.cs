using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.VisualTree;

namespace ChameLab.Controls;

/// <summary>
/// Buttons kept on one line, left-aligned at their natural size. When they don't fit, buttons
/// get the <c>iconic</c> class one at a time (styles hide their label, leaving the icon) until
/// the row fits: lowest <see cref="PriorityProperty"/> first, and among equals the last button
/// first. Labels come back as soon as there is room again.
/// </summary>
public class ActionRow : Panel
{
    public static readonly StyledProperty<double> SpacingProperty =
        AvaloniaProperty.Register<ActionRow, double>(nameof(Spacing), 8);
    public static readonly StyledProperty<bool> WrapProperty =
        AvaloniaProperty.Register<ActionRow, bool>(nameof(Wrap));

    /// <summary>Higher keeps its label longer when the row runs out of room.</summary>
    public static readonly AttachedProperty<int> PriorityProperty =
        AvaloniaProperty.RegisterAttached<ActionRow, Control, int>("Priority");

    public const string IconicClass = "iconic";

    static ActionRow()
    {
        AffectsMeasure<ActionRow>(SpacingProperty, WrapProperty);
        PriorityProperty.Changed.AddClassHandler<Control>((c, _) => (c.GetVisualParent() as ActionRow)?.InvalidateMeasure());
        // Hidden labels may change without changing their button's current icon width.
        foreach (var property in new AvaloniaProperty[]
        {
            TextBlock.TextProperty, TextBlock.FontSizeProperty, TextBlock.FontFamilyProperty,
            TextBlock.FontWeightProperty, TextBlock.FontStyleProperty, TextBlock.FontStretchProperty,
            ContentControl.ContentProperty, ContentControl.ContentTemplateProperty
        })
            property.Changed.AddClassHandler<Control>((control, _) =>
            {
                if (control.FindAncestorOfType<ActionRow>() is not { } row) return;
                row._widths.Clear();
                row.InvalidateMeasure();
            });
    }

    public double Spacing
    {
        get => GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }
    /// <summary>Wrap only when even all icon buttons cannot fit on one line.</summary>
    public bool Wrap { get => GetValue(WrapProperty); set => SetValue(WrapProperty, value); }

    public static int GetPriority(Control control) => control.GetValue(PriorityProperty);
    public static void SetPriority(Control control, int value) => control.SetValue(PriorityProperty, value);

    /// <summary>The row's width with every label hidden, as of the last measure.</summary>
    public double IconicWidth { get; private set; }

    private readonly List<Visual> _ancestors = new();
    private readonly Dictionary<Control, (double Full, double Icon)> _widths = new();

    public ActionRow() => Children.CollectionChanged += (_, _) => _widths.Clear();

    private List<Control> Visible() => Children.Where(c => c.IsVisible).ToList();

    /// <remarks>
    /// Avalonia skips measuring controls under a hidden ancestor, and when that ancestor shows
    /// again this row and its buttons keep the sizes cached before: a button that changed while
    /// hidden (its label, a part shown) is laid out at its old width. So the row's subtree is
    /// remeasured whenever an ancestor becomes visible.
    /// </remarks>
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _ancestors.AddRange(this.GetVisualAncestors());
        foreach (var ancestor in _ancestors) ancestor.PropertyChanged += OnAncestorPropertyChanged;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        foreach (var ancestor in _ancestors) ancestor.PropertyChanged -= OnAncestorPropertyChanged;
        _ancestors.Clear();
        _widths.Clear();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        OnAncestorPropertyChanged(this, change);
    }

    private void OnAncestorPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != IsVisibleProperty || e.NewValue is not true) return;
        foreach (var layoutable in this.GetSelfAndVisualDescendants().OfType<Layoutable>())
            layoutable.InvalidateMeasure();
    }

    /// <summary>Visible buttons in the order they lose their label.</summary>
    private static List<Control> CollapseOrder(List<Control> visible) => visible
        .Select((c, i) => (Control: c, Index: i))
        .OrderBy(x => GetPriority(x.Control))
        .ThenByDescending(x => x.Index)
        .Select(x => x.Control)
        .ToList();

    /// <remarks>
    /// Measure both appearances when the button's content changes, then reuse those widths.
    /// Toggling styles during every measure invalidates layout again and can starve idle work.
    /// </remarks>
    protected override Size MeasureOverride(Size availableSize)
    {
        // Hidden buttons keep their label, so they come back whole.
        foreach (var child in Children.Where(c => !c.IsVisible)) SetIconic(child, false);

        var order = CollapseOrder(Visible());
        var sizes = order.Select(WidthsOf).ToArray();

        var limit = availableSize.Width;
        var gaps = Math.Max(0, order.Count - 1) * Spacing;
        IconicWidth = sizes.Sum(s => s.Icon) + gaps;
        var width = sizes.Sum(s => s.Full) + gaps;
        var collapsed = 0;
        while (width > limit && collapsed < order.Count)
        {
            width += sizes[collapsed].Icon - sizes[collapsed].Full;
            collapsed++;
        }

        var height = 0.0;
        for (var i = 0; i < order.Count; i++)
        {
            SetIconic(order[i], i < collapsed);
            order[i].Measure(Size.Infinity);
            height = Math.Max(height, order[i].DesiredSize.Height);
        }
        return new Size(Math.Min(width, limit), Wrap ? LayoutRows(limit, arrange: false) : height);
    }

    private (double Full, double Icon) WidthsOf(Control child)
    {
        if (_widths.TryGetValue(child, out var size) && child.IsMeasureValid
            && Math.Abs(child.DesiredSize.Width - (child.Classes.Contains(IconicClass) ? size.Icon : size.Full)) < 0.5)
            return size;
        foreach (var layoutable in child.GetSelfAndVisualDescendants().OfType<Layoutable>())
            layoutable.InvalidateMeasure();
        size = (WidthOf(child, iconic: false), WidthOf(child, iconic: true));
        _widths[child] = size;
        return size;
    }

    private static double WidthOf(Control child, bool iconic)
    {
        SetIconic(child, iconic);
        child.Measure(Size.Infinity);
        return child.DesiredSize.Width;
    }

    /// <summary>
    /// Switches the label off or on. The style change hides a nested TextBlock, which does not
    /// invalidate the button's cached size by the time it is measured again, so the whole
    /// subtree is invalidated here.
    /// </summary>
    private static void SetIconic(Control child, bool iconic)
    {
        if (child.Classes.Contains(IconicClass) == iconic) return;
        child.Classes.Set(IconicClass, iconic);
        foreach (var layoutable in child.GetSelfAndVisualDescendants().OfType<Layoutable>())
            layoutable.InvalidateMeasure();
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        if (Wrap)
        {
            LayoutRows(finalSize.Width, arrange: true);
            return finalSize;
        }
        // Stretched buttons take the row's height, so icon-only ones match their labeled neighbors.
        var x = 0.0;
        foreach (var child in Visible())
        {
            var size = child.DesiredSize;
            var height = child.VerticalAlignment == VerticalAlignment.Stretch ? finalSize.Height : size.Height;
            child.Arrange(new Rect(x, (finalSize.Height - height) / 2, size.Width, height));
            x += size.Width + Spacing;
        }
        return finalSize;
    }

    private double LayoutRows(double limit, bool arrange)
    {
        var children = Visible();
        double y = 0;
        for (var start = 0; start < children.Count;)
        {
            var end = start + 1;
            var width = children[start].DesiredSize.Width;
            var height = children[start].DesiredSize.Height;
            while (end < children.Count && width + Spacing + children[end].DesiredSize.Width <= limit)
            {
                width += Spacing + children[end].DesiredSize.Width;
                height = Math.Max(height, children[end].DesiredSize.Height);
                end++;
            }
            double x = 0;
            for (var i = start; i < end; i++)
            {
                var size = children[i].DesiredSize;
                if (arrange) children[i].Arrange(new Rect(x, y + (height - size.Height) / 2, size.Width, size.Height));
                x += size.Width + Spacing;
            }
            y += height + Spacing;
            start = end;
        }
        return children.Count == 0 ? 0 : y - Spacing;
    }
}

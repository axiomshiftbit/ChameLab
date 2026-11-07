using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.VisualTree;

namespace ChameLab.Controls;

/// <summary>Keeps the app bar whole until its contents need room, then hides details in order.</summary>
public class AppBarGrid : Grid
{
    private static readonly string[] Steps =
        ["hide-summary", "hide-disconnect-label", "hide-logo", "hide-connection-label"];
    private double _lastWidth = double.NaN;
    private Size _lastContentSize;

    protected override Size MeasureOverride(Size availableSize)
    {
        // Re-probing every collapsed detail on every layout pass changes visibility twice,
        // invalidating the header and the whole page again even when nothing has changed.
        var current = base.MeasureOverride(Size.Infinity);
        if (_lastWidth == availableSize.Width && current == _lastContentSize)
            return base.MeasureOverride(availableSize);
        foreach (var step in Steps) SetStep(step, false);
        var desired = base.MeasureOverride(Size.Infinity);
        foreach (var step in Steps)
        {
            if (desired.Width <= availableSize.Width) break;
            SetStep(step, true);
            desired = base.MeasureOverride(Size.Infinity);
        }
        _lastWidth = availableSize.Width;
        _lastContentSize = desired;
        return base.MeasureOverride(availableSize);
    }

    private void SetStep(string step, bool enabled)
    {
        if (Classes.Contains(step) == enabled) return;
        Classes.Set(step, enabled);
        // Nested visibility changes must reach the Grid's cached child measurements immediately.
        foreach (var child in this.GetVisualDescendants().OfType<Layoutable>())
            child.InvalidateMeasure();
    }
}

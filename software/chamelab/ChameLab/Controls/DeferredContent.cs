using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;

namespace ChameLab.Controls;

/// <summary>Creates a pane only on its first use, then keeps its controls and editing state.</summary>
public sealed class DeferredContent : Decorator
{
    public static readonly StyledProperty<bool> IsActiveProperty =
        AvaloniaProperty.Register<DeferredContent, bool>(nameof(IsActive));
    public static readonly StyledProperty<IDataTemplate?> ContentTemplateProperty =
        AvaloniaProperty.Register<DeferredContent, IDataTemplate?>(nameof(ContentTemplate));

    public DeferredContent() => IsVisible = false;
    public bool IsActive { get => GetValue(IsActiveProperty); set => SetValue(IsActiveProperty, value); }
    public IDataTemplate? ContentTemplate { get => GetValue(ContentTemplateProperty); set => SetValue(ContentTemplateProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsActiveProperty) IsVisible = IsActive;
        if (change.Property == IsActiveProperty || change.Property == ContentTemplateProperty || change.Property == DataContextProperty)
            if (IsActive && Child is null && ContentTemplate is { } template && DataContext is not null)
                Child = template.Build(DataContext);
    }
}

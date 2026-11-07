using System;
using ActiproSoftware.UI.Avalonia.Themes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Xunit;

namespace ChameLab.UiTests;

public class ThemeTests
{
    [AvaloniaTheory]
    [InlineData("caption", ThemeResourceKind.DefaultForegroundBrushSecondary)]
    [InlineData("overline", ThemeResourceKind.DefaultForegroundBrushTertiary)]
    [InlineData("warning", ThemeResourceKind.ControlForegroundBrushOutlineWarning)]
    [InlineData("danger", ThemeResourceKind.ControlForegroundBrushOutlineDanger)]
    [InlineData("success", ThemeResourceKind.ControlForegroundBrushOutlineSuccess)]
    public void TextUsesReadableThemeColorsAfterSwitching(string style, ThemeResourceKind resource)
    {
        var text = new TextBlock { Text = "Проверка читаемости текста", Classes = { style } };
        var window = new Window { Width = 480, Height = 320, Content = text };
        window.Show();
        try
        {
            foreach (var variant in new[] { ThemeVariant.Light, ThemeVariant.Dark, ThemeVariant.Light })
            {
                Application.Current!.RequestedThemeVariant = variant;
                Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
                var actual = Assert.IsAssignableFrom<ISolidColorBrush>(text.Foreground).Color;
                var expected = Assert.IsAssignableFrom<ISolidColorBrush>(text.FindResource(variant, resource.ToResourceKey())).Color;
                Assert.Equal(expected, actual);
                var background = Assert.IsAssignableFrom<ISolidColorBrush>(window.Background).Color;
                var minimum = style is "caption" or "overline" ? 4.5 : 3;
                Assert.True(Contrast(actual, background) >= minimum,
                    $"{style}/{variant}: text contrast {Contrast(actual, background):F2} is below {minimum}:1.");
            }
        }
        finally { window.Close(); }
    }

    private static double Contrast(Color foreground, Color background)
    {
        static double Channel(byte value)
        {
            var s = value / 255.0;
            return s <= 0.04045 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }
        static double Luminance(Color c) => 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
        var a = Luminance(foreground); var b = Luminance(background);
        return (Math.Max(a, b) + 0.05) / (Math.Min(a, b) + 0.05);
    }
}

using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ChameLab.UiTesting;
using ChameLab.Views;
using Xunit;

namespace ChameLab.UiTests;

public class LibraryHeaderTests
{
    public static IEnumerable<object[]> Sizes()
    {
        foreach (var width in new[] { 320, 360, 800, 1280 })
        foreach (var scale in new[] { 1.0, 1.5 })
        foreach (var dark in new[] { false, true })
            yield return [width, scale, dark];
    }

    [AvaloniaTheory]
    [MemberData(nameof(Sizes))]
    public void FolderButtonDoesNotCoverCardCount(int width, double scale, bool dark)
    {
        Application.Current!.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
        var scene = LayoutScenarios.Create("cards-library", "ru");
        var view = (CardsView)scene.Child!;
        var window = new Window { Width = width, Height = 800, Content = scene };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            LayoutScenarios.ScaleText(scene, scale); window.UpdateLayout();
            var header = view.FindControl<Panel>("LibraryHeader")!;
            var title = header.Children[0];
            var actions = header.Children[1];
            var titleRect = new Rect(title.TranslatePoint(default, header)!.Value, title.Bounds.Size);
            var actionsRect = new Rect(actions.TranslatePoint(default, header)!.Value, actions.Bounds.Size);
            Assert.False(titleRect.Intersects(actionsRect), $"Title {titleRect} overlaps buttons {actionsRect}.");
            Assert.Empty(LayoutScenarios.FindOverflow(scene));
        }
        finally { window.Close(); }
    }
}

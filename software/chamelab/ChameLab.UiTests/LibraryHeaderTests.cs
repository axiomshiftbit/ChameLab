using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ChameLab.UiTesting;
using ChameLab.ViewModels;
using ChameLab.Views;
using Xunit;

namespace ChameLab.UiTests;

public class LibraryHeaderTests
{
    [AvaloniaTheory]
    [InlineData(320, 480, 1.0)]
    [InlineData(320, 480, 1.5)]
    [InlineData(360, 560, 1.5)]
    [InlineData(1280, 800, 1.0)]
    public void EditedSavedCardKeepsReadableDetailsAndReachableActions(int width, int height, double scale)
    {
        var scene = LayoutScenarios.Create("card-classic", "ru");
        var view = (CardsView)scene.Child!;
        var model = (CardsViewModel)view.DataContext!;
        model.Form!.Name = "Unsaved synthetic edit";
        var window = new Window { Width = width, Height = height, Content = scene };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            LayoutScenarios.ScaleText(scene, scale); window.UpdateLayout();
            Assert.True(model.IsCardDirty);
            Assert.Empty(LayoutScenarios.FindOverflow(scene));
            var scroll = view.FindControl<ScrollViewer>("PaneScroll")!;
            var buttons = view.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible &&
                (b.Command == model.SaveCommand || b.Command == model.DiscardCommand)).ToArray();
            Assert.Equal(2, buttons.Length);
            var rects = buttons.Select(b => new Rect(b.TranslatePoint(default, scene)!.Value, b.Bounds.Size)).ToArray();
            Assert.All(rects, r => Assert.True(new Rect(scene.Bounds.Size).Contains(r)));
            Assert.False(rects[0].Intersects(rects[1]));
            scroll.ScrollToEnd(); window.UpdateLayout();
            Assert.True(scroll.Offset.Y > 0);
            for (var i = 0; i < buttons.Length; i++)
                Assert.Equal(rects[i], new Rect(buttons[i].TranslatePoint(default, scene)!.Value, buttons[i].Bounds.Size));
            Assert.Equal("Unsaved synthetic edit", model.Form.Name);
        }
        finally { window.Close(); }
    }

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

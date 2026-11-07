using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ChameLab.Localization;
using ChameLab.UiTesting;
using ChameLab.ViewModels;
using Xunit;

namespace ChameLab.UiTests;

public class ToolsNavigationTests
{
    [AvaloniaTheory]
    [InlineData(360, false)]
    [InlineData(360, true)]
    [InlineData(1000, false)]
    [InlineData(1000, true)]
    public void EveryToolOpensAndReturnsWithTheCorrectHint(int width, bool dark)
    {
        Application.Current!.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
        var scene = LayoutScenarios.Create("tools", "ru");
        var model = (ToolsViewModel)scene.Child!.DataContext!;
        var window = new Window { Width = width, Height = 850, Content = scene };
        window.Show();
        void Settle() { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); }
        void Click(Button button)
        {
            button.BringIntoView(); Settle();
            var point = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
            window.MouseMove(point); Settle();
            window.MouseDown(point, MouseButton.Left); window.MouseUp(point, MouseButton.Left); Settle();
        }
        try
        {
            Settle();
            var buttons = scene.GetVisualDescendants().OfType<Button>().Where(b => b.Command == model.OpenToolCommand).ToArray();
            string[] ids = ["key-search", "plugins", "console", "ValueBlocks", "dictionaries", "T55Password", "lf", "hf", "auth", "Gen4", "T55Block", "RawHf", "RawDevice"];
            Assert.Equal(ids.Order(), buttons.Select(b => (string)b.CommandParameter!).Order());
            foreach (var id in ids)
            {
                Click(buttons.Single(b => (string)b.CommandParameter! == id));
                Assert.False(model.IsHome);
                Assert.False(string.IsNullOrWhiteSpace(model.CurrentToolTitle));
                var texts = scene.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToArray();
                Assert.DoesNotContain(texts, t => t?.StartsWith("Not Found:") == true);
                if (id == "auth")
                {
                    Assert.Contains(Strings.Tool_hf_auth_description, texts);
                    Assert.DoesNotContain(Strings.Tools_TheChameleonAnswersAReaderAs, texts);
                    Assert.Equal(Strings.Tool_unsupported_firmware, model.HfUnsupportedHint);
                }
                if (id == "hf") Assert.Contains(Strings.Tools_TheChameleonAnswersAReaderAs, texts);
                Click(scene.GetVisualDescendants().OfType<Button>().Single(b => b.Command == model.BackCommand));
                Assert.True(model.IsHome);
            }
        }
        finally { window.Close(); }
    }
}

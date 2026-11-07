using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.Styling;
using Avalonia.VisualTree;
using System.Linq;
using ChameLab.Services;
using ChameLab.UiTesting;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(ChameLab.UiTests.TestAppBuilder))]

namespace ChameLab.UiTests;

public sealed class LayoutApp : App
{
    public override void OnFrameworkInitializationCompleted() { }
}

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp()
    {
        AppServices.Register([], store: new MemoryKeyValueStore());
        return AppBuilder.Configure<LayoutApp>().UseSkia().WithInterFont()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
    }
}

public class LayoutTests
{
    public static IEnumerable<object[]> Cases()
    {
        foreach (var language in new[] { "en", "ru", "de", "fr", "es", "it", "pt", "ja", "ko", "zh-Hans" })
        foreach (var width in new[] { 320, 360, 600, 1280 })
        foreach (var page in LayoutScenarios.Pages)
            yield return [language, width, 800, page, 1.0, "Dark"];
        foreach (var language in new[] { "en", "ru", "de", "fr", "es", "it", "pt", "ja", "ko", "zh-Hans" })
        foreach (var theme in new[] { "Light", "Dark" })
        foreach (var size in new[] { (320, 640), (800, 360) })
        foreach (var page in LayoutScenarios.Pages)
            yield return [language, size.Item1, size.Item2, page, 1.5, theme];
    }

    [AvaloniaTheory]
    [MemberData(nameof(Cases))]
    public void PageLabelsFit(string language, int width, int height, string page, double scale, string theme)
    {
        Application.Current!.RequestedThemeVariant = theme == "Light" ? ThemeVariant.Light : ThemeVariant.Dark;
        var content = LayoutScenarios.Create(page, language);
        var window = new Window { Width = width, Height = height, Content = content };
        window.Show();
        try
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            foreach (var expander in content.GetVisualDescendants().OfType<Expander>().ToArray()) expander.IsExpanded = true;
            window.UpdateLayout();
            LayoutScenarios.ScaleText(content, scale);
            window.UpdateLayout();
            var errors = LayoutScenarios.FindOverflow(content);
            Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
        }
        finally { window.Close(); }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace ChameLab.UiTesting;

// This runner is absent from production builds. It never connects to a device or loads user data.
public sealed class UiTestHost : ContentControl
{
    public static string OutputDirectory { get; set; } = Environment.GetEnvironmentVariable("CHAMELAB_UI_OUTPUT")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ui-results");
    private bool _started;
    public UiTestHost() => Loaded += async (_, _) =>
    {
        if (_started) return;
        _started = true;
        await RunAsync();
    };

    public sealed class Request
    {
        public string[] Languages { get; set; } = ["en", "ru", "de", "fr", "es", "it", "pt", "ja", "ko", "zh-Hans"];
        public double FontScale { get; set; } = 1;
        public int[][] Sizes { get; set; } = [];
    }
    public sealed record Case(string Page, string Language, string Theme, double Width, double Height, double FontScale, string[] Errors);

    private async Task RunAsync()
    {
        Directory.CreateDirectory(OutputDirectory);
        var cases = new List<Case>();
        try
        {
            var path = Path.Combine(OutputDirectory, "request.json");
            var request = File.Exists(path) ? JsonSerializer.Deserialize<Request>(File.ReadAllText(path))! : new Request();

            await Task.Delay(500);
            var sizes = request.Sizes.Length == 0 ? new[] { new[] { 0, 0 } } : request.Sizes;
            foreach (var size in sizes)
            {
                if (size[0] > 0 && TopLevel.GetTopLevel(this) is Window window)
                {
                    window.Width = size[0]; window.Height = size[1];
                    await Task.Delay(200);
                }
                foreach (var theme in new[] { ThemeVariant.Light, ThemeVariant.Dark })
                foreach (var language in request.Languages)
                foreach (var page in LayoutScenarios.Pages)
                {
                    Application.Current!.RequestedThemeVariant = theme;
                    var scene = LayoutScenarios.Create(page, language);
                    scene.Background = TopLevel.GetTopLevel(this)?.Background
                        ?? (theme == ThemeVariant.Light ? Brushes.White : new SolidColorBrush(Color.FromRgb(32, 32, 32)));
                    Content = scene;
                    await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
                    await Task.Delay(40);
                    UpdateLayout();
                    // Expand advanced settings as well as their default collapsed state.
                    foreach (var expander in scene.GetVisualDescendants().OfType<Expander>().ToArray()) expander.IsExpanded = true;
                    UpdateLayout();
                    LayoutScenarios.ScaleText(scene, request.FontScale);
                    UpdateLayout();
                    var errors = LayoutScenarios.FindOverflow(scene);
                    if (scene.Bounds.Width < 200 || scene.Bounds.Height < 100) errors.Add("Scene was not rendered at a usable size.");
                    cases.Add(new(page, language, theme.ToString(), scene.Bounds.Width, scene.Bounds.Height, request.FontScale, errors.ToArray()));
                    if (language == "ru" || errors.Count > 0)
                    {
                        var stem = $"{page}-{language}-{theme}-{scene.Bounds.Width:0}x{scene.Bounds.Height:0}";
                        SaveImage(scene, Path.Combine(OutputDirectory, stem + ".png"));
                        var scroll = scene.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault(s => s.IsEffectivelyVisible && s.Extent.Height > s.Viewport.Height);
                        if (scroll is not null)
                        {
                            scroll.Offset = new Vector(0, scroll.Extent.Height);
                            await Task.Delay(30);
                            UpdateLayout();
                            SaveImage(scene, Path.Combine(OutputDirectory, stem + "-bottom.png"));
                        }
                    }
                    if (scene.Child?.DataContext is ViewModels.PageViewModelBase vm) await vm.OnDeactivatedAsync();
                }
            }
            File.WriteAllText(Path.Combine(OutputDirectory, "result.json"), JsonSerializer.Serialize(new
            {
                Complete = true, Expected = sizes.Length * 2 * request.Languages.Length * LayoutScenarios.Pages.Length,
                Passed = cases.Count(c => c.Errors.Length == 0), Failed = cases.Count(c => c.Errors.Length != 0), Cases = cases
            }));
        }
        catch (Exception ex)
        {
            File.WriteAllText(Path.Combine(OutputDirectory, "result.json"), JsonSerializer.Serialize(new { Complete = false, Error = ex.ToString(), Cases = cases }));
        }
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) desktop.Shutdown();
    }

    private static void SaveImage(Control scene, string path)
    {
        using var bitmap = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(scene.Bounds.Width), (int)Math.Ceiling(scene.Bounds.Height)), new Vector(96, 96));
        bitmap.Render(scene);
        bitmap.Save(path, PngBitmapEncoderOptions.Default);
    }
}

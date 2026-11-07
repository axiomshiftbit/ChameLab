using System;
using System.Linq;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Styling;

namespace ChameLab.Services;

internal static class PluginSettings
{
    private static readonly string[] Colors = ["Orange", "Purple", "Blue", "Green", "Indigo", "Lime", "Red", "Yellow"];
    public static JsonNode Invoke(string method, JsonArray args)
    {
        if (method == "settings.options") return new JsonObject
        {
            ["language"] = new JsonArray(LocalizationService.Languages.Select(l => (JsonNode)l.Code).ToArray()),
            ["theme"] = new JsonArray("system", "light", "dark"),
            ["themeColor"] = new JsonArray(Colors.Select(c => (JsonNode)c.ToLowerInvariant()).ToArray()),
        };
        if (method == "settings.update")
        {
            var patch = args[0] as JsonObject ?? throw new FormatException("Expected a settings object.");
            // Validate the entire patch before applying any changes.
            foreach (var (key, value) in patch)
            {
                var valid = key switch
                {
                    "language" => LocalizationService.Languages.Any(l => l.Code == (string?)value),
                    "theme" => (string?)value is "system" or "light" or "dark",
                    "themeColor" => (int?)value is >= 0 and < 8,
                    _ => false,
                };
                if (!valid) throw new ArgumentException("Unsupported setting or value: " + key);
            }
            foreach (var (key, value) in patch)
                switch (key)
                {
                    case "language": LocalizationService.SaveLanguage((string)value!); break;
                    case "theme":
                        var theme = (string?)value switch { "light" => ThemeVariant.Light, "dark" => ThemeVariant.Dark, _ => ThemeVariant.Default };
                        if (Application.Current is { } app) app.RequestedThemeVariant = theme;
                        AppSettings.Set("theme", theme.Key.ToString()); break;
                    case "themeColor": AppearanceService.Apply(Colors[(int)value!]); break;
                }
        }
        else if (method != "settings.get") throw new NotSupportedException(method);
        return new JsonObject
        {
            ["language"] = LocalizationService.LoadLanguage(),
            ["theme"] = AppSettings.GetString("theme") switch { "Light" => "light", "Dark" => "dark", _ => "system" },
            ["themeColor"] = Array.IndexOf(Colors, AppearanceService.LoadAccent()) is >= 0 and var index ? index : (int?)null,
            ["accent"] = AppearanceService.LoadAccent(),
        };
    }
}

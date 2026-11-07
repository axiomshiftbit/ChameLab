using System.Text.Json;
using System.Text;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace ChameLab.Tools;

internal static class DependencyNotices
{
    public static async Task GenerateAsync()
    {
        var root = Path.Combine(Program.Root(), "software/chamelab");
        var packages = new SortedDictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var texts = new Dictionary<string, List<(string Path, string Text)>>();
        foreach (var project in new[] {"ChameLab.Desktop", "ChameLab.Android", "ChameLab.Browser", "ChameLab.Cli", "ChameLab.Commands"})
        {
            var assets = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(root, project, "obj/project.assets.json")))!;
            foreach (var (name, library) in assets["libraries"]!.AsObject())
            {
                if ((string?)library!["type"] != "package") continue;
                var package = assets["packageFolders"]!.AsObject().Select(folder => Path.Combine(folder.Key, (string)library["path"]!))
                    .FirstOrDefault(Directory.Exists) ?? throw new FileNotFoundException("Restore package " + name);
                var spec = Directory.EnumerateFiles(package, "*.nuspec").First();
                var meta = XDocument.Load(spec).Root!.Elements().Single(e => e.Name.LocalName == "metadata");
                string Value(string key) => meta.Elements().FirstOrDefault(e => e.Name.LocalName == key)?.Value ?? "";
                var license = meta.Elements().FirstOrDefault(e => e.Name.LocalName == "license");
                var licenseText = license?.Value ?? Value("licenseUrl");
                var notices = new List<(string Path, string Text)>();
                foreach (var file in Directory.EnumerateFiles(package, "*", SearchOption.AllDirectories).Order())
                    if (Regex.IsMatch(Path.GetFileName(file), @"^(licen[cs]e|copying|notice|third[-_. ]party[-_. ]notices)([-_.][A-Za-z0-9-]+)?\.(txt|md)$", RegexOptions.IgnoreCase))
                        notices.Add((Path.GetRelativePath(package, file), await File.ReadAllTextAsync(file)));
                if ((string?)license?.Attribute("type") == "file")
                {
                    var file = Path.GetFullPath(Path.Combine(package, licenseText));
                    if (!file.StartsWith(Path.GetFullPath(package) + Path.DirectorySeparatorChar, StringComparison.Ordinal)) throw new InvalidDataException("License path outside package.");
                    if (File.Exists(file) && !notices.Any(n => n.Path == licenseText)) notices.Add((licenseText, await File.ReadAllTextAsync(file)));
                }
                packages[name] = new() { ["name"] = name, ["authors"] = Value("authors"), ["copyright"] = Value("copyright"),
                    ["license"] = licenseText, ["licenseType"] = (string?)license?.Attribute("type") ?? "", ["url"] = Value("projectUrl") };
                texts[name] = notices;
            }
        }
        var output = Path.Combine(root, "ChameLab/Assets/Licenses"); Directory.CreateDirectory(output);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        foreach (var expression in packages.Values.Where(p => p["licenseType"] == "expression").Select(p => p["license"]).Distinct())
        {
            if (!Regex.IsMatch(expression, "^[A-Za-z0-9.-]+$")) continue;
            var file = Path.Combine(output, "SPDX-" + expression + ".txt");
            if (!File.Exists(file)) await File.WriteAllBytesAsync(file, await http.GetByteArrayAsync("https://raw.githubusercontent.com/spdx/license-list-data/main/text/" + expression + ".txt"));
        }
        var sections = new List<string> { "Сторонние библиотеки / Third-party components\n\nСформировано по восстановленным зависимостям выбранных проектов. Уведомления поставщиков сохранены без изменения." };
        var unique = new Dictionary<string, string>();
        foreach (var (name, p) in packages)
        {
            var references = new List<string>();
            foreach (var notice in texts[name])
            {
                var normalized = notice.Text.Replace("\r\n", "\n");
                var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
                unique.TryAdd(id, normalized);
                references.Add($"Notice: {notice.Path} → {id}");
            }
            sections.Add(string.Join("\n", new[] {name, "Authors: " + p["authors"], "Copyright: " + p["copyright"], "License: " + p["license"], "Source: " + p["url"]}.Concat(references)));
        }
        foreach (var (id, text) in unique) sections.Add($"NOTICE {id}\n\n{text}");
        await File.WriteAllTextAsync(Path.Combine(output, "DEPENDENCIES.txt"), "\n\n" + string.Join("\n\n" + new string('=',72) + "\n\n", sections));
        await File.WriteAllTextAsync(Path.Combine(output, "dependencies.json"), JsonSerializer.Serialize(packages.Values, new JsonSerializerOptions { WriteIndented = true }) + "\n");
        Console.WriteLine($"Generated notices for {packages.Count} package versions");
    }
}

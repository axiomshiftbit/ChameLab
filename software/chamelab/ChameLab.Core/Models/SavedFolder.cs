using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace ChameLab.Models;

public sealed record SavedFolder(string Id, string Name, string Color, string? ParentId)
{
    public JsonObject ToJson() => new() { ["id"] = Id, ["name"] = Name, ["color"] = Color, ["parentId"] = ParentId };
    public static SavedFolder FromJson(JsonNode node)
    {
        var o = node as JsonObject ?? throw new FormatException("Expected a folder object.");
        var name = ((string?)o["name"] ?? "").Trim();
        if (name.Length == 0) throw new FormatException("Folder name is required.");
        return new((string?)o["id"] ?? Guid.NewGuid().ToString(), name, CardSave.NormalizeColor((string?)o["color"]), (string?)o["parentId"]);
    }
    public static IReadOnlyDictionary<string, string> Paths(IReadOnlyList<SavedFolder> folders)
    {
        if (folders.Any(f => string.IsNullOrWhiteSpace(f.Id) || string.IsNullOrWhiteSpace(f.Name))) throw new FormatException("Folder ID and name are required.");
        if (folders.Select(f => f.Id).Distinct().Count() != folders.Count) throw new FormatException("Duplicate folder ID.");
        var byId = folders.ToDictionary(f => f.Id);
        var paths = new Dictionary<string, string>();
        foreach (var folder in folders)
        {
            var names = new List<string>(); var seen = new HashSet<string>();
            for (var at = folder; ; at = byId[at.ParentId!])
            {
                if (!seen.Add(at.Id)) throw new FormatException("Folder hierarchy contains a cycle.");
                names.Add(at.Name);
                if (at.ParentId is null) break;
                if (!byId.ContainsKey(at.ParentId)) throw new FormatException("Unknown parent folder.");
            }
            names.Reverse(); var path = string.Join(ChameleonGuiImport.PathSeparator, names);
            if (path.Length > CardSave.MaxFolderLength) throw new FormatException("Folder path exceeds 64 characters.");
            paths[folder.Id] = path;
        }
        if (paths.Values.Distinct(StringComparer.OrdinalIgnoreCase).Count() != paths.Count) throw new FormatException("Folder paths must be unique.");
        return paths;
    }
}

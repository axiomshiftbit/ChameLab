using System.Text.Json.Nodes;
using ChameLab.Models;
using ChameLab.Services;

namespace ChameLab.Automation;

public sealed partial class PluginHost
{
    private FolderLibrary Tree(bool card) => card ? cards.FolderTree : dictionaries.FolderTree;
    private void ReplaceTree(bool card, IEnumerable<SavedFolder> folders)
    { if (card) cards.ReplaceFolders(folders); else dictionaries.ReplaceFolders(folders); }
    private string FolderPath(bool card, string? id) => id is null ? "" :
        Tree(card).Paths.TryGetValue(id, out var path) ? path : throw new FormatException("Unknown folder.");

    // Assign stable IDs to the existing display-path library without changing its layout.
    private void ReconcileFolders(bool card)
    {
        var tree = Tree(card);
        if (tree.LoadError is { } error) throw new InvalidOperationException(error);
        var paths = tree.Paths;
        var legacy = card ? cards.Folders : dictionaries.Dictionaries.Select(d => d.Folder).Where(p => p.Length > 0).Distinct();
        var missing = legacy.Where(p => !paths.Values.Contains(p, StringComparer.OrdinalIgnoreCase))
            .Select(p => new SavedFolder(Guid.NewGuid().ToString(), p, CardSave.DefaultColor, null)).ToArray();
        if (missing.Length > 0) tree.Replace(tree.Items.Concat(missing));
        paths = tree.Paths;
        string? Id(string path) => paths.FirstOrDefault(p => p.Value.Equals(path, StringComparison.OrdinalIgnoreCase)).Key;
        if (card)
            foreach (var item in cards.Cards) item.FolderId = Id(item.Folder);
        else
            foreach (var item in dictionaries.Dictionaries) item.FolderId = Id(item.Folder);
    }
    private static HashSet<string> Descendants(string id, IReadOnlyList<SavedFolder> folders)
    {
        if (!folders.Any(f => f.Id == id)) throw new FormatException("Unknown folder.");
        var result = new HashSet<string> { id };
        bool changed;
        do
        {
            changed = false;
            foreach (var f in folders)
                if (f.ParentId is not null && result.Contains(f.ParentId)) changed |= result.Add(f.Id);
        } while (changed);
        return result;
    }
    private JsonNode? FolderData(string method, JsonArray a)
    {
        var card = method.StartsWith("cardFolders.", StringComparison.Ordinal);
        ReconcileFolders(card);
        var tree = Tree(card);
        var format = card ? "chameleon-ultra-gui-folder" : "chameleon-ultra-gui-dictionary-folder";
        var recordsKey = card ? "cards" : "dictionaries";
        switch (method[(method.IndexOf('.') + 1)..])
        {
            case "list": return tree.ToJson();
            case "save":
                var saved = SavedFolder.FromJson(a[0]!);
                ReplaceTree(card, tree.Items.Where(f => f.Id != saved.Id).Append(saved));
                return saved.Id;
            case "delete":
                var ids = Descendants(Text(a[0]), tree.Items);
                // Snapshot records before replacing the tree clears their folder identities.
                var removedCards = cards.Cards.Where(c => c.FolderId is not null && ids.Contains(c.FolderId)).ToArray();
                var removedDicts = dictionaries.Dictionaries.Where(d => d.FolderId is not null && ids.Contains(d.FolderId)).ToArray();
                ReplaceTree(card, tree.Items.Where(f => !ids.Contains(f.Id)));
                if (card) foreach (var c in removedCards) cards.Remove(c);
                else foreach (var d in removedDicts) dictionaries.Remove(d);
                return true;
            case "export":
                var root = Text(a[0]); var subtree = Descendants(root, tree.Items);
                return new JsonObject
                {
                    ["format"] = format, ["version"] = 1, ["rootFolderId"] = root,
                    ["folders"] = new JsonArray(tree.Items.Where(f => subtree.Contains(f.Id)).Select(f => (JsonNode)f.ToJson()).ToArray()),
                    [recordsKey] = card
                        ? new JsonArray(cards.Cards.Where(c => c.FolderId is not null && subtree.Contains(c.FolderId)).Select(c => (JsonNode)c.ToJson()).ToArray())
                        : new JsonArray(dictionaries.Dictionaries.Where(d => d.FolderId is not null && subtree.Contains(d.FolderId)).Select(d => (JsonNode)d.ToJson()).ToArray()),
                }.ToJsonString();
            case "import":
                var bundle = JsonNode.Parse(Text(a[0])) as JsonObject ?? throw new FormatException("Expected a folder bundle.");
                if ((string?)bundle["format"] != format || (int?)bundle["version"] != 1) throw new FormatException("Unsupported folder bundle.");
                var oldRoot = Text(bundle["rootFolderId"]);
                var parent = a.Count > 1 ? (string?)a[1] : null; _ = FolderPath(card, parent);
                var imported = (bundle["folders"] as JsonArray ?? throw new FormatException("Missing folders."))
                    .Select(n => SavedFolder.FromJson(n!)).Select(f => f.Id == oldRoot ? f with { ParentId = null } : f).ToList();
                _ = SavedFolder.Paths(imported);
                if (Descendants(oldRoot, imported).Count != imported.Count) throw new FormatException("Bundle contains unrelated folders.");
                var remap = imported.ToDictionary(f => f.Id, _ => Guid.NewGuid().ToString());
                var replacement = tree.Items.Concat(imported.Select(f => f with { Id = remap[f.Id], ParentId = f.Id == oldRoot ? parent : remap[f.ParentId!] })).ToList();
                var newPaths = SavedFolder.Paths(replacement);
                var records = bundle[recordsKey] as JsonArray ?? throw new FormatException("Missing records.");
                var cardCopies = new List<CardSave>(); var dictionaryCopies = new List<KeyDictionary>();
                foreach (var node in records)
                {
                    var copy = Object(node).DeepClone().AsObject();
                    var oldFolder = (string?)copy["folderId"];
                    if (oldFolder is null || !remap.TryGetValue(oldFolder, out var newFolder)) throw new FormatException("Record is outside the folder bundle.");
                    copy["id"] = Guid.NewGuid().ToString(); copy["folderId"] = newFolder; copy["folder"] = newPaths[newFolder];
                    if (card) cardCopies.Add(CardSave.FromJson(copy)); else dictionaryCopies.Add(KeyDictionary.FromJson(copy));
                }
                // Parsing, hierarchy and every record are validated before any storage mutation.
                ReplaceTree(card, replacement);
                if (card) cards.AddRange(cardCopies); else dictionaries.AddRange(dictionaryCopies);
                return remap[oldRoot];
            default: throw new NotSupportedException(method);
        }
    }
}

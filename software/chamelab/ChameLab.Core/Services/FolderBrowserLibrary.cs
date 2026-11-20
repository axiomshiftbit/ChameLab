using System;
using System.Collections.Generic;
using System.Linq;
using ChameLab.Models;

namespace ChameLab.Services;

/// <summary>Path presentation over the existing ID-based folder storage. No second folder store.</summary>
public sealed class FolderBrowserLibrary : IFolderLibrary
{
    private readonly Action<Action> _subscribe;
    private readonly Action<Action> _unsubscribe;
    private readonly Func<FolderLibrary> _tree;
    private readonly Func<IEnumerable<(string Id, string? FolderId, string Path)>> _items;
    private readonly Func<IEnumerable<string>> _legacy;
    private readonly Action<IEnumerable<SavedFolder>, IReadOnlyDictionary<string, string?>> _commit;
    public FolderBrowserLibrary(CardLibrary library)
    {
        _tree = () => library.FolderTree;
        _items = () => library.Cards.Select(c => (c.Id, c.FolderId, c.Folder));
        _legacy = () => library.Folders;
        _commit = library.ReplaceFolders;
        _subscribe = handler => library.Changed += handler;
        _unsubscribe = handler => library.Changed -= handler;
    }
    public FolderBrowserLibrary(DictionaryLibrary library)
    {
        _tree = () => library.FolderTree;
        _items = () => library.Dictionaries.Select(d => (d.Id, d.FolderId, d.Folder));
        _legacy = () => library.Dictionaries.Select(d => d.Folder);
        _commit = library.ReplaceFolders;
        _subscribe = handler => library.Changed += handler;
        _unsubscribe = handler => library.Changed -= handler;
    }
    public event Action? Changed { add { if (value is not null) _subscribe(value); } remove { if (value is not null) _unsubscribe(value); } }
    public IReadOnlyList<string> Folders => _tree().Paths.Values.Concat(_legacy()).Select(FolderPath.Normalize)
        .SelectMany(FolderPath.SelfAndAncestors).Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(p => p, FolderPath.TreeOrder).ToArray();
    public IEnumerable<string> ItemFolders => _items().Select(i => FolderPath.Normalize(i.Path));
    public string? FindFolder(string? path) => Folders.FirstOrDefault(p => FolderPath.Same(p, FolderPath.Normalize(path)));

    private (List<SavedFolder> Tree, Dictionary<string, string?> Assignments) Prepare()
    {
        if (_tree().LoadError is { } error) throw new InvalidOperationException(error);
        var tree = _tree().Items.ToList();
        foreach (var path in Folders) Ensure(tree, path);
        var paths = SavedFolder.Paths(tree);
        var assignments = _items().ToDictionary(i => i.Id, i => i.FolderId is { } id && paths.ContainsKey(id)
            ? (string?)id : paths.FirstOrDefault(p => FolderPath.Same(p.Value, FolderPath.Normalize(i.Path))).Key);
        return (tree, assignments);
    }
    private static string? Ensure(List<SavedFolder> tree, string path)
    {
        string? parent = null;
        foreach (var component in FolderPath.Normalize(path).Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var name = component.Trim();
            var folder = tree.FirstOrDefault(f => f.ParentId == parent && FolderPath.Same(f.Name, name));
            if (folder is null) { folder = new SavedFolder(Guid.NewGuid().ToString(), name, "", parent); tree.Add(folder); }
            parent = folder.Id;
        }
        _ = SavedFolder.Paths(tree);
        return parent;
    }
    public string? AddFolder(string path)
    {
        path = FolderPath.Normalize(path);
        if (path.Length == 0 || FindFolder(path) is not null) return null;
        var (tree, assignments) = Prepare();
        var id = Ensure(tree, path)!;
        var result = SavedFolder.Paths(tree)[id];
        _commit(tree, assignments);
        return result;
    }
    public string? RenameFolder(string path, string newName) => Relocate(path, FolderPath.Parent(path), FolderPath.NormalizeName(newName));
    public string? MoveFolder(string path, string parent) => Relocate(path, parent, FolderPath.Name(path));
    internal string? Relocate(string path, string parent, string name)
    {
        path = FindFolder(path) ?? ""; parent = FolderPath.Normalize(parent);
        if (path.Length == 0 || name.Length == 0 || FolderPath.IsWithin(parent, path)) return null;
        if (parent.Length > 0 && FindFolder(parent) is null) return null;
        var to = FolderPath.Combine(parent, name);
        if (FindFolder(to) is { } taken && !FolderPath.Same(taken, path)) return null;
        var (tree, assignments) = Prepare();
        var paths = SavedFolder.Paths(tree);
        var id = paths.First(p => FolderPath.Same(p.Value, path)).Key;
        var parentId = paths.FirstOrDefault(p => FolderPath.Same(p.Value, parent)).Key;
        var index = tree.FindIndex(f => f.Id == id);
        tree[index] = tree[index] with { Name = name, ParentId = parentId };
        var result = SavedFolder.Paths(tree)[id];
        _commit(tree, assignments);
        return result;
    }
    public void RemoveFolder(string path)
    {
        if (FindFolder(path) is not { } found) return;
        var (tree, assignments) = Prepare();
        var paths = SavedFolder.Paths(tree);
        var removed = paths.Where(p => FolderPath.IsWithin(p.Value, found)).Select(p => p.Key).ToHashSet();
        var parentId = paths.FirstOrDefault(p => FolderPath.Same(p.Value, FolderPath.Parent(found))).Key;
        foreach (var id in assignments.Keys.ToArray())
            if (assignments[id] is { } folderId && removed.Contains(folderId)) assignments[id] = parentId;
        _commit(tree.Where(f => !removed.Contains(f.Id)), assignments);
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using ChameLab.Models;
using ChameLab.Localization;

namespace ChameLab.Services;

/// <summary>The user's saved key dictionaries, persisted via an <see cref="IKeyValueStore"/>.</summary>
public sealed class DictionaryLibrary
{
    private const string StoreKey = "dictionaries";
    private readonly IKeyValueStore _store;
    private readonly List<KeyDictionary> _dicts = new();

    public DictionaryLibrary(IKeyValueStore store)
    {
        _store = store;
        KnownKeys = new KnownCardKeys(store);
        KnownKeys.Changed += () => Changed?.Invoke();
        Load();
    }

    public IReadOnlyList<KeyDictionary> Dictionaries => _dicts;
    public KnownCardKeys KnownKeys { get; }
    public FolderLibrary FolderTree => field ??= new FolderLibrary(_store, "dictionaryFolderTree");

    public string? LoadError { get; private set; }

    public void ReplaceFolders(IEnumerable<SavedFolder> folders, IReadOnlyDictionary<string, string?>? assignments = null)
    {
        if (LoadError is { } error) throw new InvalidOperationException(error);
        var replacement = folders.ToList();
        var paths = SavedFolder.Paths(replacement);
        var values = _dicts.Select(item =>
        {
            var id = assignments is not null && assignments.TryGetValue(item.Id, out var chosen) ? chosen : item.FolderId;
            var path = id is not null ? paths.GetValueOrDefault(id) ?? "" : assignments is not null && assignments.ContainsKey(item.Id) ? "" : item.Folder;
            if (id is not null && !paths.ContainsKey(id)) id = null;
            return (Item: item, Id: id, Path: path);
        }).ToList();
        var json = new JsonArray(values.Select(v =>
        {
            var node = v.Item.ToJson(); node["folderId"] = v.Id; node["folder"] = v.Path;
            return (JsonNode)node;
        }).ToArray());
        var writes = new Dictionary<string, string> { [StoreKey] = json.ToJsonString() };
        FolderTree.Commit(replacement, writes);
        foreach (var value in values) { value.Item.FolderId = value.Id; value.Item.Folder = value.Path; }
        Changed?.Invoke();
    }

    internal IKeyValueStore Store => _store;
    internal void Accept(DictionaryLibrary source)
    {
        _dicts.Clear(); _dicts.AddRange(source.Dictionaries);
        FolderTree.Accept(source.FolderTree);
        KnownKeys.Accept(source.KnownKeys);
    }
    internal void NotifyImport() => Changed?.Invoke();

    public event Action? Changed;

    private void Load()
    {
        string? json;
        try { json = _store.Read(StoreKey); }
        catch (Exception ex) { LoadError = ex.Message; return; }
        if (string.IsNullOrWhiteSpace(json)) return;

        try
        {
            if (JsonNode.Parse(json) is JsonArray list)
                foreach (var node in list)
                    if (node is not null) _dicts.Add(KeyDictionary.FromJson(node));
        }
        catch (Exception ex)
        {
            LoadError = string.Format(Strings.Library_DictionariesLoadError, ex.Message);
            _dicts.Clear();
        }
    }

    private void Save()
    {
        if (LoadError is not null) return;
        var array = new JsonArray(_dicts.Select(d => (JsonNode)d.ToJson()).ToArray());
        _store.Write(StoreKey, array.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
        Changed?.Invoke();
    }

    public void Add(KeyDictionary dict) => AddRange(new[] { dict });

    public void AddRange(IEnumerable<KeyDictionary> dicts)
    {
        foreach (var dict in dicts)
        {
            if (_dicts.Any(d => d.Id == dict.Id)) dict.Id = Guid.NewGuid().ToString();
            _dicts.Add(dict);
        }
        Save();
    }

    public void Update(KeyDictionary dict)
    {
        var i = _dicts.FindIndex(d => d.Id == dict.Id);
        if (i < 0) _dicts.Add(dict); else _dicts[i] = dict;
        Save();
    }

    /// <summary>
    /// Puts the given dictionaries in the given order. Only the places they already take in the
    /// library are reshuffled, so dictionaries left out (filtered from view) keep theirs.
    /// </summary>
    public void Reorder(IReadOnlyList<string> ids)
    {
        var byId = _dicts.GroupBy(d => d.Id).ToDictionary(g => g.Key, g => g.First());
        if (ids.Distinct().Count() != ids.Count || !ids.All(byId.ContainsKey)) return;
        var wanted = ids.ToHashSet();
        var slots = new List<int>();
        for (var i = 0; i < _dicts.Count; i++)
            if (wanted.Contains(_dicts[i].Id)) slots.Add(i);
        var moved = ids.Select(id => byId[id]).ToList();
        if (slots.Count != moved.Count) return;
        var changed = false;
        for (var i = 0; i < slots.Count; i++)
        {
            if (ReferenceEquals(_dicts[slots[i]], moved[i])) continue;
            _dicts[slots[i]] = moved[i];
            changed = true;
        }
        if (changed) Save();
    }

    public void MoveToFolder(KeyDictionary dictionary, string folder)
    {
        var browser = new FolderBrowserLibrary(this);
        if (folder.Length > 0 && browser.FindFolder(folder) is null) browser.AddFolder(folder);
        var id = FolderTree.Paths.FirstOrDefault(p => FolderPath.Same(p.Value, FolderPath.Normalize(folder))).Key;
        ReplaceFolders(FolderTree.Items, new Dictionary<string, string?> { [dictionary.Id] = id });
    }

    public void Remove(KeyDictionary dict)
    {
        if (_dicts.RemoveAll(d => d.Id == dict.Id) > 0) Save();
    }

    /// <summary>Only keys of the requested byte length; Classic callers default to six bytes.</summary>
    public IReadOnlyList<byte[]> AllKeys(int keySize = 6)
    {
        var seen = new HashSet<string>();
        var keys = new List<byte[]>();
        foreach (var k in _dicts.Where(d => d.KeySize == keySize).SelectMany(d => d.Keys).Where(k => k.Length == keySize))
            if (seen.Add(Convert.ToHexString(k))) keys.Add(k);
        return keys;
    }
}

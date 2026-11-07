using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using ChameLab.Models;

namespace ChameLab.Services;

/// <summary>Stable folder identities and validated parent relations alongside legacy display paths.</summary>
public sealed class FolderLibrary
{
    private readonly IKeyValueStore _store;
    private readonly string _key;
    private List<SavedFolder> _folders = [];
    public string? LoadError { get; }
    public IReadOnlyList<SavedFolder> Items => _folders;
    public IReadOnlyDictionary<string, string> Paths => SavedFolder.Paths(_folders);
    public FolderLibrary(IKeyValueStore store, string key)
    {
        _store = store; _key = key;
        try
        {
            var list = JsonNode.Parse(store.Read(key) ?? "[]") as JsonArray ?? throw new FormatException("Invalid folder collection.");
            _folders = list.Select(n => SavedFolder.FromJson(n!)).ToList();
            _ = SavedFolder.Paths(_folders);
        }
        catch (Exception ex) { _folders.Clear(); LoadError = ex.Message; }
    }
    public void Replace(IEnumerable<SavedFolder> folders)
    {
        if (LoadError is { } error) throw new InvalidOperationException(error);
        var replacement = folders.ToList(); _ = SavedFolder.Paths(replacement);
        _store.Write(_key, new JsonArray(replacement.Select(f => (JsonNode)f.ToJson()).ToArray()).ToJsonString());
        _folders = replacement;
    }
    public JsonArray ToJson() => new(_folders.Select(f => (JsonNode)f.ToJson()).ToArray());
}

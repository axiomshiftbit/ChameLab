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
    public void Replace(IEnumerable<SavedFolder> folders) => Commit(folders, new Dictionary<string, string>());

    // The folder tree and its object references are one logical write. Keep the old serialized
    // values until every store write succeeds; publish the in-memory tree only afterwards.
    internal void Commit(IEnumerable<SavedFolder> folders, IReadOnlyDictionary<string, string> values)
    {
        if (LoadError is { } error) throw new InvalidOperationException(error);
        var replacement = folders.ToList();
        _ = SavedFolder.Paths(replacement);
        var writes = new Dictionary<string, string>(values)
        {
            [_key] = new JsonArray(replacement.Select(f => (JsonNode)f.ToJson()).ToArray()).ToJsonString(),
        };
        var old = writes.Keys.ToDictionary(k => k, k => _store.Read(k));
        var attempted = new List<string>();
        try
        {
            foreach (var (key, value) in writes) { attempted.Add(key); _store.Write(key, value); }
        }
        catch
        {
            foreach (var key in attempted.AsEnumerable().Reverse()) _store.Write(key, old[key] ?? "[]");
            throw;
        }
        _folders = replacement;
    }
    internal void Accept(FolderLibrary source) => _folders = source.Items.ToList();

    public JsonArray ToJson() => new(_folders.Select(f => (JsonNode)f.ToJson()).ToArray());
}

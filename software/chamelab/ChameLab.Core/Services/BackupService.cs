using System;
using System.Linq;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ChameLab.Models;
using ChameLab.Localization;

namespace ChameLab.Services;

/// <summary>
/// Exports and imports the app's saved cards and key dictionaries as a single
/// JSON backup. The shape is <c>{ "format": "chamelab-backup",
/// "cards": [...], "dictionaries": [...] }</c>.
/// </summary>
public static class BackupService
{
    public const string FormatTag = "chamelab-backup";

    public static byte[] Export(CardLibrary cards, DictionaryLibrary dictionaries)
    {
        var o = new JsonObject
        {
            ["format"] = FormatTag,
            ["version"] = 1,
            ["cards"] = new JsonArray(cards.Cards.Select(c => (JsonNode)c.ToJson()).ToArray()),
            ["dictionaries"] = new JsonArray(dictionaries.Dictionaries.Select(d => (JsonNode)d.ToJson()).ToArray()),
            ["knownCardKeys"] = dictionaries.KnownKeys.ToJson(),
            ["cardFolderTree"] = cards.FolderTree.ToJson(),
            ["dictionaryFolderTree"] = dictionaries.FolderTree.ToJson(),
            ["cardFolders"] = new JsonArray(cards.Folders.Select(f => (JsonNode)f).ToArray()),
        };
        return Encoding.UTF8.GetBytes(o.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>Imports a backup, appending its cards and dictionaries. Returns the counts added.</summary>
    public static (int cards, int dictionaries) Import(byte[] data, CardLibrary cards, DictionaryLibrary dictionaries)
    {
        var text = Encoding.UTF8.GetString(data).TrimStart('﻿');
        if (JsonNode.Parse(text) is not JsonObject o)
            throw new FormatException(Strings.Backup_Invalid);
        if ((string?)o["format"] != FormatTag)
            return ImportChameleonGui(o, cards, dictionaries);

        var importedCards = o["cards"] is JsonArray ca
            ? ca.Where(n => n is not null).Select(n => CardSave.FromJson(n!)).ToList()
            : new();
        var importedDicts = o["dictionaries"] is JsonArray da
            ? da.Where(n => n is not null).Select(n => KeyDictionary.FromJson(n!)).ToList()
            : new();

        var cardFolders = PlanFolders(cards.FolderTree, o["cardFolderTree"]);
        var dictionaryFolders = PlanFolders(dictionaries.FolderTree, o["dictionaryFolderTree"]);
        foreach (var card in importedCards)
        {
            card.FolderId = card.FolderId is { } id ? cardFolders.Map.GetValueOrDefault(id) : null;
            if (card.FolderId is { } mapped) card.Folder = cardFolders.Paths[mapped];
        }
        foreach (var dictionary in importedDicts)
        {
            dictionary.FolderId = dictionary.FolderId is { } id ? dictionaryFolders.Map.GetValueOrDefault(id) : null;
            if (dictionary.FolderId is { } mapped) dictionary.Folder = dictionaryFolders.Paths[mapped];
        }
        // Both hierarchies and all records have been parsed before applying either tree.
        cards.ReplaceFolders(cardFolders.Folders);
        dictionaries.ReplaceFolders(dictionaryFolders.Folders);
        if (o["cardFolders"] is JsonArray legacyFolders)
            foreach (var folder in legacyFolders) cards.AddFolder((string?)folder ?? "");
        if (o["knownCardKeys"] is { } knownKeys) dictionaries.KnownKeys.Merge(knownKeys);
        if (importedCards.Count > 0) cards.AddRange(importedCards);
        if (importedDicts.Count > 0) dictionaries.AddRange(importedDicts);
        return (importedCards.Count, importedDicts.Count);
    }

    private static (List<SavedFolder> Folders, Dictionary<string, string> Map, IReadOnlyDictionary<string, string> Paths)
        PlanFolders(FolderLibrary target, JsonNode? node)
    {
        if (target.LoadError is { } error) throw new InvalidOperationException(error);
        var incoming = node is JsonArray array ? array.Select(n => SavedFolder.FromJson(n!)).ToList() : new List<SavedFolder>();
        var sourcePaths = SavedFolder.Paths(incoming);
        var folders = target.Items.ToList(); var paths = target.Paths;
        var map = incoming.ToDictionary(f => f.Id, f => paths.FirstOrDefault(p =>
            p.Value.Equals(sourcePaths[f.Id], StringComparison.OrdinalIgnoreCase)).Key ?? Guid.NewGuid().ToString());
        foreach (var f in incoming)
            if (!folders.Any(existing => existing.Id == map[f.Id]))
                folders.Add(f with { Id = map[f.Id], ParentId = f.ParentId is null ? null : map[f.ParentId] });
        return (folders, map, SavedFolder.Paths(folders));
    }

    /// <summary>
    /// Imports the Flutter Chameleon Ultra GUI's settings export (or its shared_preferences.json):
    /// cards with their folders, and dictionaries not already here.
    /// </summary>
    private static (int cards, int dictionaries) ImportChameleonGui(JsonObject o, CardLibrary cards, DictionaryLibrary dictionaries)
    {
        if (ChameleonGuiImport.TryRead(o) is not { } gui)
            throw new FormatException(Strings.Backup_NotChameLab);
        var (added, refiled) = cards.Merge(gui.Cards, gui.Folders);
        var have = dictionaries.Dictionaries.Select(d => d.Id).ToHashSet();
        var newDicts = gui.Dictionaries.Where(d => have.Add(d.Id)).ToList();
        if (newDicts.Count > 0) dictionaries.AddRange(newDicts);
        return (added + refiled, newDicts.Count);
    }
}

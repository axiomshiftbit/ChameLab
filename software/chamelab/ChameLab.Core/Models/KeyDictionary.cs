using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using ChameLab.Localization;

namespace ChameLab.Models;

/// <summary>
/// A named set of equally sized keys (4, 6 or 16 bytes). The JSON shape matches
/// the Flutter ChameleonUltraGUI <c>Dictionary</c> so dictionaries move between
/// the two apps; a plain <c>.dic</c> / <c>.keys</c> / <c>.txt</c> file (one hex key per line)
/// also imports and exports.
/// </summary>
public sealed class KeyDictionary
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public string Color { get; set; } = CardSave.DefaultColor;

    /// <summary>Bytes per key; Flutter's keyLength field counts hex digits instead.</summary>
    public int KeySize { get; set; } = 6;
    public string? FolderId { get; set; }
    public string Folder { get; set; } = "";

    public static bool IsSupportedSize(int size) => size is 4 or 6 or 16;

    /// <summary>Keys retain their complete bytes on import, editing, backup and export.</summary>
    public List<byte[]> Keys { get; set; } = new();

    public int Count => Keys.Count;

    public KeyDictionary Clone()
    {
        var copy = FromJson(ToJson());
        copy.Id = Guid.NewGuid().ToString();
        return copy;
    }

    // ---- JSON (Flutter-compatible) ------------------------------------

    public JsonObject ToJson() => new()
    {
        ["id"] = Id,
        ["name"] = Name,
        ["folderId"] = FolderId,
        ["folder"] = Folder,
        ["color"] = Color,
        ["keyLength"] = KeySize * 2,
        ["keys"] = new JsonArray(Keys.Select(k =>
            (JsonNode)new JsonArray(k.Select(b => (JsonNode)(int)b).ToArray())).ToArray()),
    };

    public static KeyDictionary FromJson(JsonNode node)
    {
        if (node is not JsonObject o) throw new FormatException(Strings.Import_DictNotObject);
        var dict = new KeyDictionary
        {
            Id = (string?)o["id"] ?? Guid.NewGuid().ToString(),
            Name = (string?)o["name"] ?? string.Empty,
            FolderId = (string?)o["folderId"],
            Folder = CardSave.NormalizeFolder((string?)o["folder"]),
            Color = CardSave.NormalizeColor((string?)o["color"]),
            KeySize = (int?)o["keyLength"] is { } length ? (length % 2 == 0 ? length / 2 : 0) : 6,
        };
        if (!IsSupportedSize(dict.KeySize)) throw new FormatException(Strings.Import_DictionaryKeySize);
        if (o["keys"] is JsonArray keys)
            foreach (var k in keys)
                {
                    if (k is not JsonArray a) throw new FormatException(Strings.Import_DictionaryKeySize);
                    var bytes = a.Select(v => checked((byte)(v?.GetValue<int>() ?? -1))).ToArray();
                    if (bytes.Length != dict.KeySize) throw new FormatException(Strings.Import_DictionaryKeySize);
                    dict.Keys.Add(bytes);
                }
        return dict;
    }

    public static KeyDictionary FromJson(string json) =>
        FromJson(JsonNode.Parse(json) ?? throw new FormatException(Strings.Import_EmptyJson));

    // ---- Plain text (.dic) --------------------------------------------

    /// <summary>Parses one key per line, inferring 4/6/16 bytes from the first key; supports '#' comments.</summary>
    public static KeyDictionary FromText(string name, string text)
    {
        var dict = new KeyDictionary { Name = name };
        var seen = new HashSet<string>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Split('#')[0].Trim().TrimStart('\uFEFF');
            if (line.Length == 0) continue;
            if (!Hex.TryParse(line, out var key) || !IsSupportedSize(key.Length)) continue;
            if (dict.Keys.Count == 0) dict.KeySize = key.Length;
            if (key.Length != dict.KeySize) throw new FormatException(Strings.Import_DictionaryKeySize);
            if (seen.Add(Convert.ToHexString(key))) dict.Keys.Add(key);
        }
        return dict;
    }

    public string ToText()
    {
        var sb = new StringBuilder();
        foreach (var k in Keys) sb.AppendLine(Convert.ToHexString(k));
        return sb.ToString();
    }

    /// <summary>Parses the keys from an editor text box; throws on an invalid line.</summary>
    public static List<byte[]> ParseKeys(string text) => ParseKeys(text, out _);

    /// <summary>
    /// Parses the keys from an editor text box, dropping repeats (counted in
    /// <paramref name="duplicates"/>); throws on an invalid line.
    /// </summary>
    public static List<byte[]> ParseKeys(string text, out int duplicates)
    {
        var keys = new List<byte[]>();
        var seen = new HashSet<string>();
        var lineNo = 0;
        duplicates = 0;
        foreach (var raw in text.Split('\n'))
        {
            lineNo++;
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (!Hex.TryParse(line, out var key) || key.Length != 6)
                throw new FormatException(string.Format(Strings.Import_BadKeyLine, lineNo, line));
            if (seen.Add(Convert.ToHexString(key))) keys.Add(key);
            else duplicates++;
        }
        return keys;
    }
}

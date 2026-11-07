using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using ChameLab.Models;
using ChameLab.Protocol;
using ChameLab.Services;

namespace ChameLab.Automation;

/// <summary>Explicit portable API. GUI callbacks marshal collection changes and dialogs to the UI thread.</summary>
public sealed partial class PluginHost(
    Func<ChameleonClient?> connection, CardLibrary cards, DictionaryLibrary dictionaries,
    Func<string, JsonArray, CancellationToken, Task<JsonNode?>> gui,
    Func<Func<JsonNode?>, Task<JsonNode?>> dispatch) : IPluginHost
{
    private ChameleonClient Client => connection() ?? throw new InvalidOperationException("Connect a Chameleon first.");

    public async Task<JsonNode?> InvokeAsync(string method, JsonArray a, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (method.StartsWith("ui.", StringComparison.Ordinal) || method.StartsWith("settings.", StringComparison.Ordinal))
            return await gui(method, a, ct);
        if (method.StartsWith("codec.", StringComparison.Ordinal)) return Codec(method[6..], a);
        if (method.StartsWith("recovery.", StringComparison.Ordinal)) return await RecoverAsync(method[9..], a[0], ct);
        if (method == "device.connected") return connection() is { Connection.IsClosed: false };
        if (method == "device.enums") return Enums();
        if (method.StartsWith("cardFolders.", StringComparison.Ordinal) || method.StartsWith("dictionaryFolders.", StringComparison.Ordinal))
            return await dispatch(() => FolderData(method, a));
        if (method.StartsWith("cards.", StringComparison.Ordinal) && method is not ("cards.read" or "cards.write") ||
            method.StartsWith("dictionaries.", StringComparison.Ordinal))
            return await dispatch(() => SavedData(method, a));
        return await DeviceAsync(method, a, ct);
    }

    private JsonNode? SavedData(string method, JsonArray a)
    {
        ReconcileFolders(method.StartsWith("cards.", StringComparison.Ordinal));
        var card = a.Count > 0 && a[0] is JsonValue ? cards.Cards.FirstOrDefault(c => c.Id == Text(a[0])) : null;
        var dictionary = a.Count > 0 && a[0] is JsonValue ? dictionaries.Dictionaries.FirstOrDefault(d => d.Id == Text(a[0])) : null;
        switch (method)
        {
            case "cards.list": return new JsonArray(cards.Cards.Select(c => (JsonNode)c.ToJson()).ToArray());
            case "cards.get": return card?.ToJson();
            case "cards.save":
                if (cards.LoadError is { } cardError) throw new InvalidOperationException(cardError);
                var saved = CardSave.FromJson(a[0]!);
                if (string.IsNullOrWhiteSpace(saved.Name) || saved.Tag == TagSpecificType.Undefined || saved.Uid.Length == 0)
                    throw new FormatException("A card requires name, uid and tag.");
                saved.Folder = FolderPath(true, saved.FolderId);
                cards.Update(saved);
                return saved.Id;
            case "cards.move":
                if (card is null) return false;
                var movedCard = CardSave.FromJson(card.ToJson());
                movedCard.FolderId = a.Count > 1 ? (string?)a[1] : null;
                movedCard.Folder = FolderPath(true, movedCard.FolderId);
                cards.Update(movedCard); return true;
            case "cards.delete":
                if (card is null) return false;
                if (cards.LoadError is { } deleteError) throw new InvalidOperationException(deleteError);
                cards.Remove(card); return true;
            case "cards.decode":
                var source = Text(a[0]);
                return (OptionalText(a, 1, "json") switch
                {
                    "json" => CardSave.FromJson(source),
                    "pm3" => CardFormats.FromProxmarkJson(JsonNode.Parse(source)!),
                    "flipperNfc" => CardFormats.FromFlipperNfc(source),
                    "flipperRfid" => CardFormats.FromFlipperRfid(source),
                    "mct" => CardFormats.FromMct(source),
                    _ => throw new ArgumentException("Unknown card format."),
                }).ToJson();
            case "cards.encode":
                var encoded = CardSave.FromJson(a[0]!);
                return OptionalText(a, 1, "json") switch
                {
                    "json" => encoded.ToJson().ToJsonString(),
                    "bin" => BytesNode(encoded.Data.SelectMany(b => b).ToArray()),
                    _ => throw new ArgumentException("Unknown card format."),
                };
            case "dictionaries.list": return new JsonArray(dictionaries.Dictionaries.Select(d => (JsonNode)d.ToJson()).ToArray());
            case "dictionaries.get": return dictionary?.ToJson();
            case "dictionaries.save":
                if (dictionaries.LoadError is { } dictError) throw new InvalidOperationException(dictError);
                var data = Object(a[0]).DeepClone().AsObject();
                if (data["keyLength"] is null && data["keys"] is JsonArray { Count: > 0 } keys)
                    data["keyLength"] = Bytes(keys[0]).Length * 2;
                var dict = KeyDictionary.FromJson(data);
                if (string.IsNullOrWhiteSpace(dict.Name)) throw new FormatException("A dictionary requires a name.");
                dict.Folder = FolderPath(false, dict.FolderId);
                dictionaries.Update(dict); return dict.Id;
            case "dictionaries.move":
                if (dictionary is null) return false;
                var movedDictionary = KeyDictionary.FromJson(dictionary.ToJson());
                movedDictionary.FolderId = a.Count > 1 ? (string?)a[1] : null;
                movedDictionary.Folder = FolderPath(false, movedDictionary.FolderId);
                dictionaries.Update(movedDictionary); return true;
            case "dictionaries.delete":
                if (dictionary is null) return false;
                if (dictionaries.LoadError is { } error) throw new InvalidOperationException(error);
                dictionaries.Remove(dictionary); return true;
            case "dictionaries.decode": return KeyDictionary.FromText(OptionalText(a, 1, "Dictionary"), Text(a[0])).ToJson();
            case "dictionaries.encode": return KeyDictionary.FromJson(a[0]!).ToText();
            default: throw new NotSupportedException("Unknown saved-data operation: " + method);
        }
    }

    private static JsonNode Codec(string method, JsonArray a) => method switch
    {
        "base64Encode" => Convert.ToBase64String(Encoding.UTF8.GetBytes(Text(a[0]))),
        "base64Decode" => Encoding.UTF8.GetString(Convert.FromBase64String(Text(a[0]))),
        "compress" => Convert.ToBase64String(DevicePlugin.Compress(Encoding.UTF8.GetBytes(Text(a[0])))),
        "decompress" => Encoding.UTF8.GetString(DevicePlugin.Decompress(Convert.FromBase64String(Text(a[0])))),
        "base64EncodeBytes" => Convert.ToBase64String(Bytes(a[0])),
        "base64DecodeBytes" => BytesNode(Convert.FromBase64String(Text(a[0]))),
        "compressBytes" => Convert.ToBase64String(DevicePlugin.Compress(Bytes(a[0]))),
        "decompressBytes" => BytesNode(DevicePlugin.Decompress(Convert.FromBase64String(Text(a[0])))),
        _ => throw new NotSupportedException("Unknown codec: " + method),
    };

    internal static string Text(JsonNode? n) => n?.GetValue<string>() ?? throw new FormatException("Expected text.");
    internal static int Number(JsonNode? n) => n?.GetValue<int>() ?? throw new FormatException("Expected an integer.");
    internal static bool Boolean(JsonNode? n) => n?.GetValue<bool>() ?? throw new FormatException("Expected a boolean.");
    internal static JsonObject Object(JsonNode? n) => n is null or JsonArray { Count: 0 } ? new JsonObject() : n as JsonObject ?? throw new FormatException("Expected an object.");
    internal static string OptionalText(JsonArray a, int index, string fallback) => a.Count > index && a[index] is not null ? Text(a[index]) : fallback;
    internal static byte[] Bytes(JsonNode? n) => n is JsonArray a ? a.Select(v => checked((byte)Number(v))).ToArray() : throw new FormatException("Expected a byte table.");
    internal static JsonArray BytesNode(byte[]? bytes) => new((bytes ?? []).Select(b => (JsonNode)(int)b).ToArray());
    private static IReadOnlyList<byte[]> Keys(JsonNode? n) => n is null ? MifareClassic.DefaultKeys :
        n is JsonArray a ? a.Select(v => Bytes(v) is { Length: 6 } key ? key : throw new FormatException("Expected six-byte keys.")).ToArray() : throw new FormatException("Expected keys.");
    private static uint UInt(JsonNode? n) => checked((uint)ULong(n));
    private static ulong ULong(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s)
        ? ulong.Parse(s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? s[2..] : s, NumberStyles.HexNumber, CultureInfo.InvariantCulture)
        : n?.GetValue<ulong>() ?? throw new FormatException("Missing integer.");
}

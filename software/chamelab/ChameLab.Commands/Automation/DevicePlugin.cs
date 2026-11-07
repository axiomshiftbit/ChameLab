using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using ChameLab.Services;

namespace ChameLab.Automation;

public sealed record DevicePlugin(string Id, string Name, string Description, uint Color, string Code,
    IReadOnlyDictionary<string, string> Resources)
{
    public const int MaxDataBytes = 8 * 1024 * 1024;
    public const int MaxFileBytes = MaxDataBytes * 2;
    public const string Format = "chameleon-ultra-gui-plugin";

    public JsonObject ToJson(bool compress = false)
    {
        var resources = new JsonObject();
        foreach (var (name, value) in Resources) resources[name] = value;
        var data = new JsonObject { ["code"] = Code, ["resources"] = resources };
        if (string.IsNullOrWhiteSpace(Name) || Name.Length > 200 || Encoding.UTF8.GetByteCount(data.ToJsonString()) > MaxDataBytes)
            throw new FormatException("Invalid plugin name or data size.");
        return new JsonObject
        {
            ["format"] = Format, ["version"] = 1, ["id"] = Id, ["name"] = Name,
            ["description"] = Description, ["color"] = Color,
            ["encoding"] = compress ? "gzip+base64" : "json",
            ["data"] = compress ? JsonValue.Create(Convert.ToBase64String(Compress(Encoding.UTF8.GetBytes(data.ToJsonString())))) : data,
        };
    }

    public static DevicePlugin Parse(string text, bool freshId = false)
    {
        if (Encoding.UTF8.GetByteCount(text) > MaxFileBytes) throw new FormatException("Plugin file exceeds 16 MiB.");
        var root = JsonNode.Parse(text) as JsonObject ?? throw new FormatException("Expected a plugin object.");
        if ((string?)root["format"] != Format || (int?)root["version"] != 1) throw new FormatException("Unsupported plugin format.");
        var data = (string?)root["encoding"] switch
        {
            "json" => root["data"] as JsonObject,
            "gzip+base64" => JsonNode.Parse(Decompress(Convert.FromBase64String((string?)root["data"] ?? ""))) as JsonObject,
            _ => throw new FormatException("Unsupported plugin encoding."),
        } ?? throw new FormatException("Invalid plugin data.");
        var resources = data["resources"] as JsonObject ?? throw new FormatException("Invalid plugin resources.");
        var plugin = new DevicePlugin(freshId ? Guid.NewGuid().ToString() : (string?)root["id"] ?? Guid.NewGuid().ToString(),
            (string?)root["name"] ?? "", (string?)root["description"] ?? "", (uint?)root["color"] ?? 0xffff5722,
            (string?)data["code"] ?? "", resources.ToDictionary(p => p.Key, p => (string?)p.Value ?? throw new FormatException("Resources must be text.")));
        if (Encoding.UTF8.GetByteCount(plugin.ToJson().ToJsonString()) > MaxFileBytes) throw new FormatException("Plugin exceeds 16 MiB.");
        return plugin;
    }

    public static byte[] Compress(byte[] bytes)
    {
        using var output = new MemoryStream();
        using (var gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true)) gzip.Write(bytes);
        return output.ToArray();
    }
    public static byte[] Decompress(byte[] bytes)
    {
        using var input = new MemoryStream(bytes);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = gzip.Read(buffer)) > 0)
        {
            if (output.Length + read > MaxDataBytes) throw new FormatException("Expanded plugin data exceeds 8 MiB.");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }
}

public sealed class PluginLibrary
{
    private readonly IKeyValueStore _store;
    private readonly List<DevicePlugin> _items = [];
    public IReadOnlyList<DevicePlugin> Items => _items;
    public string? LoadError { get; }
    public PluginLibrary(IKeyValueStore store)
    {
        _store = store;
        try
        {
            if (JsonNode.Parse(store.Read("devicePlugins") ?? "[]") is JsonArray list)
                foreach (var item in list) _items.Add(DevicePlugin.Parse(item!.ToJsonString()));
        }
        catch (Exception ex) { _items.Clear(); LoadError = ex.Message; }
    }
    public void Save(DevicePlugin plugin)
    {
        if (LoadError is not null) throw new InvalidOperationException(LoadError);
        _ = plugin.ToJson();
        var index = _items.FindIndex(p => p.Id == plugin.Id);
        if (index >= 0) _items[index] = plugin; else _items.Add(plugin);
        Persist();
    }
    public void Delete(string id)
    {
        if (LoadError is not null) throw new InvalidOperationException(LoadError);
        _items.RemoveAll(p => p.Id == id);
        Persist();
    }
    private void Persist() => _store.Write("devicePlugins", new JsonArray(_items.Select(p => (JsonNode)p.ToJson()).ToArray()).ToJsonString());
}

using System.Text.Json.Nodes;
using System.Threading.Channels;
using ChameLab.Automation;
using ChameLab.Models;
using ChameLab.Protocol;
using ChameLab.Services;
using ChameLab.Transport;
using Xunit;

namespace ChameLab.Cli.Tests;

public class PluginHostTests
{
    // Upstream GPL-3.0 examples: GameTec-live/ChameleonUltraGUI, ee4501a.
    [Theory]
    [InlineData("saved-card-count", "You have 0 saved cards.")]
    [InlineData("create-dictionary", "Saved 2 example keys.")]
    [InlineData("toggle-theme", "Theme: dark")]
    [InlineData("choose-language", "language=ru")]
    [InlineData("offline-nested", "candidate keys from the example capture.")]
    public async Task OfficialExamplesRunThroughRealRuntimeAndPortableHost(string file, string expected)
    {
        var store = new MemoryKeyValueStore(); var cards = new CardLibrary(store); var dictionaries = new DictionaryLibrary(store);
        var messages = new List<string>();
        var host = new PluginHost(() => null, cards, dictionaries, (method, args, _) =>
        {
            JsonNode? result = method switch
            {
                "settings.options" => new JsonObject { ["language"] = new JsonArray("en", "ru") },
                "settings.get" => new JsonObject { ["theme"] = "light" },
                "ui.input" => JsonValue.Create("ru"),
                _ => null,
            };
            if (method.StartsWith("ui.") && method != "ui.input") messages.Add(args[0]!.GetValue<string>());
            if (method == "settings.update" && args[0]?["language"] is { } language) messages.Add("language=" + language.GetValue<string>());
            return Task.FromResult(result);
        }, work => Task.FromResult(work()));
        var plugin = DevicePlugin.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", file + ".plugin.json")));
        await new PluginRuntime().RunAsync(plugin, host).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Contains(messages, m => m.Contains(expected));
        if (file == "create-dictionary") Assert.Equal(2, Assert.Single(dictionaries.Dictionaries).Keys.Count);
    }
    [Fact]
    public async Task OfflineCardAndDictionaryOperationsPreserveSchemasAndDetachedData()
    {
        var store = new MemoryKeyValueStore(); var cards = new CardLibrary(store); var dictionaries = new DictionaryLibrary(store);
        var host = new PluginHost(() => null, cards, dictionaries, (_, _, _) => throw new Exception(), work => Task.FromResult(work()));
        var plugin = new DevicePlugin("fixture", "Fixture", "", 0, """
            local card = {name='Fixture', uid='01020304', tag=1001, sak=8, atqa={4,0}, data={}}
            local id = chameleon.cards.save(card)
            assert(chameleon.cards.get(id).name == 'Fixture')
            local copy = chameleon.cards.get(id)
            copy.name = 'Changed'
            assert(chameleon.cards.get(id).name == 'Fixture')
            chameleon.cards.save(copy)
            assert(#chameleon.cards.list() == 1)
            assert(chameleon.cards.delete(id))
            assert(not chameleon.cards.delete(id))
            local d = {name='Password fixture', keys={{0,1,2,3}}}
            local did = chameleon.dictionaries.save(d)
            assert(chameleon.dictionaries.get(did).keyLength == 8)
            assert(chameleon.codec.base64Decode(chameleon.codec.base64Encode('привет')) == 'привет')
            return chameleon.codec.decompressBytes(chameleon.codec.compressBytes({0,128,255}))
            """, new Dictionary<string, string>());
        var result = await new PluginRuntime().RunAsync(plugin, host);
        Assert.Equal(new[] {0,128,255}, result!.AsArray().Select(n => n!.GetValue<int>()));
        Assert.Empty(cards.Cards);
    }
    [Fact]
    public async Task SlotIndicesRawFlagsAndTagFieldOrderMatchOfficialApi()
    {
        var transport = new FixtureTransport();
        await using var connection = new ChameleonConnection(transport); await connection.OpenAsync();
        var client = new ChameleonClient(connection); var store = new MemoryKeyValueStore();
        var host = new PluginHost(() => client, new(store), new(store), (_,_,_) => throw new Exception(), work => Task.FromResult(work()));
        await host.InvokeAsync("slots.activateSlot", new JsonArray(0), default);
        Assert.Equal(new byte[] {0}, transport.Requests.Last().Data);
        await host.InvokeAsync("slots.activateSlot", new JsonArray(7), default);
        Assert.Equal(new byte[] {7}, transport.Requests.Last().Data);
        await Assert.ThrowsAsync<FormatException>(() => host.InvokeAsync("slots.activateSlot", new JsonArray(8), default));
        await host.InvokeAsync("device.send14ARaw", new JsonArray(new JsonArray(0x60)), default);
        Assert.Equal(new byte[] {0xF4,0,100,0,8,0x60}, transport.Requests.Last().Data);
        var tags = (await host.InvokeAsync("device.scan14443aTagKeep", [], default))!.AsArray();
        Assert.Equal(8, tags[0]!["sak"]!.GetValue<int>());
        Assert.Equal(4, tags[0]!["atqa"]![0]!.GetValue<int>());
        var enums = await host.InvokeAsync("device.enums", [], default);
        Assert.Equal(1003, enums!["commands"]!["setActiveSlot"]!.GetValue<int>());
    }
    private sealed class FixtureTransport : ITransport
    {
        private readonly Channel<byte[]> _responses = Channel.CreateUnbounded<byte[]>();
        private readonly FrameParser _parser = new();
        public List<ChameleonFrame> Requests { get; } = [];
        public TransportKind Kind => TransportKind.Tcp;
        public string DisplayName => "Fixture";
        public bool IsConnected => true;
        public Task ConnectAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
        {
            foreach (var request in _parser.Push(data))
            {
                Requests.Add(request);
                _responses.Writer.TryWrite(FrameCodec.Encode(request.Command,
                    request.Command == Command.Hf14aScanKeep ? new byte[] {4,1,2,3,4,4,0,8,0} : [],
                    (ushort)(request.Command is Command.Hf14aRaw or Command.Hf14aScanKeep ? Status.HfTagOk : Status.Success)));
            }
            return Task.CompletedTask;
        }
        public async Task<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        { var response = await _responses.Reader.ReadAsync(ct); response.CopyTo(buffer); return response.Length; }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

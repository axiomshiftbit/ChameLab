using System.Text;
using System.Text.Json.Nodes;
using ChameLab.Automation;
using ChameLab.Services;
using Xunit;

namespace ChameLab.Cli.Tests;

public class PluginTests
{
    private static DevicePlugin Plugin(string code) => new(Guid.NewGuid().ToString(), "Fixture", "", 0xff123456, code, new Dictionary<string, string>());

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PortableEnvelopeRoundTripsAndImportCreatesFreshIdentity(bool compressed)
    {
        var original = Plugin("return 42") with { Resources = new Dictionary<string, string> { ["helper.lua"] = "return 'привет'" } };
        var imported = DevicePlugin.Parse(original.ToJson(compressed).ToJsonString(), freshId: true);
        Assert.NotEqual(original.Id, imported.Id);
        Assert.Equal(original.Code, imported.Code);
        Assert.Equal(original.Resources, imported.Resources);
    }

    [Fact]
    public void ExpandedDataIsBoundedBeforeJsonParsing()
    {
        var compressed = DevicePlugin.Compress(new byte[DevicePlugin.MaxDataBytes + 1]);
        Assert.Throws<FormatException>(() => DevicePlugin.Decompress(compressed));
    }

    [Fact]
    public async Task HostCallsAreAwaitedAndErrorsCanBeCaughtInLua()
    {
        var host = new Host(async (name, args) =>
        {
            await Task.Delay(10);
            if (name == "device.connected") return JsonValue.Create(true);
            throw new InvalidOperationException("fixture error");
        });
        var result = await new PluginRuntime().RunAsync(Plugin("""
            assert(chameleon.device.connected())
            local ok, message = pcall(function() chameleon.device.unknown() end)
            assert(not ok and string.find(message, 'fixture error'))
            return 42
            """), host);
        Assert.Equal(42, result!.GetValue<int>());
    }

    [Fact]
    public async Task ResourcesUsePluginLocalLoaderAndAmbientApisAreAbsent()
    {
        var plugin = Plugin("""
            assert(io == nil and os == nil and loadfile == nil and dofile == nil and load == nil)
            assert(require('helper.lua') == 'local resource')
            return chameleon.resources.get('text')
            """) with { Resources = new Dictionary<string, string> { ["helper.lua"] = "return 'local resource'", ["text"] = "hello" } };
        var result = await new PluginRuntime().RunAsync(plugin, new Host((_, _) => throw new Exception("No host calls expected")));
        Assert.Equal("hello", result!.GetValue<string>());
    }

    [Fact]
    public async Task StopInterruptsLoopsIncludingRequiredResourceCode()
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var plugin = Plugin("require('loop.lua')") with { Resources = new Dictionary<string, string> { ["loop.lua"] = "while true do end" } };
        var task = new PluginRuntime().RunAsync(plugin, new Host((_, _) => Task.FromResult<JsonNode?>(null)), cancellation.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void CorruptedLibraryCannotBeOverwrittenBySaveOrDelete()
    {
        var store = new MemoryKeyValueStore();
        store.Write("devicePlugins", "broken");
        var library = new PluginLibrary(store);
        Assert.Throws<InvalidOperationException>(() => library.Save(Plugin("return 1")));
        Assert.Throws<InvalidOperationException>(() => library.Delete("anything"));
        Assert.Equal("broken", store.Read("devicePlugins"));
    }

    [Fact]
    public async Task SectorKeyMapUsesNumericZeroBasedIndicesInLua()
    {
        var host = new Host((method, _) => { Assert.Equal("classic.mf1CheckKeysOfSectors", method); return Task.FromResult<JsonNode?>(new JsonObject { ["0"] = new JsonArray(1,2,3,4,5,6) }); });
        var result = await new PluginRuntime().RunAsync(Plugin("local keys = chameleon.classic.mf1CheckKeysOfSectors({}, {}) return keys[0][6]"), host);
        Assert.Equal(6, result!.GetValue<int>());
    }

    private sealed class Host(Func<string, JsonArray, Task<JsonNode?>> invoke) : IPluginHost
    {
        public Task<JsonNode?> InvokeAsync(string method, JsonArray arguments, CancellationToken ct) => invoke(method, arguments);
    }
}

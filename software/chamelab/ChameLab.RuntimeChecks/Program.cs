using System.Text.Json.Nodes;
using ChameLab.Automation;
using ChameLab.Cli;

// Execute the published, trimmed binary: ordinary unit tests cannot detect a missing
// JSON metadata table or Lua built-in removed by the release linker.
var command = CommandCatalog.Find(["lf", "em", "4x05", "auth"]).Command
    ?? throw new InvalidOperationException("Command catalog was not retained.");
_ = CliArguments.Parse(command, ["--pwd", "00000000"]);
var plugin = new DevicePlugin("trim-check", "Trim check", "", 0, """
    assert(os == nil and io == nil and clr == nil)
    local values = {3, 1, 2}
    table.sort(values)
    local total = 0
    for _, value in ipairs(values) do total = total + value end
    local ok = pcall(function() error('expected') end)
    assert(not ok)
    local sectors = chameleon.classic.mf1CheckKeysOfSectors()
    return {total=total, text=string.upper('ok'), root=math.sqrt(49),
            resource=require('module').value, sector=sectors[0].keyA}
    """, new Dictionary<string, string> { ["module"] = "return {value=17}" });
var result = await new PluginRuntime().RunAsync(plugin, new TestHost());
if ((int?)result?["total"] != 6 || (string?)result?["text"] != "OK" || (int?)result?["root"] != 7
    || (int?)result?["resource"] != 17 || (bool?)result?["sector"] != true)
    throw new InvalidOperationException("Trimmed Lua runtime returned an invalid result.");
Console.WriteLine("Trimmed command catalog, Lua sandbox, built-ins, resources and host calls passed.");

sealed class TestHost : IPluginHost
{
    public Task<JsonNode?> InvokeAsync(string method, JsonArray arguments, CancellationToken ct)
        => Task.FromResult<JsonNode?>(new JsonObject { ["0"] = new JsonObject { ["keyA"] = true } });
}

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using MoonSharp.Interpreter;

namespace ChameLab.Automation;

public interface IPluginHost
{
    Task<JsonNode?> InvokeAsync(string method, JsonArray arguments, CancellationToken ct);
}

/// <summary>Pure Lua and explicit asynchronous host calls; no CLR, process, OS or file API.</summary>
public sealed class PluginRuntime
{
    public Task<JsonNode?> RunAsync(DevicePlugin plugin, IPluginHost host, CancellationToken ct = default)
        => Task.Run(async () =>
        {
            _ = plugin.ToJson();
            var script = new Script(CoreModules.Preset_HardSandbox | CoreModules.ErrorHandling | CoreModules.Metatables);
            script.Options.DebugPrint = _ => { };
            script.Globals.Set("_host", DynValue.NewCallback((_, args) =>
            {
                ct.ThrowIfCancellationRequested();
                var parameters = new Table(script);
                for (var i = 1; i < args.Count; i++) parameters.Set(i, args[i]);
                return DynValue.NewYieldReq([args[0], DynValue.NewTable(parameters)]);
            }));
            script.Globals.Set("_resource", DynValue.NewCallback((_, args) =>
            {
                var key = args[0].CastToString() ?? "";
                if (!plugin.Resources.TryGetValue(key, out var value)) throw new ScriptRuntimeException("Unknown resource: " + key);
                return DynValue.NewString(value);
            }));
            script.Globals.Set("_loadResource", DynValue.NewCallback((_, args) =>
            {
                var key = args[0].CastToString() ?? "";
                if (!plugin.Resources.TryGetValue(key, out var value)) throw new ScriptRuntimeException("Unknown resource: " + key);
                return script.LoadString(value, codeFriendlyName: key);
            }));
            script.DoString("""
                local function bind(path)
                    return function(...)
                        local ok, result = _host(path, ...)
                        if not ok then error(result, 2) end
                        return result
                    end
                end
                chameleon = {}
                for _, name in ipairs({'ui','codec','device','classic','ultralight','slots','cards','dictionaries',
                                       'cardFolders','dictionaryFolders','settings','recovery','commands'}) do
                    local namespace = name
                    chameleon[namespace] = setmetatable({}, {__index=function(_, key) return bind(namespace .. '.' .. key) end})
                end
                local loaded = {}
                local function resourceRequire(name)
                    if loaded[name] == nil then
                        local value = _loadResource(name)()
                        if value == nil then value = true end
                        loaded[name] = value
                    end
                    return loaded[name]
                end
                chameleon.resources = {get=_resource, require=resourceRequire}
                resources = chameleon.resources
                require = resourceRequire
                codec = chameleon.codec
                print = function(...) return chameleon.ui.snackbar(table.concat({...}, '\t')) end
                """);
            var coroutine = script.CreateCoroutine(script.LoadString(plugin.Code, codeFriendlyName: plugin.Name)).Coroutine;
            coroutine.AutoYieldCounter = 5000;
            DynValue[] resume = [];
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var value = coroutine.Resume(resume);
                if (coroutine.State == CoroutineState.Dead) return ToJson(value);
                if (coroutine.State == CoroutineState.ForceSuspended)
                {
                    resume = [];
                    await Task.Yield();
                    continue;
                }
                var request = value.Type == DataType.Tuple ? value.Tuple : [value];
                if (request.Length != 2 || request[0].Type != DataType.String || request[1].Type != DataType.Table)
                    throw new InvalidOperationException("Unexpected script yield.");
                try
                {
                    var result = await host.InvokeAsync(request[0].String, (JsonArray)ToJson(request[1])!, ct);
                    resume = [DynValue.True, FromJson(script, result, request[0].String == "classic.mf1CheckKeysOfSectors")];
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex) { resume = [DynValue.False, DynValue.NewString(ex.Message)]; }
            }
        }, ct);

    private static JsonNode? ToJson(DynValue value, int depth = 0)
    {
        if (depth > 32) throw new FormatException("Script table nesting exceeds 32 levels.");
        switch (value.Type)
        {
            case DataType.Nil: case DataType.Void: return null;
            case DataType.Boolean: return JsonValue.Create(value.Boolean);
            case DataType.Number: return JsonNode.Parse(value.Number.ToString("R", CultureInfo.InvariantCulture));
            case DataType.String: return JsonValue.Create(value.String);
            case DataType.Tuple: return new JsonArray(value.Tuple.Select(v => ToJson(v, depth + 1)).ToArray());
            case DataType.Table:
                var pairs = value.Table.Pairs.ToArray();
                if (pairs.All(p => p.Key.Type == DataType.Number && p.Key.Number >= 1 && p.Key.Number <= pairs.Length && p.Key.Number % 1 == 0))
                    return new JsonArray(Enumerable.Range(1, pairs.Length).Select(i => ToJson(value.Table.Get(i), depth + 1)).ToArray());
                var result = new JsonObject();
                foreach (var pair in pairs) result[pair.Key.CastToString() ?? throw new FormatException("Invalid table key.")] = ToJson(pair.Value, depth + 1);
                return result;
            default: throw new FormatException("Only values and tables can cross the plugin API.");
        }
    }

    private static DynValue FromJson(Script script, JsonNode? value, bool numericKeys = false)
    {
        if (value is null) return DynValue.Nil;
        if (value is JsonArray array)
        {
            var table = new Table(script);
            for (var i = 0; i < array.Count; i++) table.Set(i + 1, FromJson(script, array[i]));
            return DynValue.NewTable(table);
        }
        if (value is JsonObject obj)
        {
            var table = new Table(script);
            foreach (var pair in obj)
                if (numericKeys && int.TryParse(pair.Key, NumberStyles.None, CultureInfo.InvariantCulture, out var index))
                    table.Set(DynValue.NewNumber(index), FromJson(script, pair.Value));
                else table.Set(pair.Key, FromJson(script, pair.Value));
            return DynValue.NewTable(table);
        }
        return value.GetValueKind() switch
        {
            JsonValueKind.True => DynValue.True, JsonValueKind.False => DynValue.False,
            JsonValueKind.String => DynValue.NewString(value.GetValue<string>()),
            JsonValueKind.Number => DynValue.NewNumber(double.Parse(value.ToJsonString(), CultureInfo.InvariantCulture)),
            _ => DynValue.Nil,
        };
    }
}

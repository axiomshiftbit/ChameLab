using System.Text.Json.Nodes;
using ChameLab.Models;
using ChameLab.Protocol;
using ChameLab.Services;

namespace ChameLab.Automation;

public sealed partial class PluginHost
{
    private async Task<MifareDump> ClassicAsync(string operation, JsonNode? keys, CancellationToken ct)
    {
        var c = Client;
        var dictionary = Keys(keys).Concat(MifareClassic.DefaultKeys).DistinctBy(Convert.ToHexString).ToArray();
        var dump = await c.ReadMifareClassicAsync(dictionary, ct: ct);
        if (operation != "checkKeys" && (dump.KeyA.Any(k => k is null) || dump.KeyB.Any(k => k is null)))
        {
            var level = await c.DetectNtLevelAsync(ct);
            var hasKey = dump.KeyA.Concat(dump.KeyB).Any(k => k is not null);
            var method = level switch
            {
                MifareClassicPrngType.Static => MifareRecoveryMethod.StaticNested,
                MifareClassicPrngType.Weak when !hasKey => MifareRecoveryMethod.Darkside,
                MifareClassicPrngType.Weak => MifareRecoveryMethod.Nested,
                _ => MifareRecoveryMethod.Hardnested,
            };
            var recovery = new MifareKeyRecovery(c, AppServices.Hardnested);
            await recovery.RecoverAsync(dump, method, ct: ct);
            if (method == MifareRecoveryMethod.Darkside && dump.KeyA.Concat(dump.KeyB).Any(k => k is not null))
                await recovery.RecoverAsync(dump, MifareRecoveryMethod.Nested, ct: ct);
            if (operation == "read") await c.ReadMifareClassicAsync(dictionary, ct: ct, existing: dump);
        }
        return dump;
    }
    private async Task<bool> WriteCardAsync(CardSave card, JsonObject options, CancellationToken ct)
    {
        var c = Client;
        c.Connection.Journal?.Add("INFO", "Plugin: card write started");
        if (card.Tag.IsMifareClassic())
        {
            var inspection = await c.InspectWriteTargetAsync(false, ct: ct) ?? throw new InvalidOperationException("No target card.");
            var generation = (string?)options["method"] switch
            {
                "gen1" => MagicGeneration.Gen1a, "gen2" => MagicGeneration.Gen2, "gen3" => MagicGeneration.Gen3,
                "gen4" => MagicGeneration.Gen4, null => inspection.Generation ?? throw new InvalidOperationException("Select a write method; generation is unknown."),
                _ => throw new FormatException("Unknown write method."),
            };
            MifareDump? target = generation is MagicGeneration.Gen2 or MagicGeneration.Gen3 ? await c.ReadMifareClassicAsync(Keys(options["keys"]), ct: ct) : null;
            var source = card.ToMifareDump();
            var session = new MifareWriteSession(source, generation, targetKeys: target);
            var failures = await c.WriteMifareDumpToCardAsync(source, Keys(options["keys"]), generation, targetKeys: target, ct: ct, session: session);
            var verify = await c.VerifyMifareWriteAsync(session, ct: ct);
            if (failures.Count != 0 || verify.Count != 0) throw new InvalidOperationException("Write or read-back verification failed.");
        }
        else if (card.Tag.IsUltralight())
        {
            var key = options["key"] is { } password ? Hex.Parse(Text(password)) : null;
            var failed = await c.WriteUltralightUserPagesAsync(card, key, ct: ct);
            if (failed.Count != 0) throw new InvalidOperationException("Some pages were not written.");
        }
        else if (card.Tag.SenseType() == TagSenseType.Lf)
            await c.WriteLfToT55xxAsync(card.Tag, card.Uid, options["newKey"] is { } next ? Hex.Parse(Text(next)) : null,
                options["currentKey"] is { } old ? [Hex.Parse(Text(old))] : null, ct);
        else throw new NotSupportedException("Unsupported card writer.");
        c.Connection.Journal?.Add("INFO", "Plugin: card write completed");
        return true;
    }
}

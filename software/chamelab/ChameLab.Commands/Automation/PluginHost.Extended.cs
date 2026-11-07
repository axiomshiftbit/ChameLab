using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;
using ChameLab.Models;
using ChameLab.Protocol;
using ChameLab.Services;

namespace ChameLab.Automation;

public sealed partial class PluginHost
{
    private async Task<JsonNode?> ExtendedDeviceAsync(string method, JsonArray a, CancellationToken ct)
    {
        var c = Client;
        switch (method)
        {
            case "cards.read":
                var options = a.Count == 0 ? new JsonObject() : Object(a[0]);
                await c.SetDeviceModeAsync(DeviceMode.Reader, ct);
                var tags = await c.Hf14aScanAsync(ct);
                if (tags.Count != 1) throw new InvalidOperationException("Present exactly one HF card.");
                CardSave card;
                if (await c.Mf1IsSupportedAsync(ct)) card = CardFormats.FromMifareDump(await ClassicAsync("read", options["keys"], ct));
                else if (tags[0].Sak == 0) card = (await c.DumpUltralightAsync(ct: ct)).Card;
                else card = CardFormats.FromHf14a(tags[0]);
                if (options["name"] is { } name) card.Name = Text(name);
                return card.ToJson();
            case "cards.write": return await WriteCardAsync(CardSave.FromJson(a[0]!), a.Count > 1 ? Object(a[1]) : new(), ct);
            case "classic.checkKeys": case "classic.recover": case "classic.read":
                var dump = await ClassicAsync(method[8..], a.Count == 0 ? null : a[0], ct);
                return new JsonObject
                {
                    ["keys"] = new JsonArray(Enumerable.Range(0, 80).Select(i => (JsonNode)BytesNode(i / 2 >= dump.SectorCount ? null : i % 2 == 0 ? dump.KeyA[i / 2] : dump.KeyB[i / 2])).ToArray()),
                    ["data"] = new JsonArray(Enumerable.Range(0, 256).Select(i => (JsonNode)BytesNode(i < dump.BlockCount ? dump.GetBlock(i) : null)).ToArray()),
                    ["allKeysFound"] = dump.KeyA.All(k => k is not null) && dump.KeyB.All(k => k is not null),
                };
            case "slots.read":
                var active = await c.GetActiveSlotAsync(ct);
                var type = Tag(a[1]);
                var slot = (await c.GetAllSlotsAsync(ct)).Single(s => s.Index == active);
                if ((type.SenseType() == TagSenseType.Hf ? slot.HfType : slot.LfType) != type) throw new InvalidOperationException("Slot type does not match.");
                var saved = await c.ReadCardFromSlotAsync(slot, type.SenseType(), ct: ct);
                saved.Name = Text(a[0]); return saved.ToJson();
            case "slots.getSlotTagName":
                return Encoding.UTF8.GetString(await Wire(Command.GetSlotTagNick, [(byte)(Slot(a[0]) - 1), (byte)Sense(a[1])], ct));
            case "slots.getSlotTagNames": case "slots.getSlotTagTypes": case "slots.getEnabledSlots":
                return new JsonArray((await c.GetAllSlotsAsync(ct)).Select(s => (JsonNode)new JsonObject
                {
                    ["hf"] = method == "slots.getSlotTagNames" ? (JsonNode)s.HfNick : method == "slots.getSlotTagTypes" ? EnumName("TagType", (int)s.HfType) : s.HfEnabled,
                    ["lf"] = method == "slots.getSlotTagNames" ? (JsonNode)s.LfNick : method == "slots.getSlotTagTypes" ? EnumName("TagType", (int)s.LfType) : s.LfEnabled,
                }).ToArray());
            case "device.getBatteryCharge":
                var battery = await c.GetBatteryInfoAsync(ct);
                return new JsonObject { ["voltage"] = battery.Millivolts, ["percent"] = battery.Percent, ["condition"] = battery.Condition?.ToString().ToLowerInvariant() };
            case "device.getFirmwareVersion":
                var version = await Wire(Command.GetAppVersion, null, ct);
                return new JsonObject { ["legacyProtocol"] = version[0] == 0 && version[1] == 1, ["version"] = version[0] == 0 && version[1] == 1 ? 256 : BinaryPrimitives.ReadUInt16BigEndian(version) };
            case "device.getDeviceSettings":
                var settings = await Wire(Command.GetDeviceSettings, null, ct);
                if (settings.Length < 13) throw new FormatException("Incomplete settings response.");
                return new JsonObject
                {
                    ["animation"] = EnumName("AnimationSetting", settings[1]), ["aPress"] = EnumName("ButtonConfig", settings[2]),
                    ["bPress"] = EnumName("ButtonConfig", settings[3]), ["aLongPress"] = EnumName("ButtonConfig", settings[4]),
                    ["bLongPress"] = EnumName("ButtonConfig", settings[5]), ["pairingEnabled"] = settings[6] == 1,
                    ["key"] = Encoding.UTF8.GetString(settings, 7, 6), ["wakeTimeSeconds"] = settings.Length > 13 ? settings[13] : null,
                };
            case "classic.getMf1EmulatorSettings":
                var config = await c.GetMf1EmulatorConfigAsync(ct);
                return new JsonObject { ["isDetectionEnabled"] = config.Detection, ["isGen1a"] = config.Gen1a, ["isGen2"] = config.Gen2,
                    ["isAntiColl"] = config.BlockAntiColl, ["writeMode"] = EnumName("MifareWriteMode", (int)config.WriteMode) };
            case "ultralight.mf0NtagGetEmulatorConfig":
                var ul = await Wire(Command.Mf0NtagGetEmulatorConfig, null, ct);
                return new JsonObject { ["isDetectionEnabled"] = ul[0] != 0, ["isGen1a"] = false, ["isGen2"] = ul[1] != 0,
                    ["isAntiColl"] = false, ["writeMode"] = EnumName("MifareWriteMode", Math.Min(3, (int)ul[2])) };
            case "ultralight.mf0ResetAuthCount": return (await Wire(Command.Mf0NtagResetAuthCnt, null, ct))[0];
            case "ultralight.mf0EmulatorGetPageCount": return (await Wire(Command.Mf0NtagGetPageCount, null, ct))[0];
            case "ultralight.mf0NtagGetDetectionCount": return BinaryPrimitives.ReadUInt32BigEndian(await Wire(Command.Mf0NtagGetDetectionCount, null, ct));
            case "ultralight.mf0NtagGetDetectionLog":
                var log = await Wire(Command.Mf0NtagGetDetectionLog, U32(checked((uint)Number(a[0]))), ct);
                if (log.Length % 4 != 0) throw new FormatException("Invalid detection log.");
                return new JsonArray(log.Chunk(4).Select(b => (JsonNode)Convert.ToHexString(b)).ToArray());
            case "ultralight.mf0UlcAuth": case "ultralight.mf0UlcWritePage": case "ultralight.mf0UlcSetKey":
                var key = Bytes(a[0]); if (key.Length != 16) throw new FormatException("Expected a 16-byte Ultralight C key.");
                var cmd = method == "ultralight.mf0UlcAuth" ? Command.Mf0UlcAuth : method == "ultralight.mf0UlcWritePage" ? Command.Mf0UlcWrite : Command.Mf0UlcSetKey;
                byte[] payload = cmd == Command.Mf0UlcAuth ? key : cmd == Command.Mf0UlcWrite ? [..key, Byte(a[1]), ..Bytes(a[2])] : [..key, ..Bytes(a[1])];
                var auth = await DeviceTools.RawAsync(c, (int)cmd, payload, 5000, ct);
                if (auth.Status.IsOk()) return true;
                if (auth.Status is Status.MfErrAuth) return false;
                throw new ChameleonException(auth.Status.Describe(), auth.Status);
            case "device.hf14a4ApduSend":
                var response = Bytes(a[0]); await Wire(Command.Hf14a4ApduSend, [..U16(response.Length), ..response], ct); return null;
            case "device.hf14a4AddStaticResponse":
                var request = Bytes(a[0]); response = Bytes(a[1]);
                await Wire(Command.Hf14a4StaticResp, [checked((byte)request.Length), ..request, ..U16(response.Length), ..response], ct); return null;
            case "device.hf14a4SetAntiColl":
                var uid = Bytes(a[0]); var atqa = Bytes(a[1]); var ats = Bytes(a[3]);
                await Wire(Command.Hf14a4SetAntiColl, [checked((byte)uid.Length), ..uid, ..atqa, Byte(a[2]), checked((byte)ats.Length), ..ats], ct); return null;
            case "device.scan14443aTagKeep": return ParseTags(await Wire(Command.Hf14aScanKeep, null, ct));
            case "device.adcGenericRead": return BytesNode(await Wire((Command)3009, null, ct));
            case "device.ioProxDecodeRaw": return BytesNode(await Wire((Command)3012, Bytes(a[0]), ct));
            case "device.ioProxComposeID": return BytesNode(await Wire((Command)3013, [Byte(a[0]), Byte(a[1]), ..U16(Number(a[2]))], ct));
            case "classic.getMf1NTDistance":
                var distance = await c.GetNtDistanceAsync(Number(a[0]), KeyType(a[1]), Bytes(a[2]), ct);
                return new JsonObject { ["uid"] = distance.Uid, ["distance"] = distance.Distance };
            case "classic.getMf1NestedNonces": return await NestedAsync(a, ct);
            case "classic.mf1CheckKeysOfSectors": return await CheckSectorsAsync(a, ct);
            case "device.checkMf1Darkside": return (await c.CheckDarksideAsync(ct)) switch
            {
                DarksideResult.Vulnerable => "vulnerable", DarksideResult.CantFixNt => "cantFixNT", DarksideResult.LuckAuthOk => "luckAuthOK",
                DarksideResult.NotSendingNack => "notSendingNACK", DarksideResult.TagChanged => "tagChanged", _ => "fixed",
            };
            case "classic.getMf1Darkside":
                var (darkUid, item) = await c.GetDarksideAsync(Number(a[0]), KeyType(a[1]), Boolean(a[2]), Number(a[3]), ct);
                return new JsonObject { ["uid"] = darkUid, ["nt1"] = item.Nt, ["ks1"] = item.Ks1.ToString("X16"),
                    ["par"] = item.Par.ToString("X16"), ["nr"] = item.Nr, ["ar"] = item.Ar };
            default: throw new NotSupportedException("Unknown plugin operation: " + method);
        }
    }

    private static byte[] U16(int value) { var bytes = new byte[2]; BinaryPrimitives.WriteUInt16BigEndian(bytes, checked((ushort)value)); return bytes; }
    private static JsonArray ParseTags(byte[] data)
    {
        var tags = new JsonArray(); var offset = 0;
        while (offset < data.Length)
        {
            var size = data[offset++];
            if (offset + size + 4 > data.Length) throw new FormatException("Incomplete scan response.");
            var uid = data.AsSpan(offset, size).ToArray(); offset += size;
            var atqa = data.AsSpan(offset, 2).ToArray(); offset += 2; var sak = data[offset++];
            var atsLength = data[offset++];
            if (offset + atsLength > data.Length) throw new FormatException("Incomplete ATS.");
            tags.Add((JsonNode)TagNode(new(uid, atqa, sak, data.AsSpan(offset, atsLength).ToArray()))); offset += atsLength;
        }
        return tags;
    }
    private async Task<JsonNode> CheckSectorsAsync(JsonArray a, CancellationToken ct)
    {
        var mask = Bytes(a[0]); var keys = Keys(a[1]);
        if (mask.Length != 10 || keys.Count is < 1 or > 83) throw new FormatException("Expected a 10-byte mask and 1–83 keys.");
        var remaining = Enumerable.Range(0, 80).Count(i => (mask[i / 8] & (0x80 >> (i % 8))) == 0);
        var result = new JsonObject(); if (remaining == 0) return result;
        var response = await Client.Connection.SendAsync(Command.Mf1CheckKeysOfSectors, [..mask, ..keys.SelectMany(k => k)],
            timeoutMs: 1000 + (remaining + 1) * keys.Count * 100, ct: ct);
        if (response.Status is not (Status.HfTagOk or Status.HfTagNo)) throw new ChameleonException(response.Status.Describe(), response.Status);
        var bytes = response.Data;
        if (bytes.Length == 0 && response.Status == Status.HfTagNo) return result;
        if (bytes.Length != 490) throw new FormatException("Invalid sector key response.");
        for (var i = 0; i < 80; i++) if ((bytes[i / 8] & (0x80 >> (i % 8))) != 0) result[i.ToString()] = BytesNode(bytes.AsSpan(10 + i * 6, 6).ToArray());
        return result;
    }
    private async Task<JsonNode> NestedAsync(JsonArray a, CancellationToken ct)
    {
        var options = a.Count > 5 ? Object(a[5]) : new();
        var level = (string?)options["level"] ?? "weak";
        if (level == "hard")
        {
            var samples = await Client.GetHardnestedNoncesAsync((bool?)options["slow"] ?? false, Number(a[0]), KeyType(a[1]), Bytes(a[2]), Number(a[3]), KeyType(a[4]), ct: ct);
            return new JsonObject { ["nonces"] = new JsonArray(samples.Select(n => (JsonNode)new JsonObject { ["ntEnc"] = n.EncryptedNonce, ["parity"] = n.EncryptedParity }).ToArray()) };
        }
        if (level is not ("static" or "weak")) throw new FormatException("Unknown nonce level.");
        var nonces = await Client.GetNestedNoncesAsync(Number(a[0]), KeyType(a[1]), Bytes(a[2]), Number(a[3]), KeyType(a[4]), level == "static", ct);
        return new JsonObject { ["nonces"] = new JsonArray(nonces.Select(n => (JsonNode)new JsonObject { ["nt"] = n.Nt, ["ntEnc"] = n.NtEnc, ["parity"] = n.Parity }).ToArray()) };
    }
}

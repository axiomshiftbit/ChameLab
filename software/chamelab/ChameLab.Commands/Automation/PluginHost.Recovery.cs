using System.Buffers.Binary;
using System.Text.Json.Nodes;
using ChameLab.Crypto;
using ChameLab.Models;
using ChameLab.Protocol;
using ChameLab.Services;

namespace ChameLab.Automation;

public sealed partial class PluginHost
{
    private static async Task<JsonNode> RecoverAsync(string method, JsonNode? input, CancellationToken ct)
    {
        if (method == "hardNested")
        {
            var bytes = Bytes(input);
            if (bytes.Length < 15 || (bytes.Length - 6) % 9 != 0) throw new FormatException("Invalid hardnested capture.");
            var samples = new List<HardnestedSample>();
            for (var i = 6; i < bytes.Length; i += 9)
            {
                samples.Add(new(BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(i)), (byte)(bytes[i + 8] >> 4)));
                samples.Add(new(BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(i + 4)), (byte)(bytes[i + 8] & 15)));
            }
            var found = await AppServices.Hardnested.RecoverAsync(new(BinaryPrimitives.ReadUInt32BigEndian(bytes), samples), null, ct);
            return found is { } value ? new JsonArray((JsonNode)value) : new JsonArray();
        }
        var d = Object(input);
        var uid = UInt(d["uid"]);
        if (method == "mfkey64")
        {
            try { return new JsonArray((JsonNode)await Task.Run(() => MfKey.MfKey64(uid, UInt(d["nt"]), UInt(d["nrEnc"]), UInt(d["arEnc"]), UInt(d["atEnc"]), new() { CancellationToken = ct }), ct)); }
            catch (InvalidOperationException) { return new JsonArray(); }
        }
        byte Parity(string name) => UInt(d[name]) is <= 7 and var p ? (byte)p : throw new FormatException("Parity must be 0–7.");
        NestedNonce Nonce(int i, bool parity) => new(UInt(d["nt" + i]), UInt(d["nt" + i + "Enc"]), parity ? Parity("par" + i) : (byte)0);
        DetectionLog Log(int i) => new(0, MfKeyType.A, false, U32(uid), U32(UInt(d["nt" + i])), U32(UInt(d["nr" + i + "Enc"])), U32(UInt(d["ar" + i + "Enc"])));
        var request = method switch
        {
            "nested" => new KeyRecoveryRequest(KeyRecoveryOperation.Nested) { Uid = uid, Distance = UInt(d["distance"]), Nonces = [Nonce(0, true), Nonce(1, true)] },
            "staticNested" => new(KeyRecoveryOperation.StaticNested) { Uid = uid, KeyType = KeyType(d["keyType"]), Nonces = [Nonce(0, false), Nonce(1, false)] },
            "staticEncryptedNested" => new(KeyRecoveryOperation.StaticEncryptedNested) { Uid = uid, Nonces = [new(UInt(d["nt"]), UInt(d["ntEnc"]), checked((byte)UInt(d["ntParEnc"])))] },
            "darkside" => new(KeyRecoveryOperation.Darkside) { Uid = uid, Items = d["items"]!.AsArray().Select(n => { var i = Object(n); return new DarksideItem(UInt(i["nt1"]), ULong(i["ks1"]), ULong(i["par"]), UInt(i["nr"]), UInt(i["ar"])); }).ToArray() },
            "mfkey32" => new(KeyRecoveryOperation.Mfkey32) { Uid = uid, Logs = [Log(0), Log(1)] },
            _ => throw new NotSupportedException("Unknown recovery operation: " + method),
        };
        return new JsonArray((await AppServices.KeyRecovery.RecoverAsync(request, ct)).Select(k => (JsonNode)k).ToArray());
    }

    private static byte[] U32(uint value) { var bytes = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(bytes, value); return bytes; }
}

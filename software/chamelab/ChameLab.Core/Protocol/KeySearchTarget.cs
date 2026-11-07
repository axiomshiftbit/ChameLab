using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ChameLab.Localization;
using ChameLab.Models;

namespace ChameLab.Protocol;

public enum KeySearchKind { MifareClassic, NtagPassword, UltralightC, Em4x05 }
public sealed record KeySearchTarget(KeySearchKind Kind, byte[] Identity, int Block, MfKeyType KeyType, TagSpecificType Type)
{
    public int KeySize => Kind == KeySearchKind.MifareClassic ? 6 : Kind == KeySearchKind.UltralightC ? 16 : 4;
}

public sealed partial class ChameleonClient
{
    public async Task<KeySearchTarget> PrepareKeySearchAsync(KeySearchKind kind, int block = 0,
        MfKeyType keyType = MfKeyType.A, CancellationToken ct = default)
    {
        await SetDeviceModeAsync(DeviceMode.Reader, ct);
        if (kind == KeySearchKind.Em4x05)
        {
            if (!(await GetCapabilitiesAsync(ct)).Contains((ushort)Command.Em4x05CheckPassword))
                throw new NotSupportedException(Strings.Search_EmFirmware);
            // UID word is readable without LOGIN. No password is tried by this probe.
            var uid = await conn.SendExpectAsync(Command.Em4x05ReadWord, Status.LfTagOk, [1], ct: ct);
            if (uid.Data.Length != 4) throw new FormatException(Strings.Search_ResponseInvalid);
            return new(kind, uid.Data, 0, MfKeyType.A, TagSpecificType.Undefined);
        }
        var tags = await Hf14aScanAsync(ct);
        if (tags.Count != 1) throw new InvalidOperationException(Strings.Recovery_PlaceExactlyOne);
        var tag = tags[0];
        var type = CardFormats.GuessHfType(tag.Sak, tag.Atqa);
        if (kind == KeySearchKind.MifareClassic)
        {
            if (!await Mf1IsSupportedAsync(ct)) throw new NotSupportedException(Strings.Search_WrongCard);
            if (block < 0 || block >= MifareClassic.BlockCount(MifareClassic.FromSak(tag.Sak))) throw new ArgumentOutOfRangeException(nameof(block));
        }
        else
        {
            type = await IdentifyUltralightAsync(ct) ?? throw new NotSupportedException(Strings.Search_WrongCard);
            if (kind == KeySearchKind.UltralightC && type != TagSpecificType.Mf0Icu2) throw new NotSupportedException(Strings.Search_WrongCard);
            if (kind == KeySearchKind.NtagPassword)
            {
                var cfg = type.UltralightCfg0Page();
                if (cfg <= 0) throw new NotSupportedException(Strings.Search_WrongCard);
                // NXP NTAG21x / Ultralight EV1 ACCESS.AUTHLIM is bits 0..2 in CFG1 byte 0.
                // Never authenticate when this cannot be read or failed attempts are limited.
                var config = await UltralightReadAsync(cfg, false, null, ct);
                if (config is null || config.Length < 8 || (config[4] & 7) != 0)
                    throw new InvalidOperationException(Strings.Search_AuthLimited);
            }
        }
        return new(kind, tag.Uid.ToArray(), block, keyType, type);
    }

    public async Task<bool> CheckSearchCandidateAsync(KeySearchTarget target, byte[] candidate, CancellationToken ct = default)
    {
        if (candidate.Length != target.KeySize) throw new ArgumentException(Strings.Search_KeySize);
        if (target.Kind == KeySearchKind.Em4x05)
        {
            var identity = await conn.SendExpectAsync(Command.Em4x05ReadWord, Status.LfTagOk, [1], ct: ct);
            if (!identity.Data.AsSpan().SequenceEqual(target.Identity)) throw new InvalidOperationException(Strings.Recovery_CardChanged);
            var response = await conn.SendAsync(Command.Em4x05CheckPassword, candidate, timeoutMs: 5000, ct: ct);
            if (response.Status == Status.LfTagLoginRequired) return false;
            if (response.Status == Status.NotImplemented) throw new NotSupportedException(Strings.Search_EmReadProtection);
            if (response.Status != Status.LfTagOk) throw new ChameleonException(response.Status.Describe(), response.Status);
            if (!response.Data.AsSpan().SequenceEqual(target.Identity)) throw new InvalidOperationException(Strings.Recovery_CardChanged);
            return true;
        }
        var tags = await Hf14aScanAsync(ct);
        if (tags.Count != 1) throw new InvalidOperationException(Strings.Recovery_PlaceExactlyOne);
        if (!tags[0].Uid.AsSpan().SequenceEqual(target.Identity)) throw new InvalidOperationException(Strings.Recovery_CardChanged);
        if (target.Kind == KeySearchKind.MifareClassic)
        {
            var status = await Mf1AuthStatusAsync(target.Block, target.KeyType, candidate, ct);
            if (status == Status.HfTagOk) return true;
            if (status == Status.MfErrAuth) return false;
            throw new ChameleonException(status.Describe(), status);
        }
        if (target.Kind == KeySearchKind.UltralightC)
        {
            var response = await conn.SendAsync(Command.Mf0UlcAuth, candidate, ct: ct);
            if (response.Status == Status.HfTagOk) return true;
            if (response.Status == Status.MfErrAuth) return false;
            throw new ChameleonException(response.Status.Describe(), response.Status);
        }
        // Keep the raw NAK distinct from CRC errors and a missing RF response.
        // PACK has two data bytes and CRC-A; a wrong password returns a 4-bit NAK.
        await FieldOffAsync(ct);
        var payload = DeviceTools.HfPayload(0xF0, PwdAuth(candidate), 40, 200);
        var response1 = await conn.SendAsync(Command.Hf14aRaw, payload, ct: ct);
        if (response1.Status != Status.HfTagOk) throw new ChameleonException(response1.Status.Describe(), response1.Status);
        var pack = response1.Data;
        if (pack.Length == 4 && HfSniff.CrcA(pack.AsSpan(0, 2)) == (pack[2] | pack[3] << 8)) return true;
        if (pack.Length == 1 && (pack[0] & 0x0F) == 0x04) return false;
        throw new InvalidOperationException(Strings.Search_ResponseInvalid);
    }
}

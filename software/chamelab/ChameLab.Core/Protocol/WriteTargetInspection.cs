using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ChameLab.Models;
using ChameLab.Localization;

namespace ChameLab.Protocol;

public sealed record WriteTargetInspection(Hf14aTag Tag, TagSpecificType Type,
    MagicGeneration? Generation, bool Gen4, MifareDump? Memory)
{
    public int ReadableDataBlocks => Memory is null ? 0 : Enumerable.Range(1, Memory.BlockCount - 1)
        .Count(b => !MifareClassic.IsTrailer(b) && Memory.GetBlock(b) is not null);
    public int TotalDataBlocks => Memory is null ? 0 : Memory.BlockCount - Memory.SectorCount - 1;
    // A successful authentication alone says nothing about the contents. Exclude block 0 and trailers.
    public bool? DataIsZeroed => Memory is null ? null
        : Enumerable.Range(1, Memory.BlockCount - 1).Any(b => !MifareClassic.IsTrailer(b)
            && Memory.GetBlock(b) is { } data && data.Any(v => v != 0)) ? false
        : ReadableDataBlocks == TotalDataBlocks ? true : null;
    public int FactoryKeySectors => Memory is null ? 0 : Enumerable.Range(0, Memory.SectorCount)
        .Count(s => Memory.KeyA[s] is { } a && a.AsSpan().SequenceEqual(MifareClassic.DefaultKeys[0])
            && Memory.KeyB[s] is { } b && b.AsSpan().SequenceEqual(MifareClassic.DefaultKeys[0]));
}

public sealed partial class ChameleonClient
{
    /// <summary>Read-only probes. Never uses a write command to guess CUID/FUID capability.</summary>
    public async Task<WriteTargetInspection?> InspectWriteTargetAsync(bool checkMemory,
        byte[]? gen4Password = null, CancellationToken ct = default)
    {
        await SetDeviceModeAsync(DeviceMode.Reader, ct);
        IReadOnlyList<Hf14aTag> tags;
        try { tags = await Hf14aScanAsync(ct); }
        catch (ChameleonException ex) when (ex.Status == Status.HfTagNo) { return null; }
        if (tags.Count == 0) return null;
        if (tags.Count != 1) throw new InvalidOperationException(Strings.Recovery_PlaceExactlyOne);
        var tag = tags[0];
        var classic = await Mf1IsSupportedAsync(ct);
        MagicGeneration? generation = null;
        bool gen4 = false;
        try
        {
            if (classic)
            {
                await DropFieldAsync(ct);
                var wake = await Hf14aRawAsync(RawOpt.ActivateRfField | RawOpt.WaitResponse | RawOpt.KeepRfField,
                    [0x40], bitLen: 7, ct: ct);
                if (wake.Length == 1 && wake[0] == 0x0A)
                {
                    var unlock = await Hf14aRawAsync(RawOpt.WaitResponse | RawOpt.KeepRfField, [0x43], ct: ct);
                    if (unlock.Length == 1 && unlock[0] == 0x0A) generation = MagicGeneration.Gen1a;
                }
                await DropFieldAsync(ct);
                if (generation is null)
                {
                    var read = await Hf14aRawAsync(RawOpt.ActivateRfField | RawOpt.WaitResponse | RawOpt.AppendCrc | RawOpt.AutoSelect,
                        [0x30, 0x00], ct: ct);
                    if (read.Length == 18) generation = MagicGeneration.Gen3;
                }
            }
            // GTU/Ultimate read-configuration only. A password mismatch cannot rule Gen4 out.
            try
            {
                var config = await CreateGen4Client(gen4Password ?? new byte[4]).ReadConfigurationAsync(ct);
                gen4 = config.Protocol <= 1 && config.UidLength is 4 or 7 or 10;
            }
            catch (FormatException) { }
            catch (ChameleonException ex) when (IsRfFailure(ex)) { }
            ct.ThrowIfCancellationRequested();
            var type = classic ? MifareClassic.ToSlotTagType(MifareClassic.FromSak(tag.Sak))
                : (tag.Sak & 0x20) != 0 ? TagSpecificType.Hf14a4
                : await IdentifyUltralightAsync(ct) ?? TagSpecificType.Undefined;
            MifareDump? memory = null;
            if (classic && checkMemory)
            {
                memory = new MifareDump(MifareClassic.FromSak(tag.Sak), tag.Uid, tag.Atqa, tag.Sak);
                memory = await ReadMifareClassicAsync([MifareClassic.DefaultKeys[0]], ct: ct, existing: memory);
                ct.ThrowIfCancellationRequested();
            }
            // Detect replacement during the multi-command inspection.
            var current = await Hf14aScanAsync(ct);
            if (current.Count != 1 || !current[0].Uid.AsSpan().SequenceEqual(tag.Uid))
                throw new InvalidOperationException(Strings.Write_TargetUidChanged);
            return new(tag, type, generation, gen4, memory);
        }
        finally { if (!conn.IsClosed) await DropFieldAsync(CancellationToken.None); }
    }

    private async Task<IReadOnlyList<int>> WriteGen4Async(MifareWriteSession session, byte[] password,
        IProgress<MifareProgress>? progress, IProgress<MifareBlockProgress>? blocks, CancellationToken ct)
    {
        await StartMifareWriteAsync(session, progress, blocks, ct);
        var gen4 = CreateGen4Client(password);
        var config = await gen4.ReadConfigurationAsync(ct);
        var source = session.Source;
        if (config.Protocol != 0 || config.UidLength != source.Uid.Length || config.Bytes[28] + 1 < source.BlockCount)
            throw new InvalidOperationException(Strings.Writer_Incompatible);
        var failed = new List<int>();
        foreach (var block in Enumerable.Range(1, source.BlockCount - 1).Append(0))
        {
            ct.ThrowIfCancellationRequested();
            if (!session.NeedsWrite(block)) continue;
            var data = source.GetBlock(block)!;
            blocks?.Report(new(block, 1, MifareBlockState.Reading));
            await gen4.WriteBlockAsync(block, data, CancellationToken.None);
            var actual = await gen4.ReadBlockAsync(block, CancellationToken.None);
            if (actual.AsSpan().SequenceEqual(data)) session.Confirm(block); else failed.Add(block);
            blocks?.Report(new(block, 1, failed.Contains(block) ? MifareBlockState.ReadError : MifareBlockState.Read));
            progress?.Report(new(session.WrittenBlocks.Count + failed.Count, session.Total, session.WrittenBlocks.Count));
        }
        return failed;
    }

    /// <summary>Checks the written blocks. Hidden trailer keys are verified by authentication, never compared to masked read bytes.</summary>
    public async Task<IReadOnlyList<int>> VerifyMifareWriteAsync(MifareWriteSession session,
        byte[]? gen4Password = null, CancellationToken ct = default)
    {
        var failed = new List<int>();
        foreach (var block in session.WrittenBlocks.Order())
        {
            ct.ThrowIfCancellationRequested();
            bool match = false;
            try
            {
                var expected = session.Source.GetBlock(block)!;
                if (session.Generation == MagicGeneration.Gen4)
                    match = (await CreateGen4Client(gen4Password ?? new byte[4]).ReadBlockAsync(block, ct)).AsSpan().SequenceEqual(expected);
                else if (session.Generation == MagicGeneration.Gen1a)
                {
                    await DropFieldAsync(ct);
                    await Hf14aRawAsync(RawOpt.ActivateRfField | RawOpt.WaitResponse | RawOpt.KeepRfField, [0x40], bitLen: 7, ct: ct);
                    await Hf14aRawAsync(RawOpt.WaitResponse | RawOpt.KeepRfField, [0x43], ct: ct);
                    var read = await Hf14aRawAsync(RawOpt.WaitResponse | RawOpt.AppendCrc, [0x30, (byte)block], ct: ct);
                    match = read.Length >= 16 && read.AsSpan(0,16).SequenceEqual(expected);
                }
                else
                {
                    int sector = MifareClassic.SectorOfBlock(block);
                    foreach (var type in new[] { MfKeyType.A, MfKeyType.B })
                    {
                        var key = type == MfKeyType.A ? session.TargetKeys?.KeyA[sector] : session.TargetKeys?.KeyB[sector];
                        if (key is null) continue;
                        byte[] actual;
                        try { actual = await Mf1ReadBlockAsync(block, type, key, ct); }
                        catch (Exception ex) when (IsRfFailure(ex)) { continue; }
                        if (!MifareClassic.IsTrailer(block)) match = actual.AsSpan().SequenceEqual(expected);
                        else
                        {
                            var keyAOk = await Mf1AuthAsync(block, MfKeyType.A, expected[..6], ct);
                            var keyBOk = actual.AsSpan(10,6).SequenceEqual(expected.AsSpan(10,6))
                                || await Mf1AuthAsync(block, MfKeyType.B, expected[10..], ct);
                            match = actual.AsSpan(6,4).SequenceEqual(expected.AsSpan(6,4)) && keyAOk && keyBOk;
                        }
                        if (match) break;
                    }
                }
            }
            catch (Exception ex) when (IsRfFailure(ex)) { }
            if (!match) failed.Add(block);
            conn.Journal.Add(match ? "VERIFY" : "ERROR", $"block={block}: {(match ? "MATCH" : "NOT VERIFIED")}");
        }
        conn.Journal.Add(failed.Count == 0 ? "OK" : "ERROR",
            string.Format(Strings.Writer_Verified, session.WrittenBlocks.Count - failed.Count, failed.Count));
        return failed;
    }

    public Gen4Client CreateGen4Client(byte[] password) => new(async (data, ct) =>
        await Hf14aRawAsync(RawOpt.ActivateRfField | RawOpt.WaitResponse | RawOpt.AppendCrc | RawOpt.AutoSelect,
            data, ct: ct), password);
}

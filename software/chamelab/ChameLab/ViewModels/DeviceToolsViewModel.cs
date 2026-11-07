using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using ChameLab.Localization;
using ChameLab.Models;
using ChameLab.Protocol;
using ChameLab.Services;
using ReactiveUI;

namespace ChameLab.ViewModels;

public enum DeviceTool { ValueBlocks, T55Password, T55Block, Gen4, RawHf, RawDevice }
public sealed record ValueBlockRow(int Block, string Data, int? Value, int? Address);
public sealed record Gen4MemoryOption(string Name, int Address, int Length, int Stride);

public sealed class DeviceToolsViewModel : PageViewModelBase
{
    private readonly DeviceService _device;
    private readonly DictionaryLibrary _dictionaries;
    private CancellationTokenSource? _cts;
    private Task? _running;
    private Gen4Configuration? _configuration;
    private int? _packPage;

    public DeviceToolsViewModel(DeviceService device, DictionaryLibrary dictionaries, DeviceTool tool)
        : base(() => ToolName(tool), "tools")
    {
        _device = device;
        _dictionaries = dictionaries;
        Tool = tool;
        ExecuteCommand = ReactiveCommand.CreateFromTask<string>(ExecuteAsync);
        Recovery = new MifareKeyRecoveryViewModel(() => _device.Client, dictionaries, () => [], () => [],
            work => RunAsync(work));
        StopCommand = ReactiveCommand.Create(() => { _cts?.Cancel(); Recovery.Cancel(); });
        RefreshSources();
        dictionaries.Changed += RefreshSources;
        device.StateChanged += _ => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            this.RaisePropertyChanged(nameof(IsConnected));
            Recovery.SetAvailability(IsConnected, false);
            _configuration = null;
            _cts?.Cancel();
        });
        Recovery.SetAvailability(IsConnected, false);
    }

    public static string ToolName(DeviceTool tool) => tool switch
    {
        DeviceTool.ValueBlocks => Strings.Tool_live_value_blocks,
        DeviceTool.T55Password => Strings.Tool_t55xx_password_cleaner,
        DeviceTool.T55Block => Strings.Tool_t55xx_block_editor,
        DeviceTool.Gen4 => Strings.Tool_mifare_classic_gen4,
        DeviceTool.RawHf => Strings.Tool_raw_hf,
        _ => Strings.Tool_raw_device,
    };

    public DeviceTool Tool { get; }
    public bool IsValue => Tool == DeviceTool.ValueBlocks;
    public bool IsT55Password => Tool == DeviceTool.T55Password;
    public bool IsT55Block => Tool == DeviceTool.T55Block;
    public bool IsGen4 => Tool == DeviceTool.Gen4;
    public bool IsRawHf => Tool == DeviceTool.RawHf;
    public bool IsRawDevice => Tool == DeviceTool.RawDevice;
    public bool IsRaw => IsRawHf || IsRawDevice;
    public bool IsConnected => _device.State == ConnectionState.Connected;
    public bool Working { get; private set => this.RaiseAndSetIfChanged(ref field, value); }
    public string Result { get; private set => this.RaiseAndSetIfChanged(ref field, value); } = "";
    public ICommand ExecuteCommand { get; }
    public ICommand StopCommand { get; }
    public MifareKeyRecoveryViewModel Recovery { get; }
    public ObservableCollection<ValueBlockRow> Blocks { get; } = [];

    public string CommandNumber { get; set; } = "1000";
    public string Payload { get; set; } = "";
    public int Timeout { get; set; } = 1000;
    public string Bits { get; set; } = "";
    public bool ActivateRf { get; set; } = true;
    public bool WaitResponse { get; set; } = true;
    public bool AppendCrc { get; set; } = true;
    public bool AutoSelect { get; set; } = true;
    public bool KeepRf { get; set; }
    public bool CheckCrc { get; set; } = true;
    public int Page { get; set; }
    public int Block { get; set; } = 1;
    public int Destination { get; set; } = 1;
    public int Amount { get; set; } = 1;
    public string Data { get; set => this.RaiseAndSetIfChanged(ref field, value); } = "";
    public bool UsePassword { get; set; }
    public string Password { get; set => this.RaiseAndSetIfChanged(ref field, value); } = "00000000";
    public string NewPassword { get; set => this.RaiseAndSetIfChanged(ref field, value); } = "00000000";
    public bool ConfirmWrite { get; set; }
    public string PermanentConfirmation { get; set; } = "";
    public IReadOnlyList<KeyDictionary> PasswordDictionaries => _dictionaries.Dictionaries.Where(d => d.KeySize == 4).ToArray();
    public KeyDictionary? PasswordDictionary { get; set; }

    public int Gen4Protocol { get; set => this.RaiseAndSetIfChanged(ref field, value); }
    public IReadOnlyList<string> ProtocolOptions => ["MIFARE Classic", "Ultralight / NTAG"];
    public IReadOnlyList<string> UidOptions => ["4", "7", "10"];
    public IReadOnlyList<string> ShadowOptions => [Strings.Tool_gen4_prewrite, Strings.Tool_gen4_restore, Strings.Tool_disabled, Strings.Tool_gen4_high_speed, Strings.Tool_gen4_split];
    public IReadOnlyList<string> UltralightOptions => ["Ultralight EV1", "NTAG", "Ultralight C", "Ultralight"];
    public IReadOnlyList<string> Block0Options => [Strings.Tool_enabled, Strings.Tool_disabled, Strings.Tool_gen4_factory_default];
    public int UidSize { get; set => this.RaiseAndSetIfChanged(ref field, value); }
    public int Shadow { get; set => this.RaiseAndSetIfChanged(ref field, value); } = 2;
    public int UltralightMode { get; set => this.RaiseAndSetIfChanged(ref field, value); }
    public int Block0Mode { get; set => this.RaiseAndSetIfChanged(ref field, value); } = 2;
    public int MaxBlock { get; set => this.RaiseAndSetIfChanged(ref field, value); } = 63;
    public string Ats { get; set => this.RaiseAndSetIfChanged(ref field, value); } = "";
    public string Atqa { get; set => this.RaiseAndSetIfChanged(ref field, value); } = "0004";
    public string Sak { get; set => this.RaiseAndSetIfChanged(ref field, value); } = "08";
    public string Uid { get; set; } = "";
    public int PackPage { get; set => this.RaiseAndSetIfChanged(ref field, value); } = -1;
    public string MemoryData { get; set => this.RaiseAndSetIfChanged(ref field, value); } = "";
    public Gen4MemoryOption? Memory { get; set => this.RaiseAndSetIfChanged(ref field, value); }
    public IReadOnlyList<Gen4MemoryOption> MemoryOptions => _configuration?.Protocol == 1
        ? [new(Strings.Tool_gen4_otp, 3, 4, 4), new(Strings.Tool_gen4_ntag_password, 0xE5, 4, 4),
            new("PACK", _packPage ?? -1, 2, 4), new(Strings.Tool_version, 0xFA, 8, 4), new(Strings.Tool_gen4_signature, 0xF2, 32, 4)]
        : [new(Strings.Tool_gen4_signature, 0x45, 32, 16)];

    private void RefreshSources()
    {
        this.RaisePropertyChanged(nameof(PasswordDictionaries));
        Recovery.SetKeySources(new[] { new DumpKeySourceViewModel("", Strings.Common_BuiltInKeys, MifareClassic.DefaultKeys, true) }
            .Concat(_dictionaries.Dictionaries.Where(d => d.KeySize == 6).Select(d => new DumpKeySourceViewModel(d.Id, d.Name, d.Keys, true))));
    }

    internal static byte[] ParseHex(string text, int? length = null)
    {
        if (!Hex.TryParse(text, out var bytes) || length is { } n && bytes.Length != n)
            throw new FormatException(Strings.Tool_invalid_hex);
        return bytes;
    }

    private Task ExecuteAsync(string action) => _running = ExecuteCoreAsync(action);

    private async Task ExecuteCoreAsync(string action)
    {
        if (Working || Recovery.IsDumpRunning) return;
        using var cancellation = new CancellationTokenSource();
        _cts = cancellation;
        Working = true;
        try
        {
            await RunAsync(async () =>
            {
                var client = _device.Client ?? throw new InvalidOperationException(Strings.Tool_device_required);
                var mode = IsRawDevice ? DeviceMode.Reader : await client.GetDeviceModeAsync(cancellation.Token);
                try
                {
                    if (!IsRawDevice && mode != DeviceMode.Reader) await client.SetDeviceModeAsync(DeviceMode.Reader, cancellation.Token);
                    await RunToolAsync(client, action, cancellation.Token);
                }
                catch (OperationCanceledException) { Result = Strings.Common_Stop; }
                finally
                {
                    if (!IsRawDevice && mode != DeviceMode.Reader && !client.Connection.IsClosed)
                        await client.SetDeviceModeAsync(mode);
                }
            });
        }
        finally { _cts = null; Working = false; }
    }

    public override async Task OnDeactivatedAsync()
    {
        _cts?.Cancel();
        Recovery.Cancel();
        if (_running is not null) await _running;
        if (Recovery.RunningTask is { } recovery) await recovery;
    }

    private void RequireWrite()
    {
        if (!ConfirmWrite) throw new InvalidOperationException(Strings.Tool_confirm_write);
    }

    private static async Task RequireCapability(ChameleonClient client, Command command, CancellationToken ct)
    {
        var capabilities = await client.GetCapabilitiesAsync(ct);
        if (capabilities.Count > 0 && !capabilities.Contains((ushort)command))
            throw new NotSupportedException(Strings.Tool_unsupported_firmware);
    }

    private async Task RunToolAsync(ChameleonClient client, string action, CancellationToken ct)
    {
        if (IsRaw)
        {
            var data = string.IsNullOrWhiteSpace(Payload) ? Array.Empty<byte>() : ParseHex(Payload);
            var command = IsRawHf ? (int)Command.Hf14aRaw : int.Parse(CommandNumber);
            if (IsRawHf)
            {
                await RequireCapability(client, Command.Hf14aRaw, ct);
                var flags = (byte)((ActivateRf ? 128 : 0) | (WaitResponse ? 64 : 0) | (AppendCrc ? 32 : 0)
                    | (AutoSelect ? 16 : 0) | (KeepRf ? 8 : 0) | (CheckCrc ? 4 : 0));
                data = DeviceTools.HfPayload(flags, data, string.IsNullOrWhiteSpace(Bits) ? data.Length * 8 : int.Parse(Bits), Timeout);
            }
            var response = await DeviceTools.RawAsync(client, command, data, Timeout + (IsRawHf ? 5000 : 0), ct);
            Result = $"CMD 0x{response.RawCommand:X4}\nSTATUS 0x{response.RawStatus:X4}\nRX ({response.Data.Length} B): {Convert.ToHexString(response.Data)}";
        }
        else if (IsT55Block)
        {
            RequireWrite();
            await RequireCapability(client, Command.LfT55xxWrite, ct);
            await client.Connection.SendExpectAsync(Command.LfT55xxWrite, Status.Success,
                DeviceTools.T55Payload(Page, Block, ParseHex(Data, 4), UsePassword ? ParseHex(Password, 4) : null), ct: ct);
            Result = Strings.Tool_t55xx_block_editor_sent;
        }
        else if (IsT55Password)
        {
            RequireWrite();
            var keys = PasswordDictionary?.Keys.Select(k => k.ToArray()).ToArray() ?? throw new InvalidOperationException(Strings.Tool_select_dictionary);
            var next = ParseHex(NewPassword, 4);
            await RequireCapability(client, Command.Em410xWriteToT55xx, ct);
            for (var i = 0; i < keys.Length; i++)
            {
                ct.ThrowIfCancellationRequested();
                Result = $"{i + 1} / {keys.Length}";
                try
                {
                    // Like the reference tool, this replaces the tag with the test EM410x ID.
                    await client.WriteLfToT55xxAsync(TagSpecificType.Em410x, [0xDE,0xAD,0xBE,0xEF,0xFF], next, [keys[i]], ct);
                    Result = Strings.Tool_operation_verified;
                    return;
                }
                catch (Exception ex) when (ex is ChameleonException or InvalidOperationException) { }
            }
            throw new InvalidOperationException(Strings.Tool_password_not_found);
        }
        else if (IsGen4) await RunGen4Async(client, action, ct);
        else if (IsValue) await RunValueAsync(client, action, ct);
    }

    private Gen4Client Gen4(ChameleonClient client) => new(async (data, ct) =>
    {
        var response = await client.Connection.SendExpectAsync(Command.Hf14aRaw, Status.HfTagOk,
            DeviceTools.HfPayload(0xF4, data, data.Length * 8, 1000), timeoutMs: 6000, ct: ct);
        return response.Data;
    }, ParseHex(Password, 4));

    private async Task RunGen4Async(ChameleonClient client, string action, CancellationToken ct)
    {
        await RequireCapability(client, Command.Hf14aRaw, ct);
        var gen = Gen4(client);
        if (action == "read-config")
        {
            _configuration = await gen.ReadConfigurationAsync(ct);
            var b = _configuration.Bytes;
            Gen4Protocol = b[0]; UidSize = b[1]; NewPassword = Convert.ToHexString(b[2..6]); Shadow = b[6];
            Ats = Convert.ToHexString(b.AsSpan(8, b[7])); Atqa = Convert.ToHexString([b[25], b[24]]);
            Sak = b[26].ToString("X2"); UltralightMode = b[27]; MaxBlock = b[28]; Block0Mode = b[29];
            var version = await gen.ReadVersionAsync(ct);
            _packPage = Gen4Configuration.PackPage(version);
            PackPage = _packPage ?? -1;
            this.RaisePropertyChanged(nameof(MemoryOptions)); Memory = MemoryOptions[0];
            Result = $"{Strings.Tool_version}: {Convert.ToHexString(version)}";
            return;
        }
        if (action == "read-block") { Data = Convert.ToHexString(await gen.ReadBlockAsync(Block, ct)); return; }
        if (action is "read-memory" or "write-memory")
        {
            var memory = Memory ?? throw new InvalidOperationException(Strings.Tool_read_configuration_first);
            var address = memory.Name == "PACK" ? PackPage : memory.Address;
            if (address < 0) throw new InvalidOperationException(Strings.Tool_gen4_pack_page_required);
            if (action == "read-memory") MemoryData = Convert.ToHexString(await gen.ReadMemoryAsync(address, memory.Length, memory.Stride, ct));
            else
            {
                RequireWrite();
                var data = ParseHex(MemoryData, memory.Length);
                await gen.WriteMemoryAsync(address, data, memory.Stride, ct);
                Verify(data, await gen.ReadMemoryAsync(address, memory.Length, memory.Stride, ct));
            }
            return;
        }
        RequireWrite();
        if (action == "write-block")
        {
            var data = ParseHex(Data, 16);
            await gen.WriteBlockAsync(Block, data, ct);
            Verify(data, await gen.ReadBlockAsync(Block, ct));
            return;
        }
        var configuration = _configuration ?? throw new InvalidOperationException(Strings.Tool_read_configuration_first);
        if (action == "write-uid")
        {
            await gen.WriteUidAsync(configuration, ParseHex(Uid, configuration.UidLength), ct);
            var tags = await client.Hf14aScanAsync(ct);
            if (tags.Count != 1) throw new InvalidOperationException(Strings.Tool_verification_failed);
            Verify(ParseHex(Uid), tags[0].Uid);
            return;
        }
        if (action is not ("apply" or "permanent")) throw new ArgumentException(nameof(action));
        if (action == "permanent" && PermanentConfirmation != "LOCK") throw new InvalidOperationException(Strings.Tool_confirm_lock);
        var bytes = configuration.Bytes.ToArray();
        var ats = string.IsNullOrWhiteSpace(Ats) ? Array.Empty<byte>() : ParseHex(Ats);
        if (ats.Length > 16 || Gen4Protocol is < 0 or > 1 || UidSize is < 0 or > 2 || Shadow is < 0 or > 4
            || UltralightMode is < 0 or > 3 || Block0Mode is < 0 or > 2 || MaxBlock is < 0 or > 255)
            throw new FormatException(Strings.Tool_invalid_configuration);
        bytes[0] = (byte)Gen4Protocol; bytes[1] = (byte)UidSize; ParseHex(NewPassword, 4).CopyTo(bytes, 2);
        bytes[6] = (byte)Shadow; bytes[7] = (byte)ats.Length; ats.CopyTo(bytes, 8);
        var atqa = ParseHex(Atqa, 2); bytes[24] = atqa[1]; bytes[25] = atqa[0]; bytes[26] = ParseHex(Sak, 1)[0];
        bytes[27] = (byte)UltralightMode; bytes[28] = (byte)MaxBlock; bytes[29] = (byte)Block0Mode;
        var next = new Gen4Configuration(bytes);
        await gen.WriteConfigurationAsync(next, action == "permanent", ct);
        Password = NewPassword; // F0/F1 changes the password atomically, including when readback fails.
        _configuration = next;
        if (action != "permanent") Verify(next.Bytes, (await gen.ReadConfigurationAsync(ct)).Bytes);
        else Result = Strings.Tool_lock_sent;
    }

    private void Verify(byte[] expected, byte[] actual)
    {
        if (!expected.AsSpan().SequenceEqual(actual)) throw new InvalidOperationException(Strings.Tool_verification_failed);
        Result = Strings.Tool_operation_verified;
    }

    private async Task EnsureValueCard(ChameleonClient client, MifareDump dump, CancellationToken ct)
    {
        var tags = await client.Hf14aScanAsync(ct);
        if (tags.Count != 1 || !tags[0].Uid.AsSpan().SequenceEqual(dump.Uid))
            throw new InvalidOperationException(Strings.Recovery_TargetChanged);
    }

    private static (MfKeyType Type, byte[] Key) ValueKey(MifareDump dump, int block, int permission)
    {
        var sector = MifareClassic.SectorOfBlock(block);
        var trailer = dump.GetBlock(MifareClassic.SectorTrailerBlock(sector));
        var mask = trailer is null ? 0 : DeviceTools.AccessMask(trailer, block, permission);
        if ((mask & 1) != 0 && dump.KeyA[sector] is { Length: 6 } a) return ((MfKeyType)0x60, a);
        if ((mask & 2) != 0 && dump.KeyB[sector] is { Length: 6 } b) return ((MfKeyType)0x61, b);
        throw new InvalidOperationException(Strings.Tool_value_block_no_access);
    }

    private async Task RunValueAsync(ChameleonClient client, string action, CancellationToken ct)
    {
        var dump = Recovery.Target ?? throw new InvalidOperationException(Strings.Tool_scan_card_first);
        await EnsureValueCard(client, dump, ct);
        if (action == "refresh")
        {
            Blocks.Clear();
            for (var sector = 0; sector < dump.SectorCount; sector++)
            {
                var trailer = MifareClassic.SectorTrailerBlock(sector);
                foreach (var block in new[] { trailer }.Concat(Enumerable.Range(MifareClassic.SectorFirstBlock(sector), MifareClassic.BlocksInSector(sector) - 1)))
                {
                    ct.ThrowIfCancellationRequested();
                    dump.SetBlock(block, null);
                    foreach (var (type, key) in new[] { ((MfKeyType)0x60, dump.KeyA[sector]), ((MfKeyType)0x61, dump.KeyB[sector]) })
                    {
                        if (key is not { Length: 6 }) continue;
                        try { dump.SetBlock(block, await client.Mf1ReadBlockAsync(block, type, key, ct)); break; }
                        catch (ChameleonException) { }
                    }
                }
            }
            for (var i = 1; i < dump.BlockCount; i++)
                if (!MifareClassic.IsTrailer(i) && dump.GetBlock(i) is { } data)
                    Blocks.Add(new(i, Convert.ToHexString(data), DeviceTools.DecodeValue(data), DeviceTools.DecodeValue(data) is not null ? data[12] : null));
            Result = $"{Blocks.Count} / {dump.BlockCount}";
            return;
        }
        RequireWrite();
        var destination = action == "write" ? Block : Destination;
        if (Block <= 0 || Block >= dump.BlockCount || destination <= 0 || destination >= dump.BlockCount
            || MifareClassic.IsTrailer(Block) || MifareClassic.IsTrailer(destination))
            throw new InvalidOperationException(Strings.Tool_value_block_no_access);
        var source = ValueKey(dump, Block, action == "write" ? 1 : action == "increment" ? 2 : 3);
        byte[] expected;
        if (action == "write")
        {
            await RequireCapability(client, Command.Mf1WriteOneBlock, ct);
            expected = DeviceTools.EncodeValue(Amount, (byte)Block);
            dump.SetBlock(Block, null);
            if (!await client.Mf1WriteBlockAsync(Block, source.Type, source.Key, expected, ct))
                throw new InvalidOperationException(Strings.Tool_verification_failed);
        }
        else
        {
            await RequireCapability(client, Command.Mf1ManipulateValueBlock, ct);
            var readSource = ValueKey(dump, Block, 0);
            var original = await client.Mf1ReadBlockAsync(Block, readSource.Type, readSource.Key, ct);
            var value = DeviceTools.DecodeValue(original) ?? throw new InvalidOperationException(Strings.Tool_value_block_no_access);
            if (Amount < 0 && action != "transfer") throw new ArgumentOutOfRangeException(nameof(Amount));
            var target = ValueKey(dump, destination, 3);
            byte operation = action switch { "increment" => 0xC1, "decrement" => 0xC0, "transfer" => 0xC2, _ => throw new ArgumentException(nameof(action)) };
            var next = action == "transfer" ? value : checked(action == "increment" ? value + Amount : value - Amount);
            expected = DeviceTools.EncodeValue(next, original[12]);
            // A lost reply may mean the write succeeded: never retry a mutation.
            dump.SetBlock(destination, null);
            await client.Connection.SendExpectAsync(Command.Mf1ManipulateValueBlock, Status.HfTagOk,
                DeviceTools.ValuePayload(Block, source.Type, source.Key, operation, action == "transfer" ? 0 : Amount, destination, target.Type, target.Key), ct: ct);
        }
        var readKey = ValueKey(dump, destination, 0);
        var actual = await client.Mf1ReadBlockAsync(destination, readKey.Type, readKey.Key, ct);
        dump.SetBlock(destination, actual);
        Verify(expected, actual);
        var previous = Blocks.FirstOrDefault(b => b.Block == destination);
        if (previous is not null) Blocks.Remove(previous);
        Blocks.Add(new(destination, Convert.ToHexString(actual), DeviceTools.DecodeValue(actual), actual[12]));
    }
}

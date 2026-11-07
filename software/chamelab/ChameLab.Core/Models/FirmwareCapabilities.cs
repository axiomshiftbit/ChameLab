using System;
using System.Collections.Generic;
using System.Linq;
using ChameLab.Localization;
using ChameLab.Protocol;

namespace ChameLab.Models;

public enum FirmwareFeature
{
    HfScan, LfScan, ClassicRead, ClassicWrite, UltralightC, ClassicKeySearch, UltralightCKeySearch, SlotColors, LfCycle,
    BatteryCondition, LongPressThreshold, BlePairing, FirmwareUpdate,
}

public enum FeatureSupport { Unknown, Supported, Unsupported }

public sealed record FirmwareFeatureInfo(string Name, string Description);

/// <summary>One command-based description, independent of version strings and fork names.</summary>
public sealed class FirmwareCapabilities
{
    private readonly HashSet<ushort>? _commands;
    public FirmwareCapabilities(IEnumerable<ushort>? commands) => _commands = commands?.ToHashSet();
    public bool IsKnown => _commands is not null;
    public FeatureSupport Check(FirmwareFeature feature) => _commands is null ? FeatureSupport.Unknown
        : Required(feature).All(c => _commands.Contains((ushort)c)) ? FeatureSupport.Supported : FeatureSupport.Unsupported;
    public bool Supports(FirmwareFeature feature) => Check(feature) == FeatureSupport.Supported;
    // Older firmware may not implement capability reporting. Unknown is not a proven absence.
    public bool Allows(FirmwareFeature feature) => Check(feature) != FeatureSupport.Unsupported;
    public void Require(FirmwareFeature feature)
    {
        if (!Allows(feature)) throw new NotSupportedException(string.Format(Strings.Capabilities_Unavailable, Name(feature)));
    }
    public string Description(FirmwareFeature feature) => Check(feature) switch
    {
        FeatureSupport.Supported => Strings.Capabilities_Supported,
        FeatureSupport.Unsupported => Strings.Capabilities_Unsupported,
        _ => Strings.Capabilities_Unknown,
    };
    public FirmwareFeatureInfo[] Describe() => Enum.GetValues<FirmwareFeature>()
        .Select(f => new FirmwareFeatureInfo(Name(f), Description(f))).ToArray();

    public static string Name(FirmwareFeature feature) => feature switch
    {
        FirmwareFeature.HfScan => Strings.Capabilities_HfScan,
        FirmwareFeature.LfScan => Strings.Capabilities_LfScan,
        FirmwareFeature.ClassicRead => Strings.Capabilities_ClassicRead,
        FirmwareFeature.ClassicWrite => Strings.Capabilities_ClassicWrite,
        FirmwareFeature.UltralightC => "Ultralight C (3DES)",
        FirmwareFeature.ClassicKeySearch => Strings.Capabilities_ClassicKeySearch,
        FirmwareFeature.UltralightCKeySearch => Strings.Capabilities_UlcKeySearch,
        FirmwareFeature.SlotColors => Strings.Capabilities_SlotColors,
        FirmwareFeature.LfCycle => Strings.Capabilities_LfCycle,
        FirmwareFeature.BatteryCondition => Strings.Capabilities_BatteryCondition,
        FirmwareFeature.LongPressThreshold => Strings.Capabilities_LongPress,
        FirmwareFeature.BlePairing => Strings.Capabilities_BlePairing,
        FirmwareFeature.FirmwareUpdate => Strings.Capabilities_FirmwareUpdate,
        _ => throw new ArgumentOutOfRangeException(nameof(feature)),
    };

    internal static Command[] Required(FirmwareFeature feature) => feature switch
    {
        FirmwareFeature.HfScan => [Command.Hf14aScan, Command.ChangeDeviceMode],
        FirmwareFeature.LfScan => [Command.Em410xScan, Command.ChangeDeviceMode],
        FirmwareFeature.ClassicRead => [Command.Mf1AuthOneKeyBlock, Command.Mf1ReadOneBlock],
        FirmwareFeature.ClassicWrite => [Command.Mf1AuthOneKeyBlock, Command.Mf1WriteOneBlock],
        FirmwareFeature.UltralightC => [Command.Mf0UlcAuth, Command.Mf0UlcRead, Command.Mf0UlcWrite],
        FirmwareFeature.ClassicKeySearch => [Command.Hf14aScan, Command.Mf1DetectSupport, Command.Mf1AuthOneKeyBlock],
        FirmwareFeature.UltralightCKeySearch => [Command.Hf14aScan, Command.Hf14aRaw, Command.Mf0UlcAuth],
        FirmwareFeature.SlotColors => [Command.SetSlotColor, Command.GetAllSlotColors],
        FirmwareFeature.LfCycle => [Command.LfGetCycle, Command.LfSetCycle],
        FirmwareFeature.BatteryCondition => [Command.GetBatteryInfoEx],
        FirmwareFeature.LongPressThreshold => [Command.GetLongPressThreshold, Command.SetLongPressThreshold],
        FirmwareFeature.BlePairing => [Command.GetBlePairingEnable, Command.SetBlePairingEnable, Command.GetBlePairingKey, Command.SetBlePairingKey],
        FirmwareFeature.FirmwareUpdate => [Command.EnterBootloader],
        _ => throw new ArgumentOutOfRangeException(nameof(feature)),
    };
}

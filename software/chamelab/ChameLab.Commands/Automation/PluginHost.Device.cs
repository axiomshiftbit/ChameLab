using System.Buffers.Binary;
using System.Text;
using System.Text.Json.Nodes;
using ChameLab.Models;
using ChameLab.Protocol;
using ChameLab.Services;

namespace ChameLab.Automation;

// Adapters retain the ChameleonUltraGUI plugin API names and zero-based slot indices.
public sealed partial class PluginHost
{
    private static readonly Dictionary<string, IReadOnlyDictionary<string, int>> EnumValues = new()
    {
        ["TagType"] = new Dictionary<string, int> { ["unknown"] = 0, ["em410X"] = 100, ["em410X16"] = 101, ["em410X32"] = 102, ["em410X64"] = 103, ["em410XElectra"] = 104, ["pac"] = 150, ["viking"] = 170, ["jablotron"] = 180, ["hidProx"] = 200, ["ioProx"] = 201, ["idteck"] = 310, ["mifareMini"] = 1000, ["mifare1K"] = 1001, ["mifare2K"] = 1002, ["mifare4K"] = 1003, ["ntag210"] = 1107, ["ntag212"] = 1108, ["ntag213"] = 1100, ["ntag215"] = 1101, ["ntag216"] = 1102, ["ultralight"] = 1103, ["ultralightC"] = 1104, ["ultralight11"] = 1105, ["ultralight21"] = 1106, ["hf14a4"] = 3000 },
        ["TagFrequency"] = new Dictionary<string, int> { ["unknown"] = 0, ["lf"] = 1, ["hf"] = 2 },
        ["AnimationSetting"] = new Dictionary<string, int> { ["full"] = 0, ["minimal"] = 1, ["none"] = 2, ["symmetric"] = 3 },
        ["MifareWriteMode"] = new Dictionary<string, int> { ["normal"] = 0, ["denied"] = 1, ["deceive"] = 2, ["shadow"] = 3 },
        ["Mf1PrngType"] = new Dictionary<string, int> { ["static"] = 0, ["weak"] = 1, ["hard"] = 2 },
        ["ButtonType"] = new Dictionary<string, int> { ["a"] = 65, ["b"] = 66 },
        ["ButtonConfig"] = new Dictionary<string, int> { ["disable"] = 0, ["cycleForward"] = 1, ["cycleBackward"] = 2, ["cloneUID"] = 3, ["chargeStatus"] = 4, ["nfcFieldGenerator"] = 5 },
        ["ChameleonCommand"] = new Dictionary<string, int> { ["getAppVersion"] = 1000, ["changeDeviceMode"] = 1001, ["getDeviceMode"] = 1002, ["getGitVersion"] = 1017, ["getBatteryCharge"] = 1025, ["getBatteryChargeEx"] = 1043, ["setActiveSlot"] = 1003, ["setSlotTagType"] = 1004, ["setSlotDataDefault"] = 1005, ["setSlotEnable"] = 1006, ["setSlotTagNick"] = 1007, ["getSlotTagNick"] = 1008, ["saveSlotNicks"] = 1009, ["getActiveSlot"] = 1018, ["getSlotInfo"] = 1019, ["getEnabledSlots"] = 1023, ["deleteSlotInfo"] = 1024, ["deleteSlotTagNick"] = 1021, ["getAllSlotNicks"] = 1038, ["enterBootloader"] = 1010, ["getDeviceChipID"] = 1011, ["getDeviceBLEAddress"] = 1012, ["saveSettings"] = 1013, ["resetSettings"] = 1014, ["setAnimationMode"] = 1015, ["getAnimationMode"] = 1016, ["factoryReset"] = 1020, ["getDeviceType"] = 1033, ["getDeviceSettings"] = 1034, ["getDeviceCapabilities"] = 1035, ["getSleepTimeout"] = 1039, ["setSleepTimeout"] = 1040, ["getLongPressThreshold"] = 1041, ["setLongPressThreshold"] = 1042, ["getButtonPressConfig"] = 1026, ["setButtonPressConfig"] = 1027, ["getLongButtonPressConfig"] = 1028, ["setLongButtonPressConfig"] = 1029, ["bleSetConnectKey"] = 1030, ["bleGetConnectKey"] = 1031, ["bleClearBondedDevices"] = 1032, ["bleGetPairEnable"] = 1036, ["bleSetPairEnable"] = 1037, ["scan14ATag"] = 2000, ["mf1SupportDetect"] = 2001, ["mf1NTLevelDetect"] = 2002, ["mf1StaticNestedAcquire"] = 2003, ["mf1DarksideAcquire"] = 2004, ["mf1NTDistanceDetect"] = 2005, ["mf1NestedAcquire"] = 2006, ["mf1CheckKey"] = 2007, ["mf1ReadBlock"] = 2008, ["mf1WriteBlock"] = 2009, ["mf1ManipulateValueBlock"] = 2011, ["mf1CheckKeysOfSectors"] = 2012, ["mf1HardNestedAcquire"] = 2013, ["mf1StaticEncryptedNestedAcquire"] = 2014, ["mf1CheckKeysOnBlock"] = 2015, ["hf14ARawCommand"] = 2010, ["hf14aScanKeep"] = 2016, ["hf14aAuthTrace"] = 2017, ["mf0UlcAuth"] = 2018, ["mf0UlcRead"] = 2019, ["mf0UlcWrite"] = 2021, ["mf0UlcSetKey"] = 2022, ["hf14aSniff"] = 2020, ["hf14aSetFieldOn"] = 2100, ["hf14aSetFieldOff"] = 2101, ["hf14aGetConfig"] = 2200, ["hf14aSetConfig"] = 2201, ["hf14a4ApduRecv"] = 6000, ["hf14a4ApduSend"] = 6001, ["hf14a4SetAntiColl"] = 6002, ["hf14a4StaticResp"] = 6003, ["hf14a4ReaderApdu"] = 6004, ["hf14a4EmvScan"] = 6005, ["scanEM410Xtag"] = 3000, ["writeEM410XtoT5577"] = 3001, ["writeEM410XElectraToT5577"] = 3006, ["scanHIDProxTag"] = 3002, ["writeHIDProxToT5577"] = 3003, ["scanVikingTag"] = 3004, ["writeVikingToT5577"] = 3005, ["scanIoProxTag"] = 3010, ["writeIoProxToT5577"] = 3011, ["scanPacTag"] = 3014, ["writePacToT5577"] = 3015, ["writeIdteckToT5577"] = 3018, ["ioProxDecodeRaw"] = 3012, ["ioProxComposeID"] = 3013, ["adcGenericRead"] = 3009, ["writeT55XX"] = 3016, ["scanEM4X05Tag"] = 3030, ["scanJablotronTag"] = 3019, ["writeJablotronToT5577"] = 3020, ["lfSniff"] = 3031, ["mf1LoadBlockData"] = 4000, ["mf1SetAntiCollision"] = 4001, ["mf1SetDetectionEnable"] = 4004, ["mf1GetDetectionCount"] = 4005, ["mf1GetDetectionResult"] = 4006, ["mf1GetDetectionStatus"] = 4007, ["mf1GetEmulatorConfig"] = 4009, ["mf1GetGen1aMode"] = 4010, ["mf1SetGen1aMode"] = 4011, ["mf1GetGen2Mode"] = 4012, ["mf1SetGen2Mode"] = 4013, ["mf1GetFirstBlockColl"] = 4014, ["mf1SetFirstBlockColl"] = 4015, ["mf1GetWriteMode"] = 4016, ["mf1SetWriteMode"] = 4017, ["mf0NtagGetUidMagicMode"] = 4019, ["mf0NtagSetUidMagicMode"] = 4020, ["mf0NtagReadEmuPageData"] = 4021, ["mf0NtagWriteEmuPageData"] = 4022, ["mf0NtagGetVersionData"] = 4023, ["mf0NtagSetVersionData"] = 4024, ["mf0NtagGetSignatureData"] = 4025, ["mf0NtagSetSignatureData"] = 4026, ["mf0NtagGetCounterData"] = 4027, ["mf0NtagSetCounterData"] = 4028, ["mf0NtagResetAuthCount"] = 4029, ["mf0NtagGetPageCount"] = 4030, ["mf0NtagGetWriteMode"] = 4031, ["mf0NtagSetWriteMode"] = 4032, ["mf0NtagSetDetectionEnable"] = 4033, ["mf0NtagGetDetectionCount"] = 4034, ["mf0NtagGetDetectionLog"] = 4035, ["mf0NtagGetDetectionEnable"] = 4036, ["mf0NtagGetEmulatorConfig"] = 4037, ["mf1GetPrngType"] = 4040, ["mf1SetPrngType"] = 4041, ["mf1SetFieldOffDoReset"] = 4038, ["mf1GetFieldOffDoReset"] = 4039, ["mf1GetBlockData"] = 4008, ["mf1GetAntiCollData"] = 4018, ["setEM410XemulatorID"] = 5000, ["getEM410XemulatorID"] = 5001, ["setHIDProxEmulatorID"] = 5002, ["getHIDProxEmulatorID"] = 5003, ["setVikingEmulatorID"] = 5004, ["getVikingEmulatorID"] = 5005, ["setPacEmulatorID"] = 5006, ["getPacEmulatorID"] = 5007, ["setIoProxEmulatorID"] = 5008, ["getIoProxEmulatorID"] = 5009, ["setJablotronEmulatorID"] = 5010, ["getJablotronEmulatorID"] = 5011, ["setIdteckEmulatorID"] = 5012, ["getIdteckEmulatorID"] = 5013 },
    };
    private static int EnumValue(string type, JsonNode? value) => EnumValues[type].TryGetValue(Text(value), out var n) ? n : throw new FormatException("Unknown " + type + ": " + Text(value));
    private static string EnumName(string type, int value) => EnumValues[type].FirstOrDefault(p => p.Value == value).Key ?? throw new FormatException("Unknown " + type + " value.");
    private static TagSpecificType Tag(JsonNode? n) => (TagSpecificType)EnumValue("TagType", n);
    private static TagSenseType Sense(JsonNode? n) => (TagSenseType)EnumValue("TagFrequency", n);
    private static MfKeyType KeyType(JsonNode? n) => Number(n) is 0x60 ? MfKeyType.A : Number(n) is 0x61 ? MfKeyType.B : throw new FormatException("Expected key type 0x60 or 0x61.");
    private static int Slot(JsonNode? n) => Number(n) is >= 0 and < 8 ? Number(n) + 1 : throw new FormatException("Slot must be 0–7.");
    private static byte Byte(JsonNode? n) => checked((byte)Number(n));
    private static JsonObject Enums()
    {
        var result = new JsonObject();
        foreach (var (name, values) in EnumValues)
        {
            if (name == "ChameleonCommand") { var commands = new JsonObject(); foreach (var p in values) commands[p.Key] = p.Value; result["commands"] = commands; }
            else result[name] = new JsonArray(values.Keys.Select(n => (JsonNode)n).ToArray());
        }
        result["NTLevel"] = new JsonArray("static", "weak", "hard", "backdoor", "unknown");
        result["DarksideResult"] = new JsonArray("vulnerable", "fixed", "cantFixNT", "luckAuthOK", "notSendingNACK", "tagChanged");
        return result;
    }
    private static JsonObject TagNode(Hf14aTag tag) => new() { ["uid"] = BytesNode(tag.Uid), ["sak"] = (int)tag.Sak, ["atqa"] = BytesNode(tag.Atqa), ["ats"] = BytesNode(tag.Ats) };
    private async Task<byte[]> Wire(Command command, byte[]? data, CancellationToken ct, int timeout = 5000)
    {
        var response = await DeviceTools.RawAsync(Client, (int)command, data ?? [], timeout, ct);
        if (!response.Status.IsOk()) throw new ChameleonException(response.Status.Describe(), response.Status);
        return response.Data;
    }
    private async Task<JsonNode?> DeviceAsync(string method, JsonArray a, CancellationToken ct)
    {
        var c = Client;
        switch (method)
        {
            case "device.readEM410X":
                try { var lf = await c.LfScanAsync(TagSpecificType.Em410x, ct); return new JsonObject { ["uid"] = BytesNode(lf.Id), ["type"] = EnumName("TagType", (int)lf.Type) }; }
                catch (ChameleonException ex) when (ex.Status == Status.LfTagNoFound) { return null; }
            case "device.readHIDProx":
                try { var lf = await c.LfScanAsync(TagSpecificType.HidProx, ct); return new JsonObject { ["uid"] = BytesNode(lf.Id), ["type"] = EnumName("TagType", (int)lf.Type) }; }
                catch (ChameleonException ex) when (ex.Status == Status.LfTagNoFound) { return null; }
            case "device.readViking":
                try { var lf = await c.LfScanAsync(TagSpecificType.Viking, ct); return new JsonObject { ["uid"] = BytesNode(lf.Id), ["type"] = EnumName("TagType", (int)lf.Type) }; }
                catch (ChameleonException ex) when (ex.Status == Status.LfTagNoFound) { return null; }
            case "device.readPac":
                try { var lf = await c.LfScanAsync(TagSpecificType.Pac, ct); return new JsonObject { ["uid"] = BytesNode(lf.Id), ["type"] = EnumName("TagType", (int)lf.Type) }; }
                catch (ChameleonException ex) when (ex.Status == Status.LfTagNoFound) { return null; }
            case "device.readIoProx":
                try { var lf = await c.LfScanAsync(TagSpecificType.IoProx, ct); return new JsonObject { ["uid"] = BytesNode(lf.Id), ["type"] = EnumName("TagType", (int)lf.Type) }; }
                catch (ChameleonException ex) when (ex.Status == Status.LfTagNoFound) { return null; }
            case "device.readJablotron":
                try { var lf = await c.LfScanAsync(TagSpecificType.Jablotron, ct); return new JsonObject { ["uid"] = BytesNode(lf.Id), ["type"] = EnumName("TagType", (int)lf.Type) }; }
                catch (ChameleonException ex) when (ex.Status == Status.LfTagNoFound) { return null; }
            case "device.readIdteck":
                try { var lf = await c.LfScanAsync(TagSpecificType.Idteck, ct); return new JsonObject { ["uid"] = BytesNode(lf.Id), ["type"] = EnumName("TagType", (int)lf.Type) }; }
                catch (ChameleonException ex) when (ex.Status == Status.LfTagNoFound) { return null; }
            case "device.getDeviceChipID": return await c.GetChipIdAsync(ct);
            case "device.getDeviceBLEAddress": return await c.GetAddressAsync(ct);
            case "device.isReaderDeviceMode": return await c.GetDeviceModeAsync(ct) == DeviceMode.Reader;
            case "classic.detectMf1Support": return await c.Mf1IsSupportedAsync(ct);
            case "classic.getMf1NTLevel": return EnumName("Mf1PrngType", (int)await c.DetectNtLevelAsync(ct));
            case "classic.mf1Auth": return await c.Mf1AuthAsync(Number(a[0]), KeyType(a[1]), Bytes(a[2]), ct);
            case "classic.mf1AuthMultipleKeys": return (await c.CheckMifareKeysOnBlockAsync(Number(a[0]), KeyType(a[1]), Keys(a[2]), ct)) is { } key ? BytesNode(key) : null;
            case "classic.mf1ReadBlock": return BytesNode(await c.Mf1ReadBlockAsync(Number(a[0]), KeyType(a[1]), Bytes(a[2]), ct));
            case "classic.mf1WriteBlock": return await c.Mf1WriteBlockAsync(Number(a[0]), KeyType(a[1]), Bytes(a[2]), Bytes(a[3]), ct);
            case "classic.isMf1DetectionMode": return await c.GetMf1DetectionEnableAsync(ct);
            case "classic.getMf1DetectionCount": return await c.GetMf1DetectionCountAsync(ct);
            case "device.getAnimationMode": return EnumName("AnimationSetting", (int)await c.GetAnimationModeAsync(ct));
            case "device.getGitCommitHash": return await c.GetGitVersionAsync(ct);
            case "slots.getActiveSlot": return await c.GetActiveSlotAsync(ct) - 1;
            case "classic.isMf1Gen1aMode": return (await c.GetMf1EmulatorConfigAsync(ct)).Gen1a;
            case "classic.isMf1Gen2Mode": return (await c.GetMf1EmulatorConfigAsync(ct)).Gen2;
            case "classic.isMf1UseFirstBlockColl": return (await c.GetMf1EmulatorConfigAsync(ct)).BlockAntiColl;
            case "classic.getMf1WriteMode": return EnumName("MifareWriteMode", (int)(await c.GetMf1EmulatorConfigAsync(ct)).WriteMode);
            case "classic.getMf1PrngType": return EnumName("Mf1PrngType", (int)await c.GetMf1PrngTypeAsync(ct));
            case "device.getSleepTimeout": return await c.GetSleepTimeoutAsync(ct);
            case "device.getLongPressThreshold": return await c.GetLongPressThresholdAsync(ct);
            case "device.getBLEConnectionKey": return await c.GetBlePairingKeyAsync(ct);
            case "device.isBLEPairEnabled": return await c.GetBlePairingEnableAsync(ct);
            case "classic.mf1GetEmulatorBlock": return BytesNode(await c.Mf1ReadEmuBlockDataAsync(Number(a[0]), Number(a[1]), ct));
            case "classic.mf1GetAntiCollData": return TagNode(await c.GetHf14aEmulatorIdentityAsync(ct));
            case "device.getEM410XEmulatorID": return BytesNode(await c.GetLfEmulatorIdAsync(TagSpecificType.Em410x, ct));
            case "device.getDeviceCapabilities": return new JsonArray((await c.GetCapabilitiesAsync(ct)).Select(n => (JsonNode)(int)n).ToArray());
            case "classic.mf1GetFieldOffDoReset": return await c.GetMf1FieldOffDoResetAsync(ct);
            case "ultralight.mf0GetMagicMode": return await c.GetMf0UidMagicModeAsync(ct);
            case "ultralight.mf0NtagGetWriteMode": return EnumName("MifareWriteMode", (int)await c.GetMf0WriteModeAsync(ct));
            case "ultralight.mf0NtagGetDetectionEnable": return await c.GetMf0DetectionEnableAsync(ct);
            case "device.lfSniff": return BytesNode(await c.LfSniffAsync(a.Count == 0 ? 2000 : Number(a[0]), ct));
            case "device.hf14aSniff": return BytesNode(await c.Hf14aSniffAsync(a.Count == 0 ? 5000 : Number(a[0]), ct));
            case "device.getButtonConfig": return EnumName("ButtonConfig", (int)await c.GetButtonConfigAsync((ButtonType)EnumValue("ButtonType", a[0]), false, ct));
            case "device.getLongButtonConfig": return EnumName("ButtonConfig", (int)await c.GetButtonConfigAsync((ButtonType)EnumValue("ButtonType", a[0]), true, ct));
            case "device.setReaderDeviceMode": await c.SetDeviceModeAsync(Boolean(a[0]) ? DeviceMode.Reader : DeviceMode.Tag, ct); return null;
            case "slots.activateSlot": await c.SetActiveSlotAsync(Slot(a[0]), ct); return null;
            case "slots.setSlotType": await c.SetSlotTagTypeAsync(Slot(a[0]), Tag(a[1]), ct); return null;
            case "slots.setDefaultDataToSlot": await c.SetSlotDataDefaultAsync(Slot(a[0]), Tag(a[1]), ct); return null;
            case "slots.enableSlot": await c.SetSlotEnableAsync(Slot(a[0]), Sense(a[1]), Boolean(a[2]), ct); return null;
            case "slots.setSlotTagName": await c.SetSlotNickAsync(Slot(a[0]), Sense(a[2]), Text(a[1]), ct); return null;
            case "slots.deleteSlotInfo": await c.DeleteSlotSenseTypeAsync(Slot(a[0]), Sense(a[1]), ct); return null;
            case "slots.deleteSlotTagNick": await c.DeleteSlotNickAsync(Slot(a[0]), Sense(a[1]), ct); return null;
            case "slots.saveSlotData": await c.SaveSlotConfigAsync(ct); return null;
            case "slots.load": await c.LoadCardToSlotAsync(CardSave.FromJson(a[1]!), Slot(a[0]), ct: ct); return null;
            case "classic.setMf1DetectionStatus": await c.SetMf1DetectionEnableAsync(Boolean(a[0]), ct); return null;
            case "classic.setMf1BlockData": await c.Mf1WriteEmuBlockDataAsync(Number(a[0]), Bytes(a[1]), ct); return null;
            case "device.factoryReset": await c.WipeFdsAsync(ct); return null;
            case "device.saveSettings": await c.SaveSettingsAsync(ct); return null;
            case "device.resetSettings": await c.ResetSettingsAsync(ct); return null;
            case "device.setAnimationMode": await c.SetAnimationModeAsync((AnimationMode)EnumValue("AnimationSetting", a[0]), ct); return null;
            case "classic.setMf1Gen1aMode": await c.SetMf1Gen1aModeAsync(Boolean(a[0]), ct); return null;
            case "classic.setMf1Gen2Mode": await c.SetMf1Gen2ModeAsync(Boolean(a[0]), ct); return null;
            case "classic.setMf1UseFirstBlockColl": await c.SetMf1BlockAntiCollModeAsync(Boolean(a[0]), ct); return null;
            case "classic.setMf1WriteMode": await c.SetMf1WriteModeAsync((EmulatorWriteMode)EnumValue("MifareWriteMode", a[0]), ct); return null;
            case "classic.setMf1PrngType": await c.SetMf1PrngTypeAsync((MifareClassicPrngType)EnumValue("Mf1PrngType", a[0]), ct); return null;
            case "device.setSleepTimeout": await c.SetSleepTimeoutAsync(Number(a[0]), ct); return null;
            case "device.setLongPressThreshold": await c.SetLongPressThresholdAsync(Number(a[0]), ct); return null;
            case "device.clearBLEBoundedDevices": await c.DeleteAllBleBondsAsync(ct); return null;
            case "device.setBLEConnectKey": await c.SetBlePairingKeyAsync(Text(a[0]), ct); return null;
            case "device.setBLEPairEnabled": await c.SetBlePairingEnableAsync(Boolean(a[0]), ct); return null;
            case "classic.mf1SetFieldOffDoReset": await c.SetMf1FieldOffDoResetAsync(Boolean(a[0]), ct); return null;
            case "ultralight.mf0SetMagicMode": await c.SetMf0UidMagicModeAsync(Boolean(a[0]), ct); return null;
            case "ultralight.mf0EmulatorWritePages": await c.Mf0WriteEmuPagesAsync(Number(a[0]), Bytes(a[1]), ct); return null;
            case "ultralight.mf0EmulatorSetVersionData": await c.Mf0SetVersionDataAsync(Bytes(a[0]), ct); return null;
            case "ultralight.mf0EmulatorSetSignatureData": await c.Mf0SetSignatureDataAsync(Bytes(a[0]), ct); return null;
            case "ultralight.mf0EmulatorSetCounterData": await c.Mf0SetCounterAsync(Number(a[0]), Number(a[1]), Boolean(a[2]), ct); return null;
            case "ultralight.mf0NtagSetWriteMode": await c.SetMf0WriteModeAsync((EmulatorWriteMode)EnumValue("MifareWriteMode", a[0]), ct); return null;
            case "ultralight.mf0NtagSetDetectionEnable": await c.SetMf0DetectionEnableAsync(Boolean(a[0]), ct); return null;
            case "device.setButtonConfig": await c.SetButtonConfigAsync((ButtonType)EnumValue("ButtonType", a[0]), false, (ButtonPressFunction)EnumValue("ButtonConfig", a[1]), ct); return null;
            case "device.setLongButtonConfig": await c.SetButtonConfigAsync((ButtonType)EnumValue("ButtonType", a[0]), true, (ButtonPressFunction)EnumValue("ButtonConfig", a[1]), ct); return null;
            case "device.setEM410XEmulatorID": await c.SetLfEmulatorIdAsync(TagSpecificType.Em410x, Bytes(a[0]), ct); return null;
            case "device.writeEM410XtoT55XX": await c.WriteLfToT55xxAsync(TagSpecificType.Em410x, Bytes(a[0]), Bytes(a[1]), a[2]!.AsArray().Select(Bytes).ToArray(), ct); return null;
            case "device.setHIDProxEmulatorID": await c.SetLfEmulatorIdAsync(TagSpecificType.HidProx, Bytes(a[0]), ct); return null;
            case "device.writeHIDProxToT55XX": await c.WriteLfToT55xxAsync(TagSpecificType.HidProx, Bytes(a[0]), Bytes(a[1]), a[2]!.AsArray().Select(Bytes).ToArray(), ct); return null;
            case "device.setVikingEmulatorID": await c.SetLfEmulatorIdAsync(TagSpecificType.Viking, Bytes(a[0]), ct); return null;
            case "device.writeVikingToT55XX": await c.WriteLfToT55xxAsync(TagSpecificType.Viking, Bytes(a[0]), Bytes(a[1]), a[2]!.AsArray().Select(Bytes).ToArray(), ct); return null;
            case "device.setPacEmulatorID": await c.SetLfEmulatorIdAsync(TagSpecificType.Pac, Bytes(a[0]), ct); return null;
            case "device.writePacToT55XX": await c.WriteLfToT55xxAsync(TagSpecificType.Pac, Bytes(a[0]), Bytes(a[1]), a[2]!.AsArray().Select(Bytes).ToArray(), ct); return null;
            case "device.setIoProxEmulatorID": await c.SetLfEmulatorIdAsync(TagSpecificType.IoProx, Bytes(a[0]), ct); return null;
            case "device.writeIoProxToT55XX": await c.WriteLfToT55xxAsync(TagSpecificType.IoProx, Bytes(a[0]), Bytes(a[1]), a[2]!.AsArray().Select(Bytes).ToArray(), ct); return null;
            case "device.setJablotronEmulatorID": await c.SetLfEmulatorIdAsync(TagSpecificType.Jablotron, Bytes(a[0]), ct); return null;
            case "device.writeJablotronToT55XX": await c.WriteLfToT55xxAsync(TagSpecificType.Jablotron, Bytes(a[0]), Bytes(a[1]), a[2]!.AsArray().Select(Bytes).ToArray(), ct); return null;
            case "device.setIdteckEmulatorID": await c.SetLfEmulatorIdAsync(TagSpecificType.Idteck, Bytes(a[0]), ct); return null;
            case "device.writeIdteckToT55XX": await c.WriteLfToT55xxAsync(TagSpecificType.Idteck, Bytes(a[0]), Bytes(a[1]), a[2]!.AsArray().Select(Bytes).ToArray(), ct); return null;
            case "ultralight.mf0UlcReadPages": return BytesNode(await Wire(Command.Mf0UlcRead, [..Bytes(a[0]), Byte(a[1]), Byte(a[2])], ct));
            case "device.hf14aSetFieldOn": return BytesNode(await Wire(Command.Hf14aSetFieldOn, null, ct));
            case "device.hf14aSetFieldOff": return BytesNode(await Wire(Command.Hf14aSetFieldOff, null, ct));
            case "device.hf14aSetConfig": return BytesNode(await Wire(Command.Hf14aSetConfig, [Byte(a[0]), Byte(a[1]), Byte(a[2]), Byte(a[3])], ct));
            case "device.hf14a4ReaderApdu": return BytesNode(await Wire(Command.Hf14a4ReaderApdu, Bytes(a[0]), ct));
            case "device.hf14a4EmvScan": return BytesNode(await Wire(Command.Hf14a4EmvScan, null, ct));
            case "ultralight.mf0EmulatorReadPages": return BytesNode(await Wire(Command.Mf0NtagReadEmuPageData, [Byte(a[0]), Byte(a[1])], ct));
            case "ultralight.mf0EmulatorGetVersionData": return BytesNode(await Wire(Command.Mf0NtagGetVersionData, null, ct));
            case "ultralight.mf0EmulatorGetSignatureData": return BytesNode(await Wire(Command.Mf0NtagGetSignatureData, null, ct));
            case "device.hf14a4ClearStaticResponses": return BytesNode(await Wire(Command.Hf14a4StaticResp, [0], ct));
            case "device.enterDFUMode": c.EnterBootloader(); return null;
            case "device.raw":
                var raw = await DeviceTools.RawAsync(c, Number(a[0]), Bytes(a[1]), 10000, ct);
                return new JsonObject { ["command"] = raw.RawCommand, ["status"] = raw.RawStatus, ["data"] = BytesNode(raw.Data) };
            case "device.scan14443aTag":
                try { var tags = await c.Hf14aScanAsync(ct); return tags.Count == 0 ? null : TagNode(tags[0]); }
                catch (ChameleonException ex) when (ex.Status == Status.HfTagNo) { return null; }
            case "device.send14ARaw":
                var bytes = Bytes(a[0]); var options = a.Count > 1 ? Object(a[1]) : new JsonObject();
                bool Flag(string name, bool fallback) => options[name] is { } b ? Boolean(b) : fallback;
                var flags = (byte)((Flag("activateRfField", true) ? 128 : 0) | (Flag("waitResponse", true) ? 64 : 0) |
                    (Flag("appendCrc", true) ? 32 : 0) | (Flag("autoSelect", true) ? 16 : 0) |
                    (Flag("keepRfField", false) ? 8 : 0) | (Flag("checkResponseCrc", true) ? 4 : 0));
                var timeout = options["respTimeoutMs"] is { } time ? Number(time) : 100;
                return BytesNode(await Wire(Command.Hf14aRaw, DeviceTools.HfPayload(flags, bytes,
                    options["bitLen"] is { } bits ? Number(bits) : bytes.Length * 8, timeout), ct, timeout + 5000));
            default: return await ExtendedDeviceAsync(method, a, ct);
        }
    }
}

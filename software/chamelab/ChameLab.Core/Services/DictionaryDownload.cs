using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ChameLab.Models;
using ChameLab.Localization;

namespace ChameLab.Services;

public sealed record DictionarySource(string Name, string Url, int KeySize);

/// <summary>The same five public dictionaries offered by ChameleonUltraGUI.</summary>
public static class DictionaryDownload
{
    public static IReadOnlyList<DictionarySource> Sources { get; } = new DictionarySource[]
    {
        new("Proxmark3 (Mifare Classic)", "https://raw.githubusercontent.com/RfidResearchGroup/proxmark3/refs/heads/master/client/dictionaries/mfc_default_keys.dic", 6),
        new("Proxmark3 (T55XX)", "https://raw.githubusercontent.com/RfidResearchGroup/proxmark3/refs/heads/master/client/dictionaries/t55xx_default_pwds.dic", 4),
        new("Proxmark3 (Mifare Ultralight C)", "https://raw.githubusercontent.com/RfidResearchGroup/proxmark3/refs/heads/master/client/dictionaries/mfulc_default_keys.dic", 16),
        new("Proxmark3 (Mifare Plus)", "https://raw.githubusercontent.com/RfidResearchGroup/proxmark3/refs/heads/master/client/dictionaries/mfp_default_keys.dic", 16),
        new("Flipper Zero Unleashed Firmware (Mifare Classic)", "https://raw.githubusercontent.com/DarkFlippers/unleashed-firmware/refs/heads/dev/applications/main/nfc/resources/nfc/assets/mf_classic_dict.nfc", 6),
    };

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public static async Task<KeyDictionary> FetchAsync(DictionarySource source, CancellationToken ct = default)
    {
        var text = await Http.GetStringAsync(source.Url, ct);
        var dictionary = KeyDictionary.FromText(source.Name, text);
        if (dictionary.Count == 0 || dictionary.KeySize != source.KeySize)
            throw new FormatException(string.Format(Strings.Dictionaries_NoValidKeys, source.Name));
        return dictionary;
    }
}

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;
using ChameLab.Services;
using ChameLab.Models;
using ChameLab.ViewModels;
using ChameLab.Views;

namespace ChameLab.UiTesting;

// Compiled only into UI test projects/builds. All libraries are synthetic and in memory.
public static class LayoutScenarios
{
    public static readonly string[] Pages = ["connect", "settings", "device", "slots", "cards", "dictionaries", "dictionary-download", "dictionary-editor", "tools", "hf", "lf", "auth", "ValueBlocks", "T55Password", "T55Block", "Gen4", "RawHf", "RawDevice", "console", "plugins", "key-search", "writer", "writer-classic", "writer-ultralight", "writer-lf", "cards-add", "journal"];

    public static Border Create(string page, string language)
    {
        LocalizationService.Apply(language);
        LocalizationService.UseOnThisThread();
        var store = new MemoryKeyValueStore();
        var device = new DeviceService([]);
        var cards = new CardLibrary(store);
        var dictionaries = new DictionaryLibrary(store);
        foreach (var size in new[] { 4, 6, 16 })
            dictionaries.Add(KeyDictionary.FromText($"Proxmark3 — dictionary {size * 8} bit", new string('1', size * 2)));
        var firmware = new FirmwareUpdateViewModel(device, new FirmwareUpdateService(device, []));
        Control view = page switch
        {
            "connect" => new ConnectView { DataContext = new ConnectViewModel(device) },
            "settings" => new SettingsView { DataContext = new SettingsViewModel(cards, dictionaries, new AppUpdateViewModel()) },
            "device" => new ChameleonView { DataContext = new ChameleonViewModel(device, cards, firmware) },
            "slots" => new SlotsView { DataContext = new SlotsViewModel(device, cards) },
            "journal" => new JournalView { DataContext = new JournalViewModel(device.Journal) },
            "writer" or "writer-classic" or "writer-ultralight" or "writer-lf" => WriterScenario(page, device, cards, dictionaries),
            "cards-add" => AddScenario(device, cards, dictionaries),
            "cards" => new CardsView { DataContext = new CardsViewModel(device, cards, dictionaries) },
            "dictionaries" => new DictionariesView { DataContext = new DictionariesViewModel(dictionaries, cards) },
            "tools" => new ToolsView { DataContext = new ToolsViewModel(device, dictionaries, cards) },
            "dictionary-download" or "dictionary-editor" => DictionaryScenario(page, dictionaries, cards),
            _ => ToolScenario(page, device, dictionaries, cards),
        };
        var host = new Border { Child = view };
        host.SizeChanged += (_, _) => host.Classes.Set("compact", host.Bounds.Width < 760);
        return host;
    }

    private static Control AddScenario(DeviceService device, CardLibrary cards, DictionaryLibrary dictionaries)
    {
        var vm = new CardsViewModel(device, cards, dictionaries);
        vm.AddCommand.Execute(null);
        return new CardsView { DataContext = vm };
    }
    private static Control WriterScenario(string page, DeviceService device, CardLibrary cards, DictionaryLibrary dictionaries)
    {
        var vm = new WriterViewModel(device, cards, dictionaries);
        if (page != "writer")
        {
            var type = page == "writer-ultralight" ? ChameLab.Protocol.TagSpecificType.Ntag213
                : page == "writer-lf" ? ChameLab.Protocol.TagSpecificType.Em410x : ChameLab.Protocol.TagSpecificType.Mifare1024;
            var card = new CardSave { Name = "Test card with a long source name", Tag = type, Uid = type == ChameLab.Protocol.TagSpecificType.Em410x ? [1,2,3,4,5] : type == ChameLab.Protocol.TagSpecificType.Ntag213 ? [4,1,2,3,4,5,6] : [1,2,3,4], Sak = 8, Atqa = [4,0] };
            vm.Sources.Add(card); vm.Source = card;
        }
        return new WriterView { DataContext = vm };
    }

    private static Control DictionaryScenario(string page, DictionaryLibrary dictionaries, CardLibrary cards)
    {
        var vm = new DictionariesViewModel(dictionaries, cards);
        if (page == "dictionary-editor") vm.Selected = vm.Dictionaries.Last();
        var view = new DictionariesView { DataContext = vm };
        view.Loaded += (_, _) =>
        {
            foreach (var expander in view.GetVisualDescendants().OfType<Expander>()) expander.IsExpanded = true;
        };
        return view;
    }

    private static Control ToolScenario(string page, DeviceService device, DictionaryLibrary dictionaries, CardLibrary cards)
    {
        var vm = new ToolsViewModel(device, dictionaries, cards);
        vm.OpenToolCommand.Execute(page);
        var view = new ToolsView { DataContext = vm };
        view.Loaded += (_, _) =>
        {
            foreach (var expander in view.GetVisualDescendants().OfType<Expander>()) expander.IsExpanded = true;
        };
        return view;
    }

    public static void ScaleText(Control root, double scale)
    {
        if (scale == 1) return;
        // Snapshot first: changing an ancestor must not multiply inherited font sizes twice.
        var sizes = root.GetVisualDescendants().OfType<TextBlock>().Select(t => (Text: t, Size: t.FontSize)).ToArray();
        foreach (var (text, size) in sizes) text.FontSize = size * scale;
    }

    public static List<string> FindOverflow(Control root)
    {
        var errors = new List<string>();
        foreach (var text in root.GetVisualDescendants().OfType<TextBlock>())
        {
            if (!text.IsEffectivelyVisible || string.IsNullOrWhiteSpace(text.Text) || text.Bounds.Width <= 0)
                continue;
            // Ellipsis is intentional for user data (card names, addresses), never for setting labels.
            if (text.TextTrimming != TextTrimming.None || text.GetVisualAncestors().Any(v => v is ChameLab.Controls.Marquee))
                continue;
            if (text.Text.StartsWith("Not Found:", StringComparison.Ordinal)) errors.Add(text.Text);
            var layout = text.TextLayout;
            if (layout.Width > text.Bounds.Width + 2 || layout.Height > text.Bounds.Height + 2)
                errors.Add($"{text.Text}: text {layout.Width:F1}x{layout.Height:F1}, bounds {text.Bounds.Width:F1}x{text.Bounds.Height:F1}");
            var point = text.TranslatePoint(default, root);
            if (point is { } p && (p.X < -2 || p.X + text.Bounds.Width > root.Bounds.Width + 2))
                errors.Add($"{text.Text}: outside viewport at {p.X:F1}, width {text.Bounds.Width:F1} / {root.Bounds.Width:F1}");
        }
        return errors;
    }
}

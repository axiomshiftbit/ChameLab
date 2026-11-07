using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Media;
using Avalonia.VisualTree;
using ChameLab.Services;
using ChameLab.Models;
using ChameLab.ViewModels;
using ChameLab.Views;
using ChameLab.Transport;

namespace ChameLab.UiTesting;

// Compiled only into UI test projects/builds. All libraries are synthetic and in memory.
public static class LayoutScenarios
{
    public static readonly string[] Pages = ["connect", "connect-found", "settings", "device", "slots", "cards", "cards-library", "card-classic", "card-ultralight", "card-lf", "dictionaries", "dictionary-download", "dictionary-editor", "tools", "hf", "lf", "auth", "ValueBlocks", "T55Password", "T55Block", "Gen4", "RawHf", "RawDevice", "console", "plugins", "key-search", "writer", "writer-classic", "writer-ultralight", "writer-lf", "cards-add", "journal"];

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
            "connect-found" => ConnectScenario(),
            "settings" => new SettingsView { DataContext = new SettingsViewModel(cards, dictionaries, new AppUpdateViewModel()) },
            "device" => new ChameleonView { DataContext = new ChameleonViewModel(device, cards, firmware) },
            "slots" => new SlotsView { DataContext = new SlotsViewModel(device, cards) },
            "journal" => new JournalView { DataContext = new JournalViewModel(device.Journal) },
            "writer" or "writer-classic" or "writer-ultralight" or "writer-lf" => WriterScenario(page, device, cards, dictionaries),
            "cards-add" => AddScenario(device, cards, dictionaries),
            "cards" => new CardsView { DataContext = new CardsViewModel(device, cards, dictionaries) },
            "cards-library" or "card-classic" or "card-ultralight" or "card-lf" => SavedCardScenario(page, device, cards, dictionaries),
            "dictionaries" => new DictionariesView { DataContext = new DictionariesViewModel(dictionaries, cards) },
            "tools" => new ToolsView { DataContext = new ToolsViewModel(device, dictionaries, cards) },
            "dictionary-download" or "dictionary-editor" => DictionaryScenario(page, dictionaries, cards),
            _ => ToolScenario(page, device, dictionaries, cards),
        };
        var host = new Border { Child = view, Tag = device.Journal };
        host.SizeChanged += (_, _) => host.Classes.Set("compact", host.Bounds.Width < 760);
        return host;
    }

    private static Control ConnectScenario()
    {
        var vm = new ConnectViewModel(new DeviceService([new FixtureScanner()]));
        vm.ScanCommand.Execute(null);
        return new ConnectView { DataContext = vm };
    }

    private sealed class FixtureScanner : ITransportScanner
    {
        public TransportKind Kind => TransportKind.Serial;
        public string DisplayName => "UI fixture";
        public Task<IReadOnlyList<TransportCandidate>> ScanAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TransportCandidate>>([
                new(Kind, "test-a", "Chameleon Ultra", "USB — test device A"),
                new(Kind, "test-b", "Chameleon Ultra with a long device name", "USB — test device B")]);
        public ITransport Create(TransportCandidate candidate) => throw new InvalidOperationException("UI fixture cannot connect.");
    }

    private static Control AddScenario(DeviceService device, CardLibrary cards, DictionaryLibrary dictionaries)
    {
        var vm = new CardsViewModel(device, cards, dictionaries);
        vm.AddCommand.Execute(null);
        return new CardsView { DataContext = vm };
    }
    private static Control SavedCardScenario(string page, DeviceService device, CardLibrary cards, DictionaryLibrary dictionaries)
    {
        var type = page == "card-ultralight" ? Protocol.TagSpecificType.Ntag213
            : page == "card-lf" ? Protocol.TagSpecificType.Em410x : Protocol.TagSpecificType.Mifare1024;
        var card = new CardSave { Name = "Synthetic card for interface checks", Tag = type,
            Uid = page == "card-lf" ? [1, 2, 3, 4, 5] : page == "card-ultralight" ? [4, 1, 2, 3, 4, 5, 6] : [1, 2, 3, 4],
            Atqa = [4, 0], Sak = (byte)(page is "card-lf" or "card-ultralight" ? 0 : 8),
            Data = page == "card-lf" ? [] : Enumerable.Range(0, page == "card-ultralight" ? 45 : 64)
                .Select(_ => new byte[page == "card-ultralight" ? 4 : 16]).ToList() };
        cards.Add(card);
        var model = new CardsViewModel(device, cards, dictionaries);
        if (page != "cards-library") model.OpenCard(card.Id);
        return new CardsView { DataContext = model };
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
        foreach (var button in root.GetVisualDescendants().OfType<Button>())
            if (button.IsEffectivelyVisible && button.Command is null &&
                BindingOperations.GetBindingExpressionBase(button, Button.CommandProperty) is not null)
                errors.Add($"Button command binding is unresolved: {button.Name ?? button.GetType().Name}; " +
                    $"context={button.DataContext?.GetType().Name}, template={button.TemplatedParent?.GetType().Name}, " +
                    $"label={string.Join(" ", button.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text))}.");
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

    // Runs in headless tests and in the actual Android/Windows UI test builds.
    // Checking labels alone missed a journal whose toolbar consumed its entire viewport.
    public static async Task<List<string>> CheckJournalAsync(Border scene)
    {
        if (scene.Child is not (WriterView or JournalView)) return [];
        await Task.Delay(80);
        scene.UpdateLayout();
        var view = scene.GetVisualDescendants().OfType<JournalView>().FirstOrDefault(v => v.IsEffectivelyVisible);
        if (view is null) return ["Expanded journal is missing."];
        const string message = "UI journal live update";
        var pageScroll = scene.Child is WriterView
            ? scene.GetVisualDescendants().OfType<ScrollViewer>().First() : null;
        if (pageScroll is not null) pageScroll.Offset = default;
        for (var i = 0; i < 50; i++) ((DeviceJournal)scene.Tag!).Add("INFO", $"{message} {i}");
        await Task.Delay(350);
        scene.UpdateLayout();
        var text = view.GetVisualDescendants().OfType<TextBox>().Single();
        var errors = new List<string>();
        if (text.Bounds.Height < 160) errors.Add($"Journal viewport is too small: {text.Bounds.Height:F0}.");
        if (text.Text?.Contains(message) != true) errors.Add("New journal entry is not visible in the text control.");
        if (pageScroll is not null && pageScroll.Offset.Y > 1) errors.Add("Journal update moved the writer page unexpectedly.");
        return errors;
    }
}

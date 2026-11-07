using System.Text.Json.Nodes;
using ChameLab.Automation;
using ChameLab.Services;
using Xunit;

namespace ChameLab.Cli.Tests;

public class PluginFolderTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FolderRoundTripRenameMoveAndSubtreeDelete(bool card)
    {
        var store = new MemoryKeyValueStore(); var cards = new CardLibrary(store); var dictionaries = new DictionaryLibrary(store);
        var host = Host(cards, dictionaries); var folder = card ? "cardFolders" : "dictionaryFolders"; var records = card ? "cards" : "dictionaries";
        var parent = (string)(await host.InvokeAsync(folder + ".save", new JsonArray(new JsonObject { ["name"] = "Root" }), default))!;
        var child = (string)(await host.InvokeAsync(folder + ".save", new JsonArray(new JsonObject { ["name"] = "Child", ["parentId"] = parent }), default))!;
        var item = card ? new JsonObject { ["name"] = "Test", ["uid"] = "01020304", ["tag"] = 1001 } :
            new JsonObject { ["name"] = "Test", ["keys"] = new JsonArray(new JsonArray(0,0,0,0)) };
        item["folderId"] = child;
        var id = (string)(await host.InvokeAsync(records + ".save", new JsonArray(item), default))!;
        var bundle = (string)(await host.InvokeAsync(folder + ".export", new JsonArray(parent), default))!;
        await host.InvokeAsync(folder + ".save", new JsonArray(new JsonObject { ["id"] = parent, ["name"] = "Renamed" }), default);
        var read = await host.InvokeAsync(records + ".get", new JsonArray(id), default);
        Assert.Equal("Renamed / Child", (string?)read!["folder"]);
        var other = new MemoryKeyValueStore(); var otherCards = new CardLibrary(other); var otherDictionaries = new DictionaryLibrary(other);
        var otherHost = Host(otherCards, otherDictionaries);
        var importedId = (string)(await otherHost.InvokeAsync(folder + ".import", new JsonArray(bundle), default))!;
        Assert.NotEqual(parent, importedId);
        var copied = (await otherHost.InvokeAsync(records + ".list", [], default))!.AsArray();
        Assert.Single(copied); Assert.NotEqual(id, (string?)copied[0]!["id"]);
        // Backups retain the hierarchy, including empty parent folders.
        var backup = BackupService.Export(otherCards, otherDictionaries);
        var restored = new MemoryKeyValueStore(); var restoredCards = new CardLibrary(restored); var restoredDictionaries = new DictionaryLibrary(restored);
        BackupService.Import(backup, restoredCards, restoredDictionaries);
        Assert.Equal(2, (card ? restoredCards.FolderTree : restoredDictionaries.FolderTree).Items.Count);
        await otherHost.InvokeAsync(folder + ".delete", new JsonArray(importedId), default);
        Assert.Empty((await otherHost.InvokeAsync(records + ".list", [], default))!.AsArray());
        await host.InvokeAsync(records + ".move", new JsonArray(id), default);
        Assert.Null((await host.InvokeAsync(records + ".get", new JsonArray(id), default))!["folderId"]);
        await host.InvokeAsync(folder + ".delete", new JsonArray(parent), default);
        Assert.Single((await host.InvokeAsync(records + ".list", [], default))!.AsArray());
    }
    [Fact]
    public async Task InvalidHierarchyAndRecordDoNotPartiallyImport()
    {
        var store = new MemoryKeyValueStore(); var cards = new CardLibrary(store); var dictionaries = new DictionaryLibrary(store); var host = Host(cards, dictionaries);
        var folder = new JsonObject { ["id"] = "a", ["name"] = "A", ["parentId"] = "a" };
        await Assert.ThrowsAsync<FormatException>(() => host.InvokeAsync("cardFolders.save", new JsonArray(folder), default));
        Assert.Empty(cards.FolderTree.Items);
        var bundle = new JsonObject { ["format"] = "chameleon-ultra-gui-folder", ["version"] = 1, ["rootFolderId"] = "a",
            ["folders"] = new JsonArray(new JsonObject { ["id"] = "a", ["name"] = "A" }),
            ["cards"] = new JsonArray(new JsonObject { ["name"] = "Invalid", ["uid"] = "not hex", ["folderId"] = "a" }) };
        await Assert.ThrowsAnyAsync<Exception>(() => host.InvokeAsync("cardFolders.import", new JsonArray(bundle.ToJsonString()), default));
        Assert.Empty(cards.FolderTree.Items); Assert.Empty(cards.Cards);
    }
    private static PluginHost Host(CardLibrary cards, DictionaryLibrary dictionaries) =>
        new(() => null, cards, dictionaries, (_, _, _) => throw new Exception(), work => Task.FromResult(work()));
}

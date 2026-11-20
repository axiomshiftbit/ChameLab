using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ChameLab.Models;
using ChameLab.Services;
using Xunit;

namespace ChameLab.Tests;

public class FolderIdentityTests
{
    [Fact]
    public void RenameAndMoveRetainIdsColorsEmptyChildrenAndObjectReferences()
    {
        var store = new MemoryKeyValueStore();
        var cards = new CardLibrary(store);
        cards.ReplaceFolders([new("a", "Work", "#2196F3", null), new("b", "Doors", "#FF9800", "a"), new("c", "Empty", "#2196F3", "b")]);
        cards.Add(new CardSave { Name = "Synthetic", FolderId = "b", Folder = "Work / Doors" });
        var browser = new FolderBrowserLibrary(cards);
        Assert.Equal("Office", browser.RenameFolder("Work", "Office"));
        Assert.Equal("Office / Doors", cards.Cards[0].Folder);
        Assert.Equal("b", cards.Cards[0].FolderId);
        Assert.Equal("#FF9800", cards.FolderTree.Items.Single(f => f.Id == "b").Color);
        Assert.Null(browser.MoveFolder("Office", "Office / Doors"));
        Assert.Equal("Doors", browser.MoveFolder("Office / Doors", ""));
        Assert.Equal("Doors / Empty", cards.FolderTree.Paths["c"]);
        Assert.Equal("b", new CardLibrary(store).Cards[0].FolderId);
        browser.RemoveFolder("Doors / Empty");
        Assert.DoesNotContain(cards.FolderTree.Items, f => f.Id == "c");
        browser.RemoveFolder("Doors");
        Assert.Null(cards.Cards[0].FolderId);
        Assert.Equal("", cards.Cards[0].Folder);
    }

    [Fact]
    public void PathOnlyBackupCreatesEmptyTreesAndKeepsRepeatedImportStable()
    {
        var store = new MemoryKeyValueStore();
        var cards = new CardLibrary(store); var dictionaries = new DictionaryLibrary(store);
        var input = Encoding.UTF8.GetBytes("""
            {"format":"chamelab-backup","version":1,"cards":[],"dictionaries":[],
            "cardFolders":["Work/Doors","Work/Empty"],"dictionaryFolders":["Keys/Test"]}
            """);
        BackupService.Import(input, cards, dictionaries);
        var ids = cards.FolderTree.Items.Select(f => f.Id).Order().ToArray();
        Assert.Contains("Work / Empty", cards.FolderTree.Paths.Values);
        Assert.Contains("Keys / Test", dictionaries.FolderTree.Paths.Values);
        BackupService.Import(input, cards, dictionaries);
        Assert.Equal(ids, cards.FolderTree.Items.Select(f => f.Id).Order());
        var copy = new MemoryKeyValueStore();
        var otherCards = new CardLibrary(copy); var otherDictionaries = new DictionaryLibrary(copy);
        BackupService.Import(BackupService.Export(cards, dictionaries), otherCards, otherDictionaries);
        Assert.Equal(cards.FolderTree.Paths.Values.Order(), otherCards.FolderTree.Paths.Values.Order());
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(6)]
    public void FailedBackupWriteRestoresBothLibraries(int failAt)
    {
        var store = new FailingStore();
        var cards = new CardLibrary(store); var dictionaries = new DictionaryLibrary(store);
        cards.Add(new CardSave { Name = "Before" });
        dictionaries.Add(new KeyDictionary { Name = "Before", Keys = [new byte[6]] });
        var before = BackupService.Export(cards, dictionaries);
        var incoming = new MemoryKeyValueStore();
        var incomingCards = new CardLibrary(incoming); var incomingDictionaries = new DictionaryLibrary(incoming);
        new FolderBrowserLibrary(incomingCards).AddFolder("New / Empty");
        new FolderBrowserLibrary(incomingDictionaries).AddFolder("New / Keys");
        incomingCards.Add(new CardSave { Name = "After" });
        incomingDictionaries.Add(new KeyDictionary { Name = "After", Keys = [new byte[4]], KeySize = 4 });
        incomingDictionaries.KnownKeys.RememberPosition([1, 2, 3, 4], 0, ChameLab.Protocol.MfKeyType.A, new byte[6]);
        store.FailAfter = failAt;
        Assert.Throws<IOException>(() => BackupService.Import(BackupService.Export(incomingCards, incomingDictionaries), cards, dictionaries));
        Assert.Equal(before, BackupService.Export(cards, dictionaries));
        Assert.Equal(before, BackupService.Export(new CardLibrary(store), new DictionaryLibrary(store)));
    }

    [Fact]
    public void InvalidSecondTreeAndOverlongPathsNeverWriteTheFirstTree()
    {
        var store = new FailingStore();
        var cards = new CardLibrary(store); var dictionaries = new DictionaryLibrary(store);
        var input = Encoding.UTF8.GetBytes("""
            {"format":"chamelab-backup","cardFolderTree":[{"id":"a","name":"Good"}],
            "dictionaryFolderTree":[{"id":"b","name":"Bad","parentId":"b"}]}
            """);
        Assert.Throws<FormatException>(() => BackupService.Import(input, cards, dictionaries));
        Assert.Equal(0, store.Writes);
        Assert.Throws<FormatException>(() => new FolderBrowserLibrary(cards).AddFolder(new string('x', 65)));
        Assert.Equal(0, store.Writes);
    }

    [Fact]
    public void LegacyFolderApiRetainsIdsAndNestedPathsWithoutTruncation()
    {
        var store = new MemoryKeyValueStore();
        var cards = new CardLibrary(store);
        var path = new string('a', 20) + " / " + new string('b', 20);
        Assert.True(cards.AddFolder(path));
        var card = new CardSave { Name = "Synthetic" }; cards.Add(card);
        cards.MoveToFolder(card, path);
        var id = card.FolderId;
        Assert.NotNull(id);
        Assert.Equal(path, new CardLibrary(store).Cards[0].Folder);
        Assert.True(cards.RenameFolder(new string('a', 20), "Renamed"));
        Assert.Equal(id, card.FolderId);
        Assert.Equal("Renamed / " + new string('b', 20), card.Folder);
        cards.RemoveFolder("Renamed");
        Assert.Null(card.FolderId);
        Assert.Equal("", card.Folder);
        Assert.Empty(cards.FolderTree.Items);
    }

    [Fact]
    public void OverlongRecordPathIsRejectedBeforeBackupWrites()
    {
        var store = new FailingStore();
        var cards = new CardLibrary(store); var dictionaries = new DictionaryLibrary(store);
        var source = new CardSave { Name = "Synthetic", Folder = new string('x', 65) };
        var node = new System.Text.Json.Nodes.JsonObject { ["format"] = BackupService.FormatTag,
            ["cards"] = new System.Text.Json.Nodes.JsonArray(source.ToJson()) };
        Assert.Throws<FormatException>(() => BackupService.Import(Encoding.UTF8.GetBytes(node.ToJsonString()), cards, dictionaries));
        Assert.Equal(0, store.Writes);
    }

    private sealed class FailingStore : IKeyValueStore
    {
        private readonly MemoryKeyValueStore _inner = new();
        public int Writes { get; private set; }
        public int FailAfter { get; set; } = -1;
        public string? Read(string key) => _inner.Read(key);
        public void Write(string key, string value)
        {
            Writes++;
            if (FailAfter > 0 && --FailAfter == 0) throw new IOException("Synthetic write failure");
            _inner.Write(key, value);
        }
    }
}

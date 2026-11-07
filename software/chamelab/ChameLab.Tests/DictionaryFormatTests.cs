using System;
using System.Linq;
using ChameLab.Models;
using ChameLab.Services;
using ChameLab.ViewModels;
using Xunit;

namespace ChameLab.Tests;

public class DictionaryFormatTests
{
    [Theory]
    [InlineData("Proxmark3 (Mifare Classic)", 6)]
    [InlineData("Proxmark3 (T55XX)", 4)]
    [InlineData("Proxmark3 (Mifare Ultralight C)", 16)]
    [InlineData("Proxmark3 (Mifare Plus)", 16)]
    [InlineData("Flipper Zero Unleashed Firmware", 6)]
    public void AllFiveFormatsSurviveImportEditCloneBackupAndExport(string name, int size)
    {
        var first = Enumerable.Range(0, size).Select(i => (byte)i).ToArray();
        var second = Enumerable.Repeat((byte)0x55, size).ToArray();
        var text = $"\uFEFF# public dictionary format fixture\r\n{Convert.ToHexString(first)} # comment\r\n{Convert.ToHexString(second)}\r\n{Convert.ToHexString(first)}\n";
        var dict = KeyDictionary.FromText(name, text);
        Assert.Equal(size, dict.KeySize);
        Assert.Equal(2, dict.Count);
        Assert.Equal(size * 2, (int)dict.ToJson()["keyLength"]!);
        var editor = new DictionaryEditorViewModel(dict.Clone());
        Assert.Equal(size, editor.Keys.RowLength);
        var edited = editor.Build();
        var store = new MemoryKeyValueStore();
        var library = new DictionaryLibrary(store);
        library.Add(edited);
        var restored = new DictionaryLibrary(store).Dictionaries.Single();
        var importedAgain = KeyDictionary.FromText(name, restored.ToText());
        Assert.Equal(first, importedAgain.Keys[0]);
        Assert.Equal(second, importedAgain.Keys[1]);
        var backup = BackupService.Export(new CardLibrary(store), library);
        var target = new DictionaryLibrary(new MemoryKeyValueStore());
        BackupService.Import(backup, new CardLibrary(new MemoryKeyValueStore()), target);
        Assert.Equal(size, target.Dictionaries.Single().KeySize);
        Assert.Equal(second, target.Dictionaries.Single().Keys[1]);
    }

    [Fact]
    public void ClassicRecoveryDoesNotReceivePasswordsOrAesKeys()
    {
        var library = new DictionaryLibrary(new MemoryKeyValueStore());
        foreach (var size in new[] { 4, 6, 16 })
            library.Add(KeyDictionary.FromText(size.ToString(), new string('1', size * 2)));
        Assert.Equal(6, Assert.Single(library.AllKeys()).Length);
        Assert.Equal(4, Assert.Single(library.AllKeys(4)).Length);
        Assert.Equal(16, Assert.Single(library.AllKeys(16)).Length);
        var vm = new CardsViewModel(new DeviceService([]), new CardLibrary(new MemoryKeyValueStore()), library);
        Assert.DoesNotContain(vm.DumpKeySources, s => s.Keys.Any(k => k.Length != 6));
    }

    [Fact]
    public void MixedLengthsAndMalformedJsonCannotSilentlyLoseOrTruncateKeys()
    {
        Assert.Throws<FormatException>(() => KeyDictionary.FromText("mixed", "01020304\n010203040506"));
        Assert.Throws<FormatException>(() => KeyDictionary.FromJson("{\"keyLength\":32,\"keys\":[[1,2,3,4,5,6]]}"));
        Assert.Throws<OverflowException>(() => KeyDictionary.FromJson("{\"keyLength\":8,\"keys\":[[256,2,3,4]]}"));
    }

    [Fact]
    public void LegacyClassicAndEmptyAesDictionaryKeepTheirFormat()
    {
        Assert.Equal(6, KeyDictionary.FromJson("{\"keys\":[[1,2,3,4,5,6]]}").KeySize);
        var empty = KeyDictionary.FromJson("{\"name\":\"AES\",\"keyLength\":32,\"keys\":[]}");
        Assert.Equal(16, new DictionaryEditorViewModel(empty).Build().KeySize);
    }
}

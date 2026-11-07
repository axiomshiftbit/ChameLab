using System.Collections.Generic;
using System.Threading.Tasks;
using ChameLab.Services;
using ChameLab.ViewModels;
using Xunit;

namespace ChameLab.Tests;

public class DictionaryDownloadTests
{
    public static IEnumerable<object[]> Sources()
    {
        for (var i = 0; i < DictionaryDownload.Sources.Count; i++) yield return [i];
    }

    // Public upstream data only; this also catches moved URLs or a changed dictionary format.
    [Theory]
    [Trait("Category", "Network")]
    [MemberData(nameof(Sources))]
    public async Task PublicDictionaryCanBeDownloadedEditedAndSaved(int index)
    {
        var source = DictionaryDownload.Sources[index];
        var dictionary = await DictionaryDownload.FetchAsync(source);
        Assert.NotEmpty(dictionary.Keys);
        Assert.All(dictionary.Keys, key => Assert.Equal(source.KeySize, key.Length));
        var edited = new DictionaryEditorViewModel(dictionary).Build();
        Assert.Equal(dictionary.Count, edited.Count);
        Assert.Equal(dictionary.ToText(), edited.ToText());
    }
}

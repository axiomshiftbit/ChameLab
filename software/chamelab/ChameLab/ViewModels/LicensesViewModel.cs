using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Input;
using Avalonia.Platform;
using ChameLab.Localization;
using ChameLab.Services;
using ReactiveUI;

namespace ChameLab.ViewModels;

public sealed record LicenseDocument(string Name, Uri Uri);
public sealed class LicensesViewModel : ViewModelBase
{
    public LicensesViewModel()
    {
        Documents = AssetLoader.GetAssets(new Uri("avares://ChameLab/Assets/Licenses/"), null)
            .Where(u => u.AbsolutePath.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
            .Select(u => new LicenseDocument(Path.GetFileNameWithoutExtension(u.AbsolutePath), u))
            .OrderBy(d => d.Name == "AUTHORS" ? 0 : 1).ThenBy(d => d.Name).ToArray();
        Selected = Documents.FirstOrDefault();
        OpenSourceCommand = ReactiveCommand.CreateFromTask(async () =>
        {
            if (StorageService.TopLevelAccessor?.Invoke()?.Launcher is { } launcher)
                await launcher.LaunchUriAsync(new Uri("https://github.com/axiomshiftbit/ChameLab"));
        });
    }
    public IReadOnlyList<LicenseDocument> Documents { get; }
    public LicenseDocument? Selected
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            if (value is null) { Text = ""; return; }
            using var reader = new StreamReader(AssetLoader.Open(value.Uri));
            Text = reader.ReadToEnd();
        }
    }
    public string Text { get; private set => this.RaiseAndSetIfChanged(ref field, value); } = "";
    public ICommand OpenSourceCommand { get; }
}

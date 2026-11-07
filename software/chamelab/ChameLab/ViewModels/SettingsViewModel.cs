using System;
using System.Globalization;
using System.Windows.Input;
using System.Linq;
using System.Collections.Generic;
using Avalonia.Threading;
using Avalonia;
using Avalonia.Styling;
using ChameLab.Services;
using ReactiveUI;
using ChameLab.Localization;

namespace ChameLab.ViewModels;

/// <summary>App-side settings: appearance, scan feedback, backup and about. Device settings live on the Chameleon page.</summary>
public sealed class SettingsViewModel : PageViewModelBase
{
    private readonly CardLibrary _cards;
    private readonly DictionaryLibrary _dictionaries;

    public SettingsViewModel(CardLibrary cards, DictionaryLibrary dictionaries, AppUpdateViewModel appUpdate)
        : base(() => Strings.Settings_Settings, "settings")
    {
        AppUpdate = appUpdate;
        _cards = cards;
        _dictionaries = dictionaries;

        ExportBackupCommand = ReactiveCommand.CreateFromTask(ExportBackupAsync);
        ImportBackupCommand = ReactiveCommand.CreateFromTask(ImportBackupAsync);
        OpenLinkCommand = ReactiveCommand.CreateFromTask<string>(OpenLinkAsync);
    }

    public bool CanUseFiles => StorageService.IsAvailable;

    public IReadOnlyList<AccentOption> Accents => AppearanceService.Accents;

    public IReadOnlyList<ThemeOption> Themes =>
    [
        new(ThemeVariant.Default, Strings.Settings_ThemeSystem),
        new(ThemeVariant.Light, Strings.Settings_ThemeLight),
        new(ThemeVariant.Dark, Strings.Settings_ThemeDark),
    ];

    public ThemeOption Theme
    {
        get => Themes.First(t => t.Variant == (Application.Current?.RequestedThemeVariant ?? ThemeVariant.Default));
        set
        {
            if (value is null || Application.Current is not { } app) return;
            app.RequestedThemeVariant = value.Variant;
            this.RaisePropertyChanged();
        }
    }

    /// <summary>App accent color; applied and remembered as soon as it changes.</summary>
    public AccentOption Accent
    {
        get;
        set
        {
            if (value is null || value == field) return;
            this.RaiseAndSetIfChanged(ref field, value);
            AppearanceService.Apply(value.Name);
        }
    } = AppearanceService.Accents.First(a => a.Name == AppearanceService.LoadAccent());

    public IReadOnlyList<LanguageOption> Languages => LocalizationService.Languages;

    private LanguageOption _language = LocalizationService.Languages.First(l => l.Code == LocalizationService.LoadLanguage());

    /// <summary>
    /// UI language; remembered and applied as soon as it changes. Applied once the picker has
    /// finished its selection, since applying rebuilds the views, the picker included.
    /// </summary>
    public LanguageOption Language
    {
        // The option list is rebuilt after a language change. Resolve the new entry
        // when read, without a static event retaining this page and its libraries.
        get => Languages.First(l => l.Code == _language.Code);
        set
        {
            if (value is null || value.Code == _language.Code) return;
            this.RaiseAndSetIfChanged(ref _language, value);
            var code = value.Code;
            Dispatcher.UIThread.Post(() => LocalizationService.SaveLanguage(code));
        }
    }

    /// <summary>Beep when a card scan or dump finishes; remembered as soon as it changes.</summary>
    public bool ScanSound
    {
        get => ScanFeedback.SoundEnabled;
        set
        {
            if (value == ScanFeedback.SoundEnabled) return;
            ScanFeedback.SoundEnabled = value;
            this.RaisePropertyChanged();
        }
    }

    /// <summary>Vibrate when a card scan or dump finishes (phones only).</summary>
    public bool ScanVibration
    {
        get => ScanFeedback.VibrationEnabled;
        set
        {
            if (value == ScanFeedback.VibrationEnabled) return;
            ScanFeedback.VibrationEnabled = value;
            this.RaisePropertyChanged();
        }
    }

    public bool CanVibrate => ScanFeedback.CanVibrate;

    public ICommand ExportBackupCommand { get; }
    public ICommand ImportBackupCommand { get; }

    private System.Threading.Tasks.Task ExportBackupAsync() => RunAsync(async () =>
    {
        var data = BackupService.Export(_cards, _dictionaries);
        var name = await StorageService.SaveAsync(Strings.Settings_ExportBackup, "chamelab-backup.json", data, "json");
        if (name is not null) Notify(string.Format(Strings.Settings_BackupSaved, name));
    });

    private System.Threading.Tasks.Task ImportBackupAsync() => RunAsync(async () =>
    {
        var picked = await StorageService.OpenAsync(Strings.Settings_ImportBackup, "json");
        if (picked is not { } file) return;
        var (c, d) = BackupService.Import(file.Data, _cards, _dictionaries);
        Notify(string.Format(Strings.Settings_ImportedBackup, Loc.Plural(Strings.Plural_Cards, c), Loc.Plural(Strings.Plural_Dictionaries, d)));
    });

    /// <summary>Application version, from the assembly's informational version.</summary>
    public LicensesViewModel Licenses { get; } = new();

    public string AppVersion => AppUpdateService.CurrentVersion;

    /// <summary>The app's own update check, shown in the About card.</summary>
    public AppUpdateViewModel AppUpdate { get; }

    public static string TrademarkNotice => Strings.Settings_TrademarkNotice;

    private static readonly SocialLink Discord = new("Discord", "https://discord.gg/UBSReGd4Gc", "discord", 1);

    private static readonly SocialLink[] RussianLinks =
    [
        new("Telegram", "https://t.me/chamelab", "telegram", 2),
        new("MAX", "https://max.ru/channel_ChameLab", "max", 0),
        Discord,
    ];

    public const string WebsiteUrl = "https://github.com/axiomshiftbit/ChameLab";

    /// <summary>
    /// Links for the About card: the website first, then the Russian-speaking channels only in
    /// the Russian UI and Discord in every language. Read afresh when the views are rebuilt for a
    /// new language (the website's name is localized).
    /// </summary>
    public IReadOnlyList<SocialLink> SocialLinks =>
    [
        new(Strings.Settings_Website, WebsiteUrl, "web", 3),
        .. CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ru" ? RussianLinks : [Discord],
    ];

    /// <summary>Opens a community link in the browser.</summary>
    public ICommand OpenLinkCommand { get; }

    private static async System.Threading.Tasks.Task OpenLinkAsync(string url)
    {
        if (StorageService.TopLevelAccessor?.Invoke()?.Launcher is { } launcher)
            await launcher.LaunchUriAsync(new Uri(url));
    }
}

/// <summary>
/// A link shown in the About card, with its <c>Icon.*</c> mark. Short of room, lower
/// <paramref name="Priority"/> drops its label first (see <see cref="Controls.ActionRow"/>).
/// </summary>
public sealed record SocialLink(string Name, string Url, string IconKey, int Priority);

public sealed record ThemeOption(ThemeVariant Variant, string Name);

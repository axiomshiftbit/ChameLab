using System;
using System.Threading.Tasks;
using System.Windows.Input;
using ChameLab.Models;
using ChameLab.Protocol;
using ChameLab.Services;
using ChameLab.Transport;
using ReactiveUI;
using ChameLab.Localization;

namespace ChameLab.ViewModels;

public enum FirmwareCheckState { None, Checking, UpToDate, Available, Failed }

public enum FirmwareDialog { None, Confirm, Recovery, Installing, Done, Failed }

/// <summary>
/// Firmware version check against the GitHub releases (on every connect) and the
/// install flow, whose dialog lives in the shell so it survives the device
/// disconnecting while it restarts into the bootloader.
/// </summary>
public sealed class FirmwareUpdateViewModel : ViewModelBase
{
    private static readonly TimeSpan CheckCacheLifetime = TimeSpan.FromMinutes(30);

    private readonly DeviceService _device;
    private readonly FirmwareUpdateService _service;
    private readonly DeviceBackupService? _backupService;

    private FirmwareCheckState _checkState;
    private FirmwareRelease? _latest;
    private DeviceModel _latestModel;
    private DateTime _checkedAt;

    private FirmwareDialog _dialog;

    // What the dialog installs: a release to download, or a package file picked by the user.
    private FirmwareRelease? _pendingRelease;
    private byte[]? _pendingFile;
    // Kept for Retry: a failed flash leaves the device waiting in its bootloader.
    private DfuPackage? _package;
    private FirmwareUpdateTarget? _target;
    private FirmwareVersion? _expectedVersion;
    // How the device was connected when the dialog opened; it is gone while the bootloader runs.
    private bool _overBle;

    public FirmwareUpdateViewModel(DeviceService device, FirmwareUpdateService service, DeviceBackupService? backupService = null)
    {
        _device = device;
        _service = service;
        _backupService = backupService;
        CheckCommand = ReactiveCommand.CreateFromTask(() => CheckAsync(force: true));
        InstallLatestCommand = ReactiveCommand.Create(ShowInstallLatest,
            this.WhenAnyValue(x => x.CanInstallLatest));
        InstallFromFileCommand = ReactiveCommand.CreateFromTask(InstallFromFileAsync);
        ConfirmCommand = ReactiveCommand.CreateFromTask(InstallAsync);
        RetryCommand = ReactiveCommand.CreateFromTask(InstallAsync);
        CloseCommand = ReactiveCommand.Create(Close);
        OpenReleaseCommand = ReactiveCommand.CreateFromTask(OpenReleaseAsync);
        OpenRecoveryCommand = ReactiveCommand.CreateFromTask(OpenRecoveryAsync);
        DiscardRecoveryCommand = ReactiveCommand.CreateFromTask(DiscardRecoveryAsync);
    }

    public ICommand CheckCommand { get; }
    public ICommand InstallLatestCommand { get; }
    public ICommand InstallFromFileCommand { get; }
    public ICommand ConfirmCommand { get; }
    public ICommand RetryCommand { get; }
    public ICommand CloseCommand { get; }
    public ICommand OpenReleaseCommand { get; }
    public ICommand OpenRecoveryCommand { get; }
    public ICommand DiscardRecoveryCommand { get; }
    public bool CanRecover => _service.CanRecover;
    public bool CanBackup => _backupService?.IsSupported == true;
    public DeviceBackupViewModel? Backups { get; init; }
    public bool BackupBeforeUpdate { get; set => this.RaiseAndSetIfChanged(ref field, value); } = true;
    public string BackupPassword { get; set => this.RaiseAndSetIfChanged(ref field, value); } = "";
    public string BackupPasswordRepeat { get; set => this.RaiseAndSetIfChanged(ref field, value); } = "";

    /// <summary>Installing needs the bootloader over USB serial (desktop) or BLE (phones).</summary>
    public bool CanInstall => _service.CanInstall(_device.Candidate?.Kind) &&
        _device.DeviceInfo?.Capabilities.Allows(FirmwareFeature.FirmwareUpdate) == true;

    public FirmwareCheckState CheckState
    {
        get => _checkState;
        private set
        {
            this.RaiseAndSetIfChanged(ref _checkState, value);
            this.RaisePropertyChanged(nameof(IsUpdateAvailable));
            this.RaisePropertyChanged(nameof(IsChecking));
            this.RaisePropertyChanged(nameof(CanInstallLatest));
        }
    }

    public bool IsChecking => _checkState == FirmwareCheckState.Checking;
    public bool IsUpdateAvailable => _checkState == FirmwareCheckState.Available;
    public bool CanInstallLatest => IsUpdateAvailable && CanInstall && _latest?.PackageUrl is not null;

    public string CheckMessage
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = string.Empty;

    public FirmwareRelease? Latest
    {
        get => _latest;
        private set => this.RaiseAndSetIfChanged(ref _latest, value);
    }

    public string CurrentVersion
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = "-";

    public FirmwareDialog Dialog
    {
        get => _dialog;
        private set
        {
            this.RaiseAndSetIfChanged(ref _dialog, value);
            this.RaisePropertyChanged(nameof(IsDialogOpen));
            this.RaisePropertyChanged(nameof(IsConfirming));
            this.RaisePropertyChanged(nameof(IsRecovery));
            this.RaisePropertyChanged(nameof(IsInstalling));
            this.RaisePropertyChanged(nameof(IsDone));
            this.RaisePropertyChanged(nameof(IsFailed));
            this.RaisePropertyChanged(nameof(CanRetry));
        }
    }

    /// <summary>A failed install can be retried; a file that isn't a package can't.</summary>
    public bool CanRetry => _package is not null || _pendingRelease is not null || _pendingFile is not null;

    public bool IsDialogOpen => _dialog != FirmwareDialog.None;
    public bool IsConfirming => _dialog == FirmwareDialog.Confirm;
    public bool IsRecovery => _dialog == FirmwareDialog.Recovery;
    public bool IsInstalling => _dialog == FirmwareDialog.Installing;
    public bool IsDone => _dialog == FirmwareDialog.Done;
    public bool IsFailed => _dialog == FirmwareDialog.Failed;

    public string InstallTitle
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = string.Empty;

    public string InstallHint => _overBle
        ? Strings.Firmware_HowBle
        : Strings.Firmware_HowUsb;

    public string KeepConnectedHint => _overBle
        ? Strings.Firmware_KeepClose
        : Strings.Firmware_DontUnplug;

    /// <summary>Release notes of the version about to be installed, as Markdown (empty for a file).</summary>
    public string InstallNotes => _pendingRelease?.Notes.Trim() ?? string.Empty;

    public string StepText
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    } = string.Empty;

    /// <summary>0..100 while the step reports progress.</summary>
    public double Progress
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public bool ProgressKnown
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public string? Error
    {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// <summary>Called on every connect: shows the device version and checks for a newer release.</summary>
    public Task OnConnectedAsync()
    {
        if (_device.DeviceInfo is not { } info) return Task.CompletedTask;
        CurrentVersion = FirmwareVersion.OfDevice(info.GitVersion, info.FirmwareVersion)?.ToString()
                         ?? info.FirmwareVersion;
        this.RaisePropertyChanged(nameof(CanInstall));
        this.RaisePropertyChanged(nameof(CanInstallLatest));
        return CheckAsync(force: false);
    }

    private async Task CheckAsync(bool force)
    {
        if (_device.DeviceInfo is not { } info || IsChecking) return;
        CheckState = FirmwareCheckState.Checking;
        CheckMessage = Strings.Update_Checking;
        try
        {
            // GitHub allows 60 anonymous API calls an hour; reconnects reuse a recent answer.
            if (force || _latest is null || _latestModel != info.Model || DateTime.UtcNow - _checkedAt > CheckCacheLifetime)
            {
                Latest = await _service.GetLatestAsync(info.Model);
                _latestModel = info.Model;
                _checkedAt = DateTime.UtcNow;
            }

            var current = FirmwareVersion.OfDevice(info.GitVersion, info.FirmwareVersion);
            if (current is { } c && !(_latest!.Version > c))
            {
                CheckState = FirmwareCheckState.UpToDate;
                CheckMessage = string.Format(Strings.Firmware_UpToDate, _latest.Version);
            }
            else
            {
                CheckState = FirmwareCheckState.Available;
                CheckMessage = _latest!.PackageUrl is null
                    ? string.Format(Strings.Firmware_NoPackage, _latest.Version)
                    : CanInstall
                        ? string.Format(Strings.Firmware_Available, _latest.Version)
                        : string.Format(Strings.Firmware_AvailableConnect, _latest.Version);
            }
        }
        catch (Exception ex)
        {
            CheckState = FirmwareCheckState.Failed;
            CheckMessage = string.Format(Strings.Update_CheckFailed, ex.Message);
        }
    }

    private void ShowInstallLatest()
    {
        if (!CanInstallLatest || _latest is null) return;
        _pendingRelease = _latest;
        _pendingFile = null;
        _package = null;
        _target = null;
        _expectedVersion = _latest.Version;
        InstallTitle = string.Format(Strings.Firmware_InstallPrompt, _latest.Version);
        SetTransport();
        Error = null;
        Dialog = FirmwareDialog.Confirm;
    }

    private async Task InstallFromFileAsync()
    {
        if (!CanInstall) return;
        PickedFile? picked;
        try { picked = await StorageService.OpenBoundedAsync(Strings.Firmware_PackageTitle, DfuPackage.MaxZipSize, "zip"); }
        catch (Exception ex) { Error = ex.Message; Dialog = FirmwareDialog.Failed; return; }
        if (picked is not { } file) return;

        try { _package = await Task.Run(() => DfuPackage.Parse(file.Data)); _package.RequireModel(_device.DeviceInfo!.Model); }
        catch (Exception ex)
        {
            _package = null;
            _pendingRelease = null;
            _pendingFile = null;
            Error = $"{file.Name}: {ex.Message}";
            InstallTitle = Strings.Firmware_CantInstallFile;
            Dialog = FirmwareDialog.Failed;
            return;
        }
        _pendingRelease = null;
        _pendingFile = file.Data;
        _target = null;
        _expectedVersion = null;
        InstallTitle = string.Format(Strings.Firmware_InstallFromPrompt, file.Name);
        SetTransport();
        Error = null;
        Dialog = FirmwareDialog.Confirm;
    }

    private async Task InstallAsync()
    {
        if (_target is null && CanBackup && BackupBeforeUpdate &&
            (!EncryptedDeviceBackup.IsValidPassword(BackupPassword) || BackupPassword != BackupPasswordRepeat))
        {
            Error = Strings.Backup_PasswordHint;
            Dialog = FirmwareDialog.Confirm;
            return;
        }
        Dialog = FirmwareDialog.Installing;
        Error = null;
        var name = _pendingRelease?.Version.ToString() ?? Strings.Firmware_TheFirmware;
        InstallTitle = string.Format(Strings.Firmware_Installing, name);
        try
        {
            if (_package is null)
            {
                SetStep(Strings.Firmware_Downloading, 0);
                var zip = _pendingFile ?? await _service.DownloadAsync(_pendingRelease!,
                    new Progress<double>(f => SetStep(Strings.Firmware_Downloading, f)));
                SetStep(Strings.Firmware_CheckingPackage);
                _package = await Task.Run(() => DfuPackage.Parse(zip));
            }
            // Prepared before the restart, so a retry still knows which bootloader is this device's.
            if (_target is null)
            {
                var target = _service.PrepareTarget();
                _package.RequireModel(target.Device.Model);
                // Preserve the pending device's backup before creating one for another device.
                await _service.CheckRecoveryTargetAsync(target.Device);
                if (CanBackup && BackupBeforeUpdate)
                {
                    SetStep(Strings.Backup_Creating);
                    await _backupService!.CreateAsync(BackupPassword, new Progress<int>(slot =>
                        SetStep(string.Format(Strings.Backup_Reading, slot), slot / 8.0)));
                    BackupPassword = ""; BackupPasswordRepeat = "";
                }
                _target = target;
            }

            await _service.InstallAsync(_package, _target, new Progress<FirmwareUpdateProgress>(p =>
            {
                switch (p.Step)
                {
                    case FirmwareUpdateStep.Checking: SetStep(Strings.Firmware_CheckingPackage); break;
                    case FirmwareUpdateStep.Restarting: SetStep(Strings.Firmware_Restarting); break;
                    case FirmwareUpdateStep.WaitingForBootloader: SetStep(Strings.Firmware_WaitingBootloader); break;
                    case FirmwareUpdateStep.Flashing: SetStep(string.Format(Strings.Firmware_Flashing, p.Fraction), p.Fraction); break;
                    case FirmwareUpdateStep.Reconnecting: SetStep(Strings.Firmware_WaitingRestart); break;
                }
            }), expectedVersion: _expectedVersion);

            _package = null;
            _target = null;
            InstallTitle = Strings.Firmware_Updated;
            StepText = _device.DeviceInfo is { } info
                ? string.Format(Strings.Firmware_NowRuns, info.ModelName, FirmwareVersion.OfDevice(info.GitVersion, info.FirmwareVersion)?.ToString() ?? info.FirmwareVersion)
                : Strings.Firmware_Restarted;
            Dialog = FirmwareDialog.Done;
        }
        catch (Exception ex)
        {
            InstallTitle = Strings.Firmware_Failed;
            Error = ex.Message + (_target is not null && _device.Client is null ? "\n\n" + Strings.Firmware_RecoveryHint : "");
            Dialog = FirmwareDialog.Failed;
        }
    }

    private void SetTransport()
    {
        _overBle = (_target?.Candidate ?? _device.Candidate)?.Kind == TransportKind.Ble;
        this.RaisePropertyChanged(nameof(InstallNotes));
        this.RaisePropertyChanged(nameof(InstallHint));
        this.RaisePropertyChanged(nameof(KeepConnectedHint));
    }

    private async Task OpenRecoveryAsync()
    {
        if (IsInstalling) return;
        _pendingRelease = null;
        _pendingFile = null;
        _package = null;
        _target = null;
        _expectedVersion = null;
        Error = null;
        InstallTitle = Strings.Firmware_RecoveryTitle;
        SetStep(Strings.Firmware_CheckingPackage);
        Dialog = FirmwareDialog.Installing;
        try
        {
            if (await _service.LoadRecoveryAsync() is { } saved)
            {
                _target = _service.PrepareRecovery(saved);
                _package = saved.Package;
                _expectedVersion = saved.ExpectedVersion;
                SetTransport();
                StepText = string.Format(Strings.Firmware_RecoveryReady, saved.Device.ModelName,
                    saved.ExpectedVersion?.ToString() ?? Strings.Firmware_TheFirmware);
            }
            else StepText = Strings.Firmware_RecoveryEmpty;
        }
        catch (Exception ex) { Error = ex.Message; StepText = Strings.Firmware_RecoveryEmpty; }
        Dialog = FirmwareDialog.Recovery;
    }

    private async Task DiscardRecoveryAsync()
    {
        if (IsInstalling) return;
        try
        {
            await _service.DiscardRecoveryAsync();
            _package = null;
            _target = null;
            Dialog = FirmwareDialog.None;
        }
        catch (Exception ex) { Error = ex.Message; }
    }

    private void SetStep(string text, double? fraction = null)
    {
        StepText = text;
        ProgressKnown = fraction is not null;
        Progress = (fraction ?? 0) * 100;
    }

    private void Close()
    {
        if (IsInstalling) return;
        BackupPassword = ""; BackupPasswordRepeat = "";
        Dialog = FirmwareDialog.None;
    }

    private async Task OpenReleaseAsync()
    {
        var url = _latest?.PageUrl ?? $"https://github.com/{FirmwareUpdateService.Repository}/releases";
        if (StorageService.TopLevelAccessor?.Invoke()?.Launcher is { } launcher)
            await launcher.LaunchUriAsync(new Uri(url));
    }
}

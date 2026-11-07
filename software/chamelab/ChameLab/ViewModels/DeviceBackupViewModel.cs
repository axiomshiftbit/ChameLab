using System;
using System.Threading.Tasks;
using System.Windows.Input;
using ChameLab.Localization;
using ChameLab.Services;
using ReactiveUI;

namespace ChameLab.ViewModels;

public sealed class DeviceBackupViewModel(DeviceBackupService service, DeviceService device) : ViewModelBase
{
    private byte[]? _encrypted;
    private DeviceBackupSnapshot? _snapshot;
    public bool IsSupported => service.IsSupported;
    public bool IsOpen { get; private set => this.RaiseAndSetIfChanged(ref field, value); }
    public bool IsWorking { get; private set => this.RaiseAndSetIfChanged(ref field, value); }
    public bool HasFile { get; private set => this.RaiseAndSetIfChanged(ref field, value); }
    public bool CanRestore { get; private set => this.RaiseAndSetIfChanged(ref field, value); }
    public string Password { get; set => this.RaiseAndSetIfChanged(ref field, value); } = "";
    public string Message { get; private set => this.RaiseAndSetIfChanged(ref field, value); } = "";
    public string? Error { get; private set => this.RaiseAndSetIfChanged(ref field, value); }

    public ICommand OpenCommand => field ??= ReactiveCommand.CreateFromTask(OpenAsync);
    public ICommand ImportCommand => field ??= ReactiveCommand.CreateFromTask(ImportAsync);
    public ICommand ExportCommand => field ??= ReactiveCommand.CreateFromTask(ExportAsync);
    public ICommand UnlockCommand => field ??= ReactiveCommand.CreateFromTask(UnlockAsync);
    public ICommand RestoreCommand => field ??= ReactiveCommand.CreateFromTask(RestoreAsync);
    public ICommand CloseCommand => field ??= ReactiveCommand.Create(Close);

    private async Task RunAsync(Func<Task> action)
    {
        if (IsWorking) return;
        IsWorking = true; Error = null;
        try { await action(); }
        catch (Exception ex) { Error = ex.Message; }
        finally { IsWorking = false; }
    }

    private Task OpenAsync() => RunAsync(async () =>
    {
        IsOpen = true; Password = ""; _snapshot = null; CanRestore = false;
        _encrypted = await service.ReadEncryptedAsync();
        HasFile = _encrypted is not null;
        Message = HasFile ? Strings.Backup_LocalAvailable : Strings.Backup_None;
    });

    private Task ImportAsync() => RunAsync(async () =>
    {
        var file = await StorageService.OpenBoundedAsync(Strings.Backup_Title, EncryptedDeviceBackup.MaxFileSize, "clbackup");
        if (file is null) return;
        _encrypted = file.Value.Data; HasFile = true; _snapshot = null; CanRestore = false; Password = "";
        Message = Strings.Backup_Imported;
    });

    private Task ExportAsync() => RunAsync(async () =>
    {
        if (_encrypted is null) return;
        if (await StorageService.SaveAsync(Strings.Backup_Title, "chamelab.clbackup", _encrypted, "clbackup") is not null)
            Message = Strings.Backup_Exported;
    });

    private Task UnlockAsync() => RunAsync(async () =>
    {
        _snapshot = null; CanRestore = false;
        if (_encrypted is null) return;
        var password = Password;
        Password = "";
        _snapshot = await Task.Run(() => EncryptedDeviceBackup.Decrypt(_encrypted, password));
        Message = string.Format(Strings.Backup_Preview, _snapshot.Device.ModelName, _snapshot.Created.ToLocalTime(), _snapshot.Slots.Length);
        CanRestore = device.DeviceInfo is { } info && info.ChipId == _snapshot.Device.ChipId && info.Model == _snapshot.Device.Model;
        if (!CanRestore) Message += "\n" + Strings.Backup_ConnectOriginal;
    });

    private Task RestoreAsync() => RunAsync(async () =>
    {
        if (_snapshot is null || !CanRestore) return;
        CanRestore = false;
        try
        {
            await service.RestoreAsync(_snapshot, new Progress<int>(slot => Message = string.Format(Strings.Backup_Restoring, slot)));
            _snapshot = null;
            Message = Strings.Backup_Restored;
        }
        catch
        {
            CanRestore = true;
            Message = Strings.Backup_RestoreInterrupted;
            throw;
        }
    });

    private void Close()
    {
        if (IsWorking) return;
        Password = ""; _snapshot = null; _encrypted = null; CanRestore = false; IsOpen = false;
    }
}

using System;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using ChameLab.Localization;
using ChameLab.Services;
using ReactiveUI;

namespace ChameLab.ViewModels;

public sealed class JournalViewModel : PageViewModelBase
{
    private readonly DeviceJournal _journal;
    private int _pending;
    private bool _visible;
    public JournalViewModel(DeviceJournal journal) : base(() => Strings.Nav_Journal, "info")
    {
        _journal = journal;
        ClearCommand = ReactiveCommand.Create(journal.Clear);
        ExportCommand = ReactiveCommand.CreateFromTask(ExportAsync);
        Refresh();
    }
    public string Text { get; private set => this.RaiseAndSetIfChanged(ref field, value); } = "";
    public bool Details
    {
        get;
        set { this.RaiseAndSetIfChanged(ref field, value); Refresh(); }
    }
    public ICommand ClearCommand { get; }
    public ICommand ExportCommand { get; }
    internal void SetVisible(bool visible)
    {
        if (_visible == visible) return;
        _visible = visible;
        if (visible) { _journal.Changed += QueueRefresh; Refresh(); }
        else _journal.Changed -= QueueRefresh;
    }
    private void QueueRefresh()
    {
        if (Interlocked.Exchange(ref _pending, 1) != 0) return;
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            Avalonia.Threading.DispatcherTimer.RunOnce(() =>
            {
                Interlocked.Exchange(ref _pending, 0);
                if (_visible) Refresh();
            }, TimeSpan.FromMilliseconds(200)));
    }
    private void Refresh()
    {
        var entries = _journal.Snapshot().Where(e => Details || e.Level is not ("TX" or "RX")).TakeLast(1500);
        Text = string.Join(Environment.NewLine, entries.Select(e => $"{e.Time:HH:mm:ss.fff} [{e.Level}] {e.Message}"));
    }
    private Task ExportAsync() => RunAsync(async () =>
        await StorageService.SaveAsync(Strings.Journal_Export, $"ChameLab-{DateTime.Now:yyyyMMdd-HHmmss}.log",
            Encoding.UTF8.GetBytes(_journal.Export()), "log"));
}

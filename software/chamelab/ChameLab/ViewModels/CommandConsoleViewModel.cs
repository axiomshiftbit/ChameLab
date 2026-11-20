using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Threading;
using ChameLab.Cli;
using ChameLab.Localization;
using ChameLab.Services;
using ReactiveUI;

namespace ChameLab.ViewModels;

public sealed class CommandConsoleViewModel : PageViewModelBase
{
    private readonly DeviceService _device;
    private readonly ConsoleWriter _writer = new();
    private readonly CliSession _session;
    private readonly DispatcherTimer _timer;
    private CancellationTokenSource? _cts;
    private Task? _running;
    private TaskCompletionSource<string?>? _input;
    private readonly string _directory;
    private readonly CommandHistory _history;
    private int _historyIndex;
    private string _draftCommand = "";
    public IReadOnlyList<string> Suggestions => CommandCatalog.Complete(CommandText).Take(12).ToArray();
    public bool WrapOutput { get; set => this.RaiseAndSetIfChanged(ref field, value); } = true;
    public Avalonia.Media.TextWrapping OutputWrapping => WrapOutput ? Avalonia.Media.TextWrapping.Wrap : Avalonia.Media.TextWrapping.NoWrap;
    public bool AutoScroll { get; set => this.RaiseAndSetIfChanged(ref field, value); } = true;
    public bool Stopping { get; private set => this.RaiseAndSetIfChanged(ref field, value); }
    public string StopLabel => Stopping ? Strings.Common_Stopping : Strings.Console_Stop;
    public IReadOnlyList<string> History => _history.Lines;
    public void HistoryStep(int direction)
    {
        if (Working) return;
        if (_historyIndex == _history.Lines.Count) _draftCommand = CommandText;
        _historyIndex = Math.Clamp(_historyIndex + direction, 0, _history.Lines.Count);
        CommandText = _historyIndex == _history.Lines.Count ? _draftCommand : _history.Lines[_historyIndex];
    }
    public void CompleteCommand()
    {
        if (Suggestions.FirstOrDefault() is { } match) CommandText = match + " ";
    }
    private void Stop()
    {
        if (_cts is null || Stopping) return;
        Stopping = true;
        this.RaisePropertyChanged(nameof(StopLabel));
        _cts.Cancel();
    }

    public CommandConsoleViewModel(DeviceService device) : base(() => Strings.Console_Title, "tools")
    {
        _device = device;
        _directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ChameLab", "command-workspace");
        _history = new CommandHistory(Path.Combine(_directory, "history.txt"));
        _historyIndex = _history.Lines.Count;
        _session = new CliSession(_writer, _writer)
        {
            WorkingDirectory = _directory,
            ClearOutput = _writer.Clear,
            ReadLineAsync = ReadLineAsync,
        };
        RunCommand = ReactiveCommand.CreateFromTask(ExecuteAsync);
        StopCommand = ReactiveCommand.Create(Stop);
        SendInputCommand = ReactiveCommand.Create(() => _input?.TrySetResult(Response));
        ImportCommand = ReactiveCommand.CreateFromTask(ImportAsync);
        ExportCommand = ReactiveCommand.CreateFromTask(ExportAsync);
        ClearCommand = ReactiveCommand.Create(() => { _writer.Clear(); Output = ""; });
        _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(150), DispatcherPriority.Background, (_, _) => Flush());
        this.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(WrapOutput)) this.RaisePropertyChanged(nameof(OutputWrapping)); };
        SelectedCommand = Commands.First(c => c.Name == "hw version");
    }

    public IReadOnlyList<CommandSpec> Commands { get; } = CommandCatalog.Commands
        .Where(c => c.Name is not ("hw connect" or "hw disconnect" or "exit")).ToArray();
    public CommandSpec? SelectedCommand
    {
        get;
        set
        {
            this.RaiseAndSetIfChanged(ref field, value);
            if (value is null) return;
            CommandText = value.Name;
            using var text = new StringWriter();
            CommandCatalog.Help(text, value);
            Help = text.ToString();
        }
    }
    public string CommandText
    {
        get;
        set { this.RaiseAndSetIfChanged(ref field, value); this.RaisePropertyChanged(nameof(Suggestions)); }
    } = "hw version";
    public string Help { get; private set => this.RaiseAndSetIfChanged(ref field, value); } = "";
    public string Output { get; private set => this.RaiseAndSetIfChanged(ref field, value); } = "";
    public bool Working { get; private set => this.RaiseAndSetIfChanged(ref field, value); }
    public bool WaitingForInput { get; private set => this.RaiseAndSetIfChanged(ref field, value); }
    public string Response { get; set => this.RaiseAndSetIfChanged(ref field, value); } = "";
    public ObservableCollection<string> Files { get; } = [];
    public string? SelectedFile { get; set => this.RaiseAndSetIfChanged(ref field, value); }
    public ICommand RunCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand SendInputCommand { get; }
    public ICommand ImportCommand { get; }
    public ICommand ExportCommand { get; }
    public ICommand ClearCommand { get; }

    private Task ExecuteAsync() => RunAsync(async () =>
    {
        if (Working) return;
        Directory.CreateDirectory(_directory);
        Working = true;
        _cts = new CancellationTokenSource();
        _session.BorrowConnection(_device.Client?.Connection);
        _timer.Start();
        var command = CommandText;
        _history.Add(command);
        _historyIndex = _history.Lines.Count;
        this.RaisePropertyChanged(nameof(History));
        try
        {
            _running = Task.Run(async () =>
            {
                var code = await _session.ExecuteAsync(command, _cts.Token);
                _writer.WriteLine($"\n[{code}]");
            });
            await _running;
        }
        finally
        {
            _timer.Stop();
            Flush();
            Working = false;
            Stopping = false;
            this.RaisePropertyChanged(nameof(StopLabel));
            WaitingForInput = false;
            _cts.Dispose();
            _cts = null;
            RefreshFiles();
        }
    });

    private async Task<string?> ReadLineAsync(CancellationToken ct)
    {
        var input = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        await Dispatcher.UIThread.InvokeAsync(() => { _input = input; Response = ""; WaitingForInput = true; });
        try { return await input.Task.WaitAsync(ct); }
        finally { await Dispatcher.UIThread.InvokeAsync(() => { WaitingForInput = false; _input = null; }); }
    }

    private void Flush() => Output = _writer.Snapshot();
    private void RefreshFiles()
    {
        Files.Clear();
        if (Directory.Exists(_directory))
            foreach (var file in Directory.EnumerateFiles(_directory).Order()) Files.Add(Path.GetFileName(file));
    }
    private Task ImportAsync() => RunAsync(async () =>
    {
        if (Working) return;
        var file = await StorageService.OpenAsync(Strings.Console_Import, "bin", "eml", "dic", "key", "json", "txt", "trace");
        if (file is null) return;
        Directory.CreateDirectory(_directory);
        var name = Path.GetFileName(file.Value.Name);
        var path = Path.Combine(_directory, name);
        for (var i = 1; File.Exists(path); i++)
            path = Path.Combine(_directory, Path.GetFileNameWithoutExtension(name) + $"-{i}" + Path.GetExtension(name));
        await File.WriteAllBytesAsync(path, file.Value.Data);
        RefreshFiles();
        SelectedFile = Path.GetFileName(path);
    });
    private Task ExportAsync() => RunAsync(async () =>
    {
        if (Working || SelectedFile is not { } name) return;
        var data = await File.ReadAllBytesAsync(Path.Combine(_directory, Path.GetFileName(name)));
        await StorageService.SaveAsync(Strings.Console_Export, name, data, Path.GetExtension(name).TrimStart('.'));
    });
    public override async Task OnDeactivatedAsync()
    {
        _cts?.Cancel();
        if (_running is not null) await _running;
        _timer.Stop();
    }

    private sealed class ConsoleWriter : TextWriter
    {
        private readonly StringBuilder _text = new();
        public override Encoding Encoding => Encoding.UTF8;
        public override void Write(char value) => Write(value.ToString());
        public override void Write(string? value)
        {
            lock (_text)
            {
                _text.Append(value);
                if (_text.Length > 100_000) _text.Remove(0, _text.Length - 100_000);
            }
        }
        public override void WriteLine(string? value) => Write(value + Environment.NewLine);
        public string Snapshot() { lock (_text) return _text.ToString(); }
        public void Clear() { lock (_text) _text.Clear(); }
    }
}

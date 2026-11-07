using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia;
using Avalonia.Styling;
using Avalonia.Threading;
using ChameLab.Automation;
using ChameLab.Localization;
using ChameLab.Services;
using ReactiveUI;

namespace ChameLab.ViewModels;

public sealed class PluginsViewModel : PageViewModelBase
{
    private readonly PluginLibrary _library;
    private readonly PluginHost _host;
    private readonly DeviceService _device;
    private CancellationTokenSource? _cts;
    private Task? _running;
    private TaskCompletionSource<JsonNode?>? _input;
    private string _inputType = "text";
    private bool _required;
    private string _id = Guid.NewGuid().ToString();
    private uint _color = 0xffff5722;

    public PluginsViewModel(DeviceService device, CardLibrary cards, DictionaryLibrary dictionaries)
        : base(() => Strings.Plugins_Title, "tools")
    {
        _device = device;
        _library = new PluginLibrary(AppServices.Store);
        _host = new PluginHost(() => device.Client, cards, dictionaries, GuiAsync,
            async work => await Dispatcher.UIThread.InvokeAsync(work));
        foreach (var plugin in _library.Items) Items.Add(plugin);
        if (_library.LoadError is { } error) ReportError(error);
        var idle = this.WhenAnyValue(x => x.Working, x => x.IsBusy, (work, busy) => !work && !busy);
        NewCommand = ReactiveCommand.Create(New, idle);
        SaveCommand = ReactiveCommand.CreateFromTask(() => RunSaveAsync(() => { Save(); return Task.CompletedTask; }), idle);
        DeleteCommand = ReactiveCommand.Create(() => { _library.Delete(_id); Refresh(); New(); }, idle);
        ImportCommand = ReactiveCommand.CreateFromTask(ImportAsync, idle);
        ExportCommand = ReactiveCommand.CreateFromTask(ExportAsync, idle);
        RunCommand = ReactiveCommand.CreateFromTask(ExecuteAsync, idle);
        StopCommand = ReactiveCommand.Create(() => _cts?.Cancel());
        AcceptCommand = ReactiveCommand.Create(Accept);
        CancelInputCommand = ReactiveCommand.Create(() => _input?.TrySetResult(null));
    }
    public ObservableCollection<DevicePlugin> Items { get; } = [];
    public DevicePlugin? Selected
    {
        get;
        set
        {
            if (Working) return;
            this.RaiseAndSetIfChanged(ref field, value);
            if (value is null) return;
            _id = value.Id; _color = value.Color; Name = value.Name; Description = value.Description; Code = value.Code;
            Resources = new JsonObject(value.Resources.Select(p => new KeyValuePair<string, JsonNode?>(p.Key, JsonValue.Create(p.Value)))).ToJsonString(new() { WriteIndented = true });
        }
    }
    public string Name { get; set => this.RaiseAndSetIfChanged(ref field, value); } = "";
    public string Description { get; set => this.RaiseAndSetIfChanged(ref field, value); } = "";
    public string Code { get; set => this.RaiseAndSetIfChanged(ref field, value); } = "chameleon.ui.info('ChameLab')";
    public string Resources { get; set => this.RaiseAndSetIfChanged(ref field, value); } = "{}";
    public bool Compress { get; set; }
    public bool Working { get; private set => this.RaiseAndSetIfChanged(ref field, value); }
    public string Output { get; private set => this.RaiseAndSetIfChanged(ref field, value); } = "";
    public bool WaitingForInput { get; private set => this.RaiseAndSetIfChanged(ref field, value); }
    public string Prompt { get; private set => this.RaiseAndSetIfChanged(ref field, value); } = "";
    public string PromptDescription { get; private set => this.RaiseAndSetIfChanged(ref field, value); } = "";
    public string InputError { get; private set => this.RaiseAndSetIfChanged(ref field, value); } = "";
    public string Response { get; set => this.RaiseAndSetIfChanged(ref field, value); } = "";
    public bool BooleanResponse { get; set => this.RaiseAndSetIfChanged(ref field, value); }
    public string? ChoiceResponse { get; set => this.RaiseAndSetIfChanged(ref field, value); }
    public IReadOnlyList<string> Choices { get; private set => this.RaiseAndSetIfChanged(ref field, value); } = [];
    public bool IsTextInput => _inputType is "text" or "number";
    public bool IsBooleanInput => _inputType == "boolean";
    public bool IsChoiceInput => _inputType == "choice";
    public char PasswordChar { get; private set => this.RaiseAndSetIfChanged(ref field, value); }
    public ICommand NewCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand DeleteCommand { get; }
    public ICommand ImportCommand { get; }
    public ICommand ExportCommand { get; }
    public ICommand RunCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand AcceptCommand { get; }
    public ICommand CancelInputCommand { get; }

    private DevicePlugin Draft()
    {
        var resources = JsonNode.Parse(Resources) as JsonObject ?? throw new FormatException(Strings.Plugins_ResourcesError);
        var plugin = new DevicePlugin(_id, Name.Trim(), Description, _color, Code,
            resources.ToDictionary(p => p.Key, p => p.Value?.GetValue<string>() ?? throw new FormatException(Strings.Plugins_ResourcesError)));
        _ = plugin.ToJson(); return plugin;
    }
    private void New() { Selected = null; _id = Guid.NewGuid().ToString(); Name = ""; Description = ""; Code = "chameleon.ui.info('ChameLab')"; Resources = "{}"; }
    private void Refresh() { Items.Clear(); foreach (var plugin in _library.Items) Items.Add(plugin); }
    private void Save() { var plugin = Draft(); _library.Save(plugin); Refresh(); Selected = plugin; }
    private Task ImportAsync() => RunAsync(async () =>
    {
        var file = await StorageService.OpenAsync(Strings.Common_Import, "json");
        if (file is null) return;
        var plugin = DevicePlugin.Parse(Encoding.UTF8.GetString(file.Value.Data), freshId: true);
        _library.Save(plugin); Refresh(); Selected = plugin;
    });
    private Task ExportAsync() => RunAsync(async () =>
    {
        var plugin = Draft();
        await StorageService.SaveAsync(Strings.Common_Export, "chamelab.plugin.json", Encoding.UTF8.GetBytes(plugin.ToJson(Compress).ToJsonString(new() { WriteIndented = true })), "json");
    });
    private Task ExecuteAsync() => RunAsync(async () =>
    {
        var plugin = Draft();
        using var cts = new CancellationTokenSource(); _cts = cts;
        Working = true; Output = "";
        _device.Journal.Add("INFO", "Plugin: execution started");
        try
        {
            _running = new PluginRuntime().RunAsync(plugin, _host, cts.Token);
            await _running;
            Append(Strings.Plugins_Completed);
            _device.Journal.Add("INFO", "Plugin: execution completed");
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        { Append(Strings.Plugins_Stopped); _device.Journal.Add("INFO", "Plugin: execution cancelled"); }
        catch (Exception ex)
        { Append(ex.Message); _device.Journal.Add("ERROR", "Plugin: " + ex.GetType().Name); }
        finally { Working = false; WaitingForInput = false; _cts = null; _input = null; }
    });
    private void Append(string value)
    {
        var text = Output + value + Environment.NewLine;
        Output = text.Length > 100_000 ? text[^100_000..] : text;
    }
    private async Task<JsonNode?> GuiAsync(string method, JsonArray a, CancellationToken ct)
    {
        if (method.StartsWith("settings.", StringComparison.Ordinal))
            return await Dispatcher.UIThread.InvokeAsync(() => PluginSettings.Invoke(method, a));
        if (method == "ui.snackbar") { await Dispatcher.UIThread.InvokeAsync(() => Append(PluginHostText(a[0]))); return null; }
        if (method is not ("ui.input" or "ui.info" or "ui.warning" or "ui.error")) throw new NotSupportedException(method);
        var completion = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var options = a.Count > 1 && a[1] is JsonObject o ? o : new JsonObject();
            _inputType = method == "ui.input" ? (string?)options["type"] ?? "text" : "message";
            if (_inputType is not ("text" or "number" or "choice" or "boolean" or "message")) throw new FormatException(Strings.Plugins_InputInvalid);
            Choices = options["choices"] is JsonArray choices ? choices.Select(PluginHostText).ToArray() : [];
            if (IsChoiceInput && Choices.Count == 0) throw new FormatException(Strings.Plugins_InputInvalid);
            _required = (bool?)options["required"] ?? false;
            Prompt = Name + ": " + PluginHostText(a[0]); PromptDescription = (string?)options["description"] ?? "";
            Response = options["default"]?.ToString() ?? "";
            BooleanResponse = IsBooleanInput && options["default"] is JsonValue b && b.TryGetValue<bool>(out var value) && value;
            ChoiceResponse = IsChoiceInput ? Choices.Contains(Response) ? Response : Choices[0] : null;
            PasswordChar = (bool?)options["secret"] == true ? '●' : '\0';
            InputError = ""; _input = completion; WaitingForInput = true;
            this.RaisePropertyChanged(nameof(IsTextInput)); this.RaisePropertyChanged(nameof(IsChoiceInput)); this.RaisePropertyChanged(nameof(IsBooleanInput));
        });
        try { return await completion.Task.WaitAsync(ct); }
        finally { await Dispatcher.UIThread.InvokeAsync(() => { WaitingForInput = false; _input = null; Response = ""; }); }
    }
    private static string PluginHostText(JsonNode? value) => value?.GetValue<string>() ?? "";
    private void Accept()
    {
        JsonNode? answer;
        if (_inputType == "number")
        {
            if (!double.TryParse(Response, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !double.IsFinite(value))
            { InputError = Strings.Plugins_InputInvalid; return; }
            answer = JsonValue.Create(value);
        }
        else if (IsBooleanInput) answer = JsonValue.Create(BooleanResponse);
        else if (IsChoiceInput) answer = JsonValue.Create(ChoiceResponse);
        else if (_inputType == "message") answer = null;
        else
        {
            if (_required && string.IsNullOrWhiteSpace(Response)) { InputError = Strings.Plugins_InputInvalid; return; }
            answer = JsonValue.Create(Response);
        }
        _input?.TrySetResult(answer);
    }
    public override async Task OnDeactivatedAsync()
    {
        _cts?.Cancel();
        if (_running is not null) try { await _running; } catch (OperationCanceledException) { } catch { /* reported by the run panel */ }
    }
}

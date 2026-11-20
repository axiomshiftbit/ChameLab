namespace ChameLab.Cli;

/// <summary>Commands typed before, shared by the terminal and the app's console: the last 1000, kept in a file.</summary>
public sealed class CommandHistory
{
    private const int Limit = 1000;
    private readonly string? _file;
    private readonly List<string> _lines = [];

    /// <param name="file">Where it is kept; null keeps it for this run only.</param>
    public CommandHistory(string? file = null)
    {
        _file = file;
        if (file is null) return;
        try { if (File.Exists(file)) _lines.AddRange(File.ReadLines(file).Where(IsSafe).TakeLast(Limit)); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>~/.chamelab_history.</summary>
    public static string DefaultFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".chamelab_history");

    public IReadOnlyList<string> Lines => _lines;

    // Persist only command paths and flags. Values may be keys, passwords, UIDs or private paths.
    public static bool IsSafe(string line)
    {
        if (line.Length is 0 or > 512) return false;
        try
        {
            var (spec, args) = CommandCatalog.Find(CommandLine.Split(line));
            return spec is not null && args.All(a => a is "--help" or "-h"
                || spec.Options.Any(o => o.IsFlag && o.Names.Contains(a)));
        }
        catch (FormatException) { return false; }
        catch (ArgumentException) { return false; }
    }

    public void Add(string line)
    {
        if (!IsSafe(line)) return;
        if (_lines.LastOrDefault() == line) return;
        _lines.Add(line);
        if (_lines.Count > Limit) _lines.RemoveRange(0, _lines.Count - Limit);
        if (_file is null) return;
        try { File.WriteAllLines(_file, _lines); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

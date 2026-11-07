namespace ChameLab.Cli;

// CLI paths retain their usual meaning. Embedded commands use an isolated workspace
// populated through the platform's file picker, without changing process cwd.
internal sealed class CommandFiles
{
    public string? Root { get; set; }
    public string Resolve(string path)
    {
        if (Root is null) return path;
        var root = Path.GetFullPath(Root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(path, root);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!full.StartsWith(root, comparison)) throw new ArgumentException("Use a file in the command workspace.");
        return full;
    }

    public byte[] ReadAllBytes(string path) => File.ReadAllBytes(Resolve(path));
    public string ReadAllText(string path) => File.ReadAllText(Resolve(path));
    public IEnumerable<string> ReadLines(string path) => File.ReadLines(Resolve(path));
    public Task<string> ReadAllTextAsync(string path, CancellationToken ct) => File.ReadAllTextAsync(Resolve(path), ct);
    public void WriteAllBytes(string path, byte[] data) => File.WriteAllBytes(Resolve(path), data);
    public void WriteAllLines(string path, IEnumerable<string> lines) => File.WriteAllLines(Resolve(path), lines);
    public Task WriteAllBytesAsync(string path, byte[] data, CancellationToken ct) => File.WriteAllBytesAsync(Resolve(path), data, ct);
    public Task WriteAllLinesAsync(string path, IEnumerable<string> lines, CancellationToken ct) => File.WriteAllLinesAsync(Resolve(path), lines, ct);
    public Task WriteAllTextAsync(string path, string text, CancellationToken ct = default) => File.WriteAllTextAsync(Resolve(path), text, ct);
    public void Move(string source, string target, bool overwrite) => File.Move(Resolve(source), Resolve(target), overwrite);
}

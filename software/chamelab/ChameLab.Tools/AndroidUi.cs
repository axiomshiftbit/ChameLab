using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace ChameLab.Tools;

internal static class AndroidUi
{
    private const string Package = "io.github.axiomshiftbit.chamelab.uitests";
    public static async Task RunAsync(string apk, string output)
    {
        Directory.CreateDirectory(output);
        Console.WriteLine("Installing UI test APK...");
        await Adb(["install", "--no-streaming", "-r", apk], timeoutMinutes: 5);
        var failures = new List<string>();
        (string Name, int Width, int Height, double Scale)[] profiles =
            [("small",320,640,1), ("phone",360,800,1), ("large-text",360,800,1.5), ("landscape",800,360,1), ("tablet",1280,800,1)];
        try
        {
            foreach (var p in profiles)
            {
                Console.WriteLine($"Starting UI profile: {p.Name}");
                var folder = Path.Combine(output, p.Name); Directory.CreateDirectory(folder);
                await Adb(["shell", "am", "force-stop", Package]);
                await Adb(["shell", "wm", "size", $"{p.Width}x{p.Height}"]);
                await Adb(["shell", "wm", "density", "160"]);
                await Adb(["shell", "settings", "put", "system", "font_scale", p.Scale.ToString(CultureInfo.InvariantCulture)]);
                await Adb(["shell", "run-as", Package, "rm", "-rf", "files/ui-results"]);
                await Adb(["shell", "run-as", Package, "mkdir", "-p", "files/ui-results"]);
                var request = Encoding.UTF8.GetBytes(new JsonObject { ["FontScale"] = p.Scale }.ToJsonString());
                var requestPath = Path.Combine(folder, "request.json");
                await File.WriteAllBytesAsync(requestPath, request);
                // Legacy adbd (API 23) cannot signal stdin EOF independently from closing
                // the shell connection. A redirected `cat` can wait forever on that device.
                await Adb(["push", requestPath, "/data/local/tmp/chamelab-ui-request.json"]);
                await Adb(["shell", "run-as", Package, "cp", "/data/local/tmp/chamelab-ui-request.json", "files/ui-results/request.json"]);
                await Adb(["shell", "rm", "/data/local/tmp/chamelab-ui-request.json"]);
                var activities = Encoding.UTF8.GetString(await Adb(["shell", "cmd", "package", "resolve-activity", "--brief", Package], false));
                var component = activities.Split('\n').Select(s => s.Trim()).LastOrDefault(s => s.Contains('/'));
                if (component is not null) await Adb(["shell", "am", "start", "-W", "-n", component]);
                else await Adb(["shell", "monkey", "-p", Package, "-c", "android.intent.category.LAUNCHER", "1"]);
                JsonNode? report = null;
                for (var i = 0; i < 180; i++)
                {
                    var raw = await Adb(["exec-out", "run-as", Package, "cat", "files/ui-results/result.json"], false);
                    try { if (raw.Length > 0) report = JsonNode.Parse(raw); } catch (System.Text.Json.JsonException) { }
                    if (report is not null) break;
                    await Task.Delay(2000);
                }
                var names = Encoding.UTF8.GetString(await Adb(["shell", "run-as", Package, "ls", "files/ui-results"], false));
                foreach (var name in names.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                    if (Path.GetFileName(name) == name && (name.EndsWith(".png") || name.EndsWith(".json")))
                        await File.WriteAllBytesAsync(Path.Combine(folder, name), await Adb(["exec-out", "run-as", Package, "cat", "files/ui-results/" + name]));
                await File.WriteAllBytesAsync(Path.Combine(folder,"android-screen.png"), await Adb(["exec-out", "screencap", "-p"]));
                if (report is null || (bool?)report["Complete"] != true || (int?)report["Failed"] != 0 || (int?)report["Passed"] != (int?)report["Expected"])
                {
                    failures.Add(p.Name);
                    await File.WriteAllBytesAsync(Path.Combine(folder,"logcat.txt"), await Adb(["logcat", "-d", "-s", "AndroidRuntime:E", "monodroid:E", "*:S"], false));
                    Console.WriteLine($"{p.Name}: {report?.ToJsonString() ?? "UI runner timed out"}");
                }
                else Console.WriteLine($"{p.Name}: {report["Passed"]} UI scenarios passed");
            }
        }
        finally
        {
            await Adb(["shell", "wm", "size", "reset"], false);
            await Adb(["shell", "wm", "density", "reset"], false);
            await Adb(["shell", "settings", "put", "system", "font_scale", "1"], false);
            await Adb(["shell", "am", "force-stop", Package], false);
        }
        if (failures.Count > 0) throw new InvalidOperationException("Failed UI profiles: " + string.Join(", ", failures));
    }
    private static async Task<byte[]> Adb(string[] args, bool check = true, int timeoutMinutes = 2)
    {
        var start = new ProcessStartInfo("adb") { UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Cannot start adb.");
        using var bytes = new MemoryStream();
        var stdout = process.StandardOutput.BaseStream.CopyToAsync(bytes);
        var stderr = process.StandardError.ReadToEndAsync();
        process.StandardInput.Close();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(timeoutMinutes));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException) { process.Kill(true); throw new TimeoutException($"adb {string.Join(' ', args.Take(3))} timed out."); }
        await stdout; var error = await stderr;
        if (check && process.ExitCode != 0) throw new InvalidOperationException("adb failed: " + error);
        return bytes.ToArray();
    }
}

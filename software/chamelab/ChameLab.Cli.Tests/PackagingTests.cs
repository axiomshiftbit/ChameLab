using System.IO.Compression;
using Xunit;

namespace ChameLab.Cli.Tests;

public class PackagingTests
{
    [Fact]
    public async Task MacBundleContainsVersionIconAndExecutable()
    {
        var root = Path.Combine(Path.GetTempPath(), "chamelab-mac-" + Guid.NewGuid().ToString("N"));
        try
        {
            var branding = Directory.CreateDirectory(Path.Combine(root, "software/chamelab/Branding")).FullName;
            await File.WriteAllBytesAsync(Path.Combine(branding, "chameleon.icns"), [0x69, 0x63, 0x6e, 0x73]);
            var publish = Directory.CreateDirectory(Path.Combine(root, "publish")).FullName;
            await File.WriteAllTextAsync(Path.Combine(publish, "ChameLab"), "synthetic executable");
            ChameLab.Tools.DesktopBundle.CreateMac(publish, "1.5.1-dev.2", root);
            var contents = Path.Combine(publish, "ChameLab.app/Contents");
            Assert.True(File.Exists(Path.Combine(contents, "MacOS/ChameLab")));
            Assert.True(File.Exists(Path.Combine(contents, "Resources/chameleon.icns")));
            var plist = System.Xml.Linq.XDocument.Load(Path.Combine(contents, "Info.plist"));
            Assert.Contains(plist.Descendants("string"), value => value.Value == "1.5.1");
            Assert.Contains(plist.Descendants("string"), value => value.Value == "io.github.axiomshiftbit.chamelab");
            if (!OperatingSystem.IsWindows()) Assert.True(File.GetUnixFileMode(Path.Combine(contents, "MacOS/ChameLab")).HasFlag(UnixFileMode.UserExecute));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task ReleaseNamesKeepVersionsAndArchiveContainsExecutable()
    {
        var directory = Path.Combine(Path.GetTempPath(), "chamelab-packaging-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var source = Path.Combine(directory, "source"); Directory.CreateDirectory(source);
            await File.WriteAllTextAsync(Path.Combine(source, "chamelab"), "executable fixture");
            var zip = Path.Combine(directory, "client-windows.zip");
            Assert.Equal(0, await ChameLab.Tools.Program.Main(["zip", source, zip]));
            using (var archive = ZipFile.OpenRead(zip))
            {
                Assert.Equal("chamelab", Assert.Single(archive.Entries).FullName);
                using var reader = new StreamReader(archive.Entries[0].Open());
                Assert.Equal("executable fixture", await reader.ReadToEndAsync());
            }
            await File.WriteAllTextAsync(Path.Combine(directory, "ultra-dfu-app.zip"), "firmware fixture");
            Assert.Equal(0, await ChameLab.Tools.Program.Main(["name-release-files", directory, "1.3.33", "2.3.4-cl"]));
            Assert.True(File.Exists(Path.Combine(directory, "client-1.3.33-windows.zip")));
            Assert.True(File.Exists(Path.Combine(directory, "ultra-dfu-app-2.3.4-cl.zip")));
            Assert.Equal(0, await ChameLab.Tools.Program.Main(["name-release-files", directory, "1.3.33", "2.3.4-cl"]));
            Assert.True(File.Exists(Path.Combine(directory, "client-1.3.33-windows.zip")));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}

using System.Xml.Linq;

namespace ChameLab.Tools;

internal static class DesktopBundle
{
    public static void CreateMac(string publish, string version, string root)
    {
        version = version.Split('-')[0];
        if (!Version.TryParse(version, out _)) throw new ArgumentException("Invalid app version");
        var files = Directory.GetFileSystemEntries(publish);
        var contents = Path.Combine(publish, "ChameLab.app", "Contents");
        var binaries = Directory.CreateDirectory(Path.Combine(contents, "MacOS")).FullName;
        var resources = Directory.CreateDirectory(Path.Combine(contents, "Resources")).FullName;
        foreach (var file in files)
        {
            var destination = Path.Combine(binaries, Path.GetFileName(file));
            if (Directory.Exists(file)) Directory.Move(file, destination); else File.Move(file, destination);
        }
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(Path.Combine(binaries, "ChameLab"),
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
            UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        File.Copy(Path.Combine(root, "software/chamelab/Branding/chameleon.icns"), Path.Combine(resources, "chameleon.icns"));
        var dictionary = new XElement("dict");
        foreach (var (key, value) in new[] {
            ("CFBundleExecutable", "ChameLab"), ("CFBundleIdentifier", "io.github.axiomshiftbit.chamelab"),
            ("CFBundleName", "ChameLab"), ("CFBundlePackageType", "APPL"),
            ("CFBundleShortVersionString", version), ("CFBundleVersion", version), ("CFBundleIconFile", "chameleon.icns") })
            dictionary.Add(new XElement("key", key), new XElement("string", value));
        dictionary.Add(new XElement("key", "NSHighResolutionCapable"), new XElement("true"));
        new XDocument(new XDeclaration("1.0", "UTF-8", null),
            new XDocumentType("plist", "-//Apple//DTD PLIST 1.0//EN", "http://www.apple.com/DTDs/PropertyList-1.0.dtd", null),
            new XElement("plist", new XAttribute("version", "1.0"), dictionary)).Save(Path.Combine(contents, "Info.plist"));
    }
}

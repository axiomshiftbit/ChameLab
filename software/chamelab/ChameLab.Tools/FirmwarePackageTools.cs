using ChameLab.Models;
using ChameLab.Protocol;

namespace ChameLab.Tools;

internal static class FirmwarePackageTools
{
    public static void Verify(string path, string model)
    {
        var target = model switch { "ultra" => DeviceModel.Ultra, "lite" => DeviceModel.Lite, _ => throw new ArgumentException("Expected ultra or lite") };
        if (new FileInfo(path).Length > DfuPackage.MaxZipSize) throw new InvalidDataException("DFU package is too large");
        var package = DfuPackage.Parse(File.ReadAllBytes(path));
        package.RequireModel(target);
        Console.WriteLine($"DFU verified: model={model}, image={package.Firmware.Length} bytes, SHA-256=OK, application only");
    }
}

using ChameLab.Transport.Serial;
using Xunit;

namespace ChameLab.Cli.Tests;

public class LinuxSerialPortTests
{
    /// <summary>
    /// The sysfs layout of a CDC-ACM port: the class entry and its "device" are both relative links,
    /// and the second one only resolves from the real directory of the first.
    /// </summary>
    [Fact]
    public void UsbIdsAreReadThroughTheSysfsLinks()
    {
        if (OperatingSystem.IsWindows()) return; // creating links needs a privilege there
        var sysfs = Path.Combine(Path.GetTempPath(), "chamelab-sysfs-" + Guid.NewGuid().ToString("N"));
        try
        {
            var usb = Directory.CreateDirectory(Path.Combine(sysfs, "devices/pci0000:00/usb3/3-2")).FullName;
            File.WriteAllText(Path.Combine(usb, "idVendor"), "6868\n");
            File.WriteAllText(Path.Combine(usb, "idProduct"), "8686\n");
            File.WriteAllText(Path.Combine(usb, "serial"), "DC739D15DFA9\n");
            var tty = Directory.CreateDirectory(Path.Combine(usb, "3-2:1.0/tty/ttyACM0")).FullName;
            Directory.CreateSymbolicLink(Path.Combine(tty, "device"), "../../../3-2:1.0");
            var classTty = Directory.CreateDirectory(Path.Combine(sysfs, "class/tty")).FullName;
            Directory.CreateSymbolicLink(Path.Combine(classTty, "ttyACM0"), "../../devices/pci0000:00/usb3/3-2/3-2:1.0/tty/ttyACM0");

            Assert.Equal("6868", SerialScanner.LinuxUsbId("/dev/ttyACM0", "idVendor", sysfs));
            Assert.Equal("8686", SerialScanner.LinuxUsbId("/dev/ttyACM0", "idProduct", sysfs));
            Assert.Equal("DC739D15DFA9", SerialScanner.LinuxUsbId("/dev/ttyACM0", "serial", sysfs));
            Assert.Null(SerialScanner.LinuxUsbId("/dev/ttyACM1", "idVendor", sysfs));
        }
        finally { Directory.Delete(sysfs, recursive: true); }
    }
}

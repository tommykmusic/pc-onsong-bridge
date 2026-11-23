using System;
using System.Linq;
using System.Windows.Forms;
using PcOnSongBridge.Setup;

internal static class PlatformTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }

    [STAThread]
    private static void Main()
    {
        Check(Installer.Support(true, 10, 14393, 528040) == PlatformSupport.Ready, "Supported Windows 10 / .NET 4.8 rejected.");
        Check(Installer.Support(true, 10, 26100, 533325) == PlatformSupport.Ready, "Windows 11 / .NET 4.8.1 rejected.");
        Check(Installer.Support(true, 10, 17763, 461814) == PlatformSupport.FrameworkRequired, "Old framework accepted.");
        Check(Installer.Support(true, 10, 17763, 0) == PlatformSupport.FrameworkRequired, "Missing framework accepted.");
        Check(Installer.Support(false, 10, 19045, 528040) == PlatformSupport.WindowsRequired &&
            Installer.Support(true, 6, 9600, 528049) == PlatformSupport.WindowsRequired &&
            Installer.Support(true, 10, 10586, 461814) == PlatformSupport.WindowsRequired, "Unsupported Windows accepted.");
        Check(Installer.StartArguments(false, false) == null && Installer.StartArguments(false, true) == String.Empty &&
            Installer.StartArguments(true, true) == "--setup-features --open" &&
            Installer.StartArguments(true, false) == "--setup-features", "Installation launch choices changed.");
        foreach (bool existing in new[]{false,true})
        using (var form = new SetupForm(existing, PlatformSupport.Ready))
            Check(((CheckBox)form.Controls.Find("chooseFeatures", true).Single()).Checked == !existing,
                "First install / update default is incorrect.");
        Console.WriteLine("PASS core Windows and .NET gates, first install and upgrade defaults, offline and open choices.");
    }
}

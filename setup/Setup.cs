using Microsoft.Win32;
using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyTitle("PC OnSong Bridge Setup")]
[assembly: AssemblyProduct("PC OnSong Bridge")]
[assembly: AssemblyCompany("PC OnSong Bridge contributors")]

namespace PcOnSongBridge.Setup
{
    internal enum PlatformSupport { Ready, WindowsRequired, FrameworkRequired }

    internal static class Installer
    {
        internal const string Product = "PC OnSong Bridge";
        private const string Executable = "PC-OnSong-Bridge.exe";
        private const string Publisher = "PC OnSong Bridge contributors";
        private const string RegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\PCOnSongBridge";
        private const string MutexName = @"Local\PCOnSongBridge.Setup";

        internal static readonly string InstallDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", Product);
        private static readonly string InstalledExe = Path.Combine(InstallDirectory, Executable);
        private static readonly string Shortcut = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Programs), Product + ".lnk");

        internal static bool AlreadyInstalled { get { return File.Exists(InstalledExe); } }

        internal static PlatformSupport Support(bool windows64, int major, int build, int frameworkRelease)
        {
            // Windows 10 version 1607 is the earliest Windows 10 able to install .NET Framework 4.8.
            if (!windows64 || major < 10 || build < 14393) return PlatformSupport.WindowsRequired;
            return frameworkRelease >= 528040 ? PlatformSupport.Ready : PlatformSupport.FrameworkRequired;
        }

        internal static PlatformSupport CheckPlatform()
        {
            using (var registry = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32))
            using (var net = registry.OpenSubKey(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full"))
            using (var os = registry.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion"))
            {
                int build;
                Int32.TryParse(os == null ? null : os.GetValue("CurrentBuildNumber") as string, out build);
                return Support(Environment.Is64BitOperatingSystem,
                    os == null ? 0 : Convert.ToInt32(os.GetValue("CurrentMajorVersionNumber", 0)),
                    build, net == null ? 0 : Convert.ToInt32(net.GetValue("Release", 0)));
            }
        }

        [STAThread]
        private static int Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using (var form = new SetupForm())
            {
                return form.ShowDialog() == DialogResult.OK ? 0 : 1;
            }
        }

        internal static void Install()
        {
            if (CheckPlatform() != PlatformSupport.Ready)
                throw new InvalidOperationException("Setup requires 64-bit Windows 10 or 11 and .NET Framework 4.8 or later.");
            using (var mutex = new Mutex(false, MutexName))
            {
                try
                {
                    if (!mutex.WaitOne(0))
                        throw new InvalidOperationException("Another PC OnSong Bridge setup is running.");
                }
                catch (AbandonedMutexException) { }

                try
                {
                    InstallFiles();
                    CreateShortcut();
                    RegisterUninstall();
                }
                finally
                {
                    mutex.ReleaseMutex();
                }
            }
        }

        private static void InstallFiles()
        {
            Directory.CreateDirectory(InstallDirectory);
            CopyResource("BridgeApp.exe", InstalledExe);
            CopyResource("LICENSE", Path.Combine(InstallDirectory, "LICENSE.txt"));
        }

        private static void CopyResource(string resource, string destination)
        {
            string staging = Path.Combine(InstallDirectory, Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (Stream content = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource))
                {
                    if (content == null)
                        throw new InvalidOperationException("The installer is missing " + resource + ".");
                    using (var file = new FileStream(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        content.CopyTo(file);
                }

                if (!File.Exists(destination))
                {
                    File.Move(staging, destination);
                    return;
                }

                try
                {
                    ReplaceFile(staging, destination);
                }
                catch (IOException error)
                {
                    if (destination != InstalledExe)
                        throw;
                    throw new IOException("Close PC OnSong Bridge and try the update again. " + error.Message, error);
                }
                catch (UnauthorizedAccessException error)
                {
                    if (destination != InstalledExe)
                        throw;
                    throw new IOException("Close PC OnSong Bridge and try the update again. " + error.Message, error);
                }
            }
            finally
            {
                File.Delete(staging);
            }
        }

        private static void ReplaceFile(string staging, string destination)
        {
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    File.Replace(staging, destination, null);
                    return;
                }
                catch (IOException error)
                {
                    int code = error.HResult & 0xffff;
                    // Real-time scanners can briefly lock a newly written executable.
                    if (attempt == 4 || (code != 32 && code != 33 && code != 1175))
                        throw;
                    Thread.Sleep(attempt * 150);
                }
            }
        }

        private static void CreateShortcut()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Shortcut));
            object shell = Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046")));
            try
            {
                var shortcut = (IShellLinkW)shell;
                shortcut.SetPath(InstalledExe);
                shortcut.SetIconLocation(InstalledExe, 0);
                shortcut.SetWorkingDirectory(InstallDirectory);
                shortcut.SetDescription(Product);
                ((IPersistFile)shell).Save(Shortcut, true);
            }
            finally
            {
                Marshal.FinalReleaseComObject(shell);
            }
        }

        private static void RegisterUninstall()
        {
            string powershell = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
            string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(UninstallScript));
            string command = "\"" + powershell + "\" -NoProfile -NonInteractive -WindowStyle Hidden -EncodedCommand " + encoded;
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RegistryPath))
            {
                key.SetValue("DisplayName", Product);
                key.SetValue("DisplayVersion", BridgeRelease.Version);
                key.SetValue("DisplayIcon", InstalledExe + ",0");
                key.SetValue("InstallLocation", InstallDirectory);
                key.SetValue("Publisher", Publisher);
                key.SetValue("EstimatedSize", (int)((new FileInfo(InstalledExe).Length + 1023) / 1024), RegistryValueKind.DWord);
                key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                key.SetValue("UninstallString", command);
            }
        }

        internal static string StartArguments(bool chooseFeatures, bool openApp)
        {
            if (chooseFeatures) return openApp ? "--setup-features --open" : "--setup-features";
            return openApp ? String.Empty : null;
        }

        internal static void OpenApplication(string arguments)
        {
            Process.Start(new ProcessStartInfo(InstalledExe, arguments) { WorkingDirectory = InstallDirectory, UseShellExecute = true });
        }

        [ComImport]
        [Guid("000214F9-0000-0000-C000-000000000046")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellLinkW
        {
            void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int capacity, IntPtr data, uint flags);
            void GetIDList(out IntPtr itemIdList);
            void SetIDList(IntPtr itemIdList);
            void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder description, int capacity);
            void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string description);
            void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory, int capacity);
            void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
            void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int capacity);
            void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
            void GetHotkey(out short hotkey);
            void SetHotkey(short hotkey);
            void GetShowCmd(out int showCommand);
            void SetShowCmd(int showCommand);
            void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder location, int capacity, out int index);
            void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string location, int index);
            void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
            void Resolve(IntPtr window, uint flags);
            void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
        }

        // Windows launches this command from Apps settings. It removes the files and registration
        // created by the installer and preserves application settings.
        private const string UninstallScript = @"
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
$mutex = [Threading.Mutex]::new($false, 'Local\PCOnSongBridge.Setup')
$ownsMutex = $false
try {
    try { $ownsMutex = $mutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $ownsMutex = $true }
    if (-not $ownsMutex) { throw 'Another PC OnSong Bridge setup is running.' }
    $dir = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'Programs\PC OnSong Bridge'
    $app = Join-Path $dir 'PC-OnSong-Bridge.exe'
    try { [IO.File]::Delete($app) } catch { throw 'Close PC OnSong Bridge and try uninstalling again.' }
    [IO.File]::Delete((Join-Path $dir 'LICENSE.txt'))
    $shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'PC OnSong Bridge.lnk'
    [IO.File]::Delete($shortcut)
    if ([IO.Directory]::Exists($dir) -and [IO.Directory]::GetFileSystemEntries($dir).Length -eq 0) {
        [IO.Directory]::Delete($dir)
    }
    [Microsoft.Win32.Registry]::CurrentUser.DeleteSubKeyTree(
        'Software\Microsoft\Windows\CurrentVersion\Uninstall\PCOnSongBridge', $false)
    exit 0
} catch {
    [void][Windows.Forms.MessageBox]::Show($_.Exception.Message, 'PC OnSong Bridge', 'OK', 'Error')
    exit 1
} finally {
    if ($ownsMutex) { $mutex.ReleaseMutex() }
    $mutex.Dispose()
}
";
    }

    internal sealed class SetupForm : BridgeWindow
    {
        internal SetupForm() : this(Installer.AlreadyInstalled, Installer.CheckPlatform()) { }

        internal SetupForm(bool alreadyInstalled, PlatformSupport support)
        {
            Text = Installer.Product + " Setup v" + BridgeRelease.Version;
            StartPosition = FormStartPosition.CenterScreen;
            AutoScaleMode = AutoScaleMode.Font;
            Font = new Font("Segoe UI", 10);
            ClientSize = new Size(550, 333);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = false;
            MaximizeBox = false;
            Padding = new Padding(24);

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5 };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(layout);

            var title = new Label
            {
                Text = "Install PC OnSong Bridge",
                Font = new Font(Font.FontFamily, 21, FontStyle.Bold),
                AutoSize = true,
                Margin = new Padding(0, 0, 0, 8)
            };
            layout.Controls.Add(title, 0, 0);
            var description = new Label
            {
                Text = "Setup installs the bridge for your Windows account and adds a Start menu shortcut. " +
                    "Choose optional components for Wi-Fi audio, Android USB or voice commands in the next step. " +
                    "You can also set these up later.\r\n\r\nClose PC OnSong Bridge before updating.",
                Dock = DockStyle.Fill,
                Margin = new Padding(0, 0, 0, 8)
            };
            layout.Controls.Add(description, 0, 1);

            var features = new CheckBox
            {
                Name = "chooseFeatures", Text = "Choose optional components after installation",
                Checked = !alreadyInstalled, AutoSize = true, Margin = new Padding(0, 0, 0, 4)
            };
            layout.Controls.Add(features, 0, 2);

            var open = new CheckBox
            {
                Text = "Open PC OnSong Bridge after setup",
                Checked = true,
                AutoSize = true,
                Margin = new Padding(0, 0, 0, 10)
            };
            layout.Controls.Add(open, 0, 3);

            var actions = new FlowLayoutPanel
            {
                AutoSize = true,
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft,
                WrapContents = false,
                Margin = new Padding(0)
            };
            var install = new Button { Text = "Install", AutoSize = true };
            var cancel = new Button { Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel };
            actions.Controls.Add(install);
            actions.Controls.Add(cancel);
            layout.Controls.Add(actions, 0, 4);
            AcceptButton = install;
            CancelButton = cancel;

            if (support != PlatformSupport.Ready)
            {
                features.Visible = open.Visible = install.Visible = false;
                cancel.Text = "Close";
                AcceptButton = cancel;
                description.Text = support == PlatformSupport.FrameworkRequired ?
                    "Install Microsoft .NET Framework 4.8 Runtime, then run this setup again. Microsoft also offers an offline installer you can copy to this PC." :
                    "This release requires 64-bit Windows 10 version 1607 or later, or Windows 11. Check the system type and version in Windows Settings.";
                if (support == PlatformSupport.FrameworkRequired)
                {
                    var microsoft = new Button { Text = "Get Microsoft .NET Framework 4.8", AutoSize = true };
                    microsoft.Click += delegate
                    {
                        try { Process.Start(new ProcessStartInfo("https://dotnet.microsoft.com/en-us/download/dotnet-framework/net48") { UseShellExecute = true }); }
                        catch (Exception) { description.Text = "Visit dotnet.microsoft.com/download/dotnet-framework/net48 for the Microsoft .NET Framework 4.8 Runtime, then run this setup again."; }
                    };
                    actions.Controls.Add(microsoft);
                }
            }

            install.Click += delegate
            {
                try
                {
                    Installer.Install();
                }
                catch (Exception error)
                {
                    MessageBox.Show(this, error.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }

                string arguments = Installer.StartArguments(features.Checked, open.Checked);
                if (arguments != null)
                {
                    try { Installer.OpenApplication(arguments); }
                    catch (Exception error)
                    {
                        MessageBox.Show(this, "Setup completed. Open PC OnSong Bridge from Start.\r\n\r\n" + error.Message,
                            Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                    }
                }
                DialogResult = DialogResult.OK;
                Close();
            };
        }
    }
}

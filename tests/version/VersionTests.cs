using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;

internal static class VersionTests
{
    private const BindingFlags Constructors = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (Control nested in Descendants(child)) yield return nested;
        }
    }

    [STAThread]
    private static void Main(string[] args)
    {
        string appPath = Path.GetFullPath(args[0]);
        string setupPath = Path.GetFullPath(args[1]);
        string iconPath = Path.GetFullPath(args[2]);
        byte[] sourceIcon = File.ReadAllBytes(iconPath);
        var app = Assembly.LoadFrom(appPath);
        var setup = Assembly.LoadFrom(setupPath);
        var version = app.GetName().Version;
        string release = version.ToString(3);
        foreach (var binary in new[] { app, setup })
        {
            var file = FileVersionInfo.GetVersionInfo(binary.Location);
            Require(binary.GetName().Version == version && file.FileVersion == version.ToString(4)
                && file.ProductVersion == release, "Compiled app and setup versions disagree.");
            string sizes = IconResources.VerifyExecutable(binary.Location, sourceIcon);
            Console.WriteLine("PASS " + Path.GetFileName(binary.Location) + " Windows icon frames at " + sizes + " px");
        }
        using (var payload = setup.GetManifestResourceStream("BridgeApp.exe"))
        using (var copy = new MemoryStream())
        {
            payload.CopyTo(copy);
            Require(File.ReadAllBytes(appPath).SequenceEqual(copy.ToArray()), "Installer contains a different app.");
        }
        Console.WriteLine("PASS app metadata, installer metadata and embedded app v" + release);

        var prefs = Activator.CreateInstance(app.GetType("PcOnSongBridge.Preferences", true));
        using (var setupForm = (Form)Activator.CreateInstance(setup.GetType("PcOnSongBridge.Setup.SetupForm", true), true))
        using (var microphone = (Form)Activator.CreateInstance(app.GetType("PcOnSongBridge.MicrophoneTest", true), Constructors,
            null, new object[] { null, null }, null))
        {
            Require(IconResources.FormShowsIcon(setupForm, iconPath), "Setup window icon differs from the app icon.");
            Require(IconResources.FormShowsIcon(microphone, iconPath), "Standalone microphone window icon differs from the app icon.");
        }
        // Construct without showing the bridge. This keeps tablet discovery and saved user settings out of this test.
        using (var main = (Form)Activator.CreateInstance(app.GetType("PcOnSongBridge.MainForm", true), Constructors,
            null, new[] { prefs }, null))
        {
            Require(main.Text.EndsWith(" v" + release, StringComparison.Ordinal), "Main title has the wrong version.");
            Require(IconResources.FormShowsIcon(main, iconPath), "Main window icon differs from the app icon.");
            var about = Descendants(main).OfType<Button>().Single(b => b.Text == "&About v" + release);
            Require(about.TabStop && about.UseMnemonic, "About must be keyboard accessible.");
            // Invoke the same handler used by the About button without displaying the main window.
            bool verified = false;
            using (var timer = new Timer { Interval = 25 })
            {
                timer.Tick += delegate
                {
                    timer.Stop();
                    var dialog = Application.OpenForms.Cast<Form>().FirstOrDefault(f => f.Modal);
                    if (dialog == null) return;
                    var content = Descendants(dialog).ToArray();
                    verified = dialog.Text == "About PC OnSong Bridge"
                        && IconResources.FormShowsIcon(dialog, iconPath)
                        && content.OfType<Label>().Any(l => l.Text.Contains("Version " + release))
                        && content.OfType<TextBox>().Any(t => t.ReadOnly && t.Text == appPath)
                        && dialog.AcceptButton != null && dialog.AcceptButton == dialog.CancelButton;
                    dialog.Close();
                };
                timer.Start();
                typeof(Button).GetMethod("OnClick", BindingFlags.NonPublic | BindingFlags.Instance)
                    .Invoke(about, new object[] { EventArgs.Empty });
            }
            Require(verified, "About must show the running version and executable, and support Enter and Escape.");
        }
        Console.WriteLine("PASS main, About, microphone and setup window icons; About identifies this running build");
    }
}

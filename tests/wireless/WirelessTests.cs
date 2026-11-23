using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using PcOnSongBridge;

internal static class WirelessTests
{
    private const string First = "adb-TCL-aa _adb-tls-connect._tcp. 192.0.2.7:39999\n";
    private const string Pairing = "adb-TCL-pair _adb-tls-pairing._tcp 192.0.2.7:33002\n";
    private static readonly string Dir = AppDomain.CurrentDomain.BaseDirectory;
    private static string FileAt(string name) { return Path.Combine(Dir, name); }
    private static void Check(bool ok, string text) { if (!ok) throw new Exception(text); }
    private static IEnumerable<Control> Children(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (Control sub in Children(child)) yield return sub;
        }
    }
    private static Button Button(Form form, string text)
    {
        return Children(form).OfType<Button>().Single(item => item.Text == text);
    }
    private static ComboBox Picker(Form form, string label)
    {
        var table = (TableLayoutPanel)Children(form).OfType<Label>().Single(item => item.Text == label).Parent;
        return (ComboBox)table.GetControlFromPosition(1, 0);
    }
    private static async Task Until(Func<bool> predicate, string message)
    {
        var timer = Stopwatch.StartNew();
        while (!predicate())
        {
            if (timer.ElapsedMilliseconds > 7000) throw new Exception(message);
            await Task.Delay(30);
        }
    }

    private static async Task CheckDialog(MainForm.WirelessDialog dialog)
    {
        var find = Button(dialog, "Find tablets");
        var connect = Picker(dialog, "Connection IP:port");
        var pairing = Picker(dialog, "Pairing IP:port");
        await Until(() => find.Enabled && connect.Items.Count > 0, "Initial discovery did not complete.");
        Check(connect.Items.Count == 2 && pairing.Items.Count == 2 && connect.SelectedIndex < 0 &&
            pairing.SelectedIndex < 0, "Multiple or duplicate advertisements produced an ambiguous choice.");
        Check(!File.Exists(FileAt("actions.txt")), "Discovery dispatched a connect or pair command.");
        File.WriteAllText(FileAt("mdns.txt"), Pairing + First);
        find.PerformClick();
        await Until(() => find.Enabled && pairing.Items.Count == 1, "Single address refresh failed.");
        Check(pairing.SelectedIndex == 0 && connect.SelectedIndex == 0, "Single addresses were not filled in.");
        var code = Children(dialog).OfType<TextBox>().Single();
        code.Text = "123456";
        Button(dialog, "Pair").PerformClick();
        await Until(() => find.Enabled && code.Text == "", "Pairing failed to complete or clear the code.");
        Check(File.ReadAllLines(FileAt("actions.txt")).SequenceEqual(new[] { "pair 192.0.2.7:33002" }), "Unexpected target or pairing code in arguments.");
        File.WriteAllText(FileAt("mdns.txt"), First.Replace("39999", "41111"));
        find.PerformClick();
        await Until(() => find.Enabled && pairing.Items.Count == 0, "Port refresh failed.");
        Check(!connect.Text.Contains("39999"), "Stale connection port survived.");
        connect.Text = "192.0.2.7:39999";
        File.Delete(FileAt("mdns.txt"));
        find.PerformClick();
        await Until(() => find.Enabled && connect.Items.Count == 0, "Failed refresh left a stale advertisement.");
        Check(connect.Text == "192.0.2.7:39999", "Failed discovery removed the manual address.");
        File.WriteAllText(FileAt("mdns.txt"), "");
        find.PerformClick();
        await Until(() => find.Enabled, "Empty discovery did not complete.");
        Check(connect.Text == "192.0.2.7:39999", "Empty discovery removed the manual address.");
        Button(dialog, "Connect").PerformClick();
        await Until(() => dialog.ConnectedEndpoint != null, "Manual fallback failed.");
        Check(dialog.ConnectedServiceSerial == null && dialog.ConnectedEndpoint == "192.0.2.7:39999", "Manual connection returned wrong identity.");
        Console.WriteLine("PASS service filtering, explicit multiple choices, selected pairing, code clearing, port changes and manual fallback.");
    }

    private static async Task Exercise(MainForm form)
    {
        File.WriteAllText(FileAt("mdns.txt"), First + First + Pairing +
            "another _adb-tls-connect._tcp [2001:db8::4]:35678\nother _adb-tls-pairing._tcp 192.0.2.9:30999\n" +
            "legacy _adb._tcp 192.0.2.88:5555\nbad _adb-tls-connect._tcp bad:2\ninvalid _adb-tls-pairing._tcp 192.0.2.9:68888");
        using (var bridge = new AndroidBridge(FileAt("scrcpy.exe")))
        using (var dialog = new MainForm.WirelessDialog(bridge) { ShowInTaskbar = false, Opacity = 0 })
        {
            dialog.Show(form);
            await CheckDialog(dialog);
        }
        File.Delete(FileAt("connected"));
        await Until(() => Button(form, "Start cue audio").Enabled, "USB fixture not selected.");
        Button(form, "Check navigation").PerformClick();
        await Until(() => Button(form, "Next song").Enabled, "USB navigation verification failed.");
        Button(form, "Start cue audio").PerformClick();
        var wifi = Button(form, "Wireless setup");
        await Until(() => File.Exists(FileAt("stream.pid")) && wifi.Enabled, "Wireless setup unavailable during USB playback.");
        using (var running = Process.GetProcessById(Int32.Parse(File.ReadAllText(FileAt("stream.pid")))))
        {
            File.WriteAllText(FileAt("mdns.txt"), First);
            form.BeginInvoke(new Action(wifi.PerformClick));
            await Until(() => Application.OpenForms.OfType<MainForm.WirelessDialog>().Any(), "Wireless dialog failed to open.");
            var wireless = Application.OpenForms.OfType<MainForm.WirelessDialog>().Single();
            wireless.Opacity = 0;
            await Until(() => Picker(wireless, "Connection IP:port").SelectedIndex == 0 && Button(wireless, "Connect").Enabled, "Selected connection missing.");
            Button(wireless, "Connect").PerformClick();
            await Until(() => !Application.OpenForms.OfType<MainForm.WirelessDialog>().Any() && wifi.Enabled, "Wireless connection did not complete.");
            var tablets = Picker(form, "Android tablet");
            Check(!running.HasExited && (string)tablets.SelectedItem == "USB123" && Button(form, "Next song").Enabled,
                "Wireless preparation interrupted USB playback, changed the tablet, or invalidated navigation.");
            Button(form, "Stop").PerformClick();
            await Until(() => Button(form, "Refresh tablets").Enabled && running.HasExited, "USB stream failed to stop.");
            Button(form, "Refresh tablets").PerformClick();
            await Until(() => (string)tablets.SelectedItem == "adb-TCL-aa._adb-tls-connect._tcp", "Refresh did not select the prepared Wi-Fi device.");
            Check(tablets.GetItemText(tablets.SelectedItem).Contains("Wi-Fi / LAN"), "Wireless transport missing from tablet selector.");
            Check(File.ReadAllLines(FileAt("actions.txt")).Count(s => s.StartsWith("connect ")) == 2, "Additional connection dispatched.");
        }
        Console.WriteLine("PASS wireless preparation preserves a running USB stream and selects the advertised tablet after Stop and Refresh.");
    }

    [STAThread]
    private static int Main()
    {
        int exit = 1;
        Application.EnableVisualStyles();
        var preferences = Preferences.Load(FileAt("settings.xml"));
        preferences.ScrcpyPath = FileAt("scrcpy.exe");
        using (var form = new MainForm(preferences) { ShowInTaskbar = false, Opacity = 0 })
        {
            form.Shown += async delegate
            {
                try { await Exercise(form); exit = 0; }
                catch (Exception error) { Console.Error.WriteLine(error); }
                finally
                {
                    foreach (Form other in Application.OpenForms.Cast<Form>().ToArray())
                        if (other != form) other.Close();
                    form.Close();
                }
            };
            Application.Run(form);
        }
        return exit;
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using PcOnSongBridge;

internal static class FeatureTests
{
    private static void Check(bool value, string message) { if (!value) throw new Exception(message); }
    private static T Get<T>(Form form,string name) where T:Control { return (T)form.Controls.Find(name,true).Single(); }
    private static void Show(Form form) { form.ShowInTaskbar = false; form.Opacity = 0; form.Show(); Application.DoEvents(); }
    private static void Finish(Task task)
    {
        DateTime end = DateTime.UtcNow.AddSeconds(4);
        while (!task.IsCompleted && DateTime.UtcNow < end) { Application.DoEvents(); Thread.Sleep(5); }
        Check(task.IsCompleted, "Feature setup hung."); task.GetAwaiter().GetResult();
    }

    [STAThread]
    private static void Main(string[] args)
    {
        var calls = new List<SetupFeature>();
        var present = new HashSet<SetupFeature>();
        using (var form = new FeatureSetupForm(true,present.Contains,(feature, token) =>
        {
            calls.Add(feature);
            if (feature == SetupFeature.Usb) present.Add(feature);
            return Task.FromResult(true);
        }))
        {
            Show(form);
            Check(calls.Count == 0, "Clean install started a download or language settings without opt-in.");
            Finish(form.ApplyAsync());
            Check(calls.Count == 0, "Empty selection started an action.");
            Get<CheckBox>(form,"selectUsb").Checked = true;
            Finish(form.ApplyAsync());
            Check(calls.SequenceEqual(new[]{SetupFeature.Usb}) && !Get<CheckBox>(form,"selectUsb").Enabled,
                "USB setup ran unrelated actions or failed to detect installation.");
            Get<CheckBox>(form,"selectWifi").Checked = true;
            present.Add(SetupFeature.Wifi); // Installed externally after the chooser opened.
            Finish(form.ApplyAsync());
            Check(calls.Count == 1 && !Get<CheckBox>(form,"selectWifi").Enabled, "Already installed NDI setup was launched.");
            Get<CheckBox>(form,"selectSpeech").Checked = true;
            Finish(form.ApplyAsync());
            Check(calls.SequenceEqual(new[]{SetupFeature.Usb,SetupFeature.Speech}) &&
                Get<Label>(form,"statusSpeech").Text.Contains("Check again"), "Pending Windows language installation claimed ready.");
        }
        calls.Clear(); present.Clear();
        using (var form = new FeatureSetupForm(false,present.Contains,(feature, token) =>
        {
            calls.Add(feature);
            if (feature == SetupFeature.Wifi) throw new IOException("offline fixture");
            present.Add(feature); return Task.FromResult(true);
        }))
        {
            Show(form);
            Get<CheckBox>(form,"selectWifi").Checked = Get<CheckBox>(form,"selectUsb").Checked = true;
            Finish(form.ApplyAsync());
            Check(calls.SequenceEqual(new[]{SetupFeature.Wifi,SetupFeature.Usb}) && Get<Label>(form,"statusWifi").Text.Contains("offline fixture") &&
                Get<CheckBox>(form,"selectWifi").Enabled && !Get<CheckBox>(form,"selectUsb").Enabled, "Offline failure lost retry or independent work.");
        }
        calls.Clear();
        using (var form = new FeatureSetupForm(true, f=>false, (feature,token) => { calls.Add(feature); return Task.Delay(10000,token); }))
        {
            Show(form);
            Get<CheckBox>(form,"selectWifi").Checked = Get<CheckBox>(form,"selectUsb").Checked = true;
            Task pending = form.ApplyAsync();
            Finish(form.ApplyAsync());
            form.Close(); Finish(pending); Application.DoEvents();
            Check(calls.SequenceEqual(new[]{SetupFeature.Wifi}) && !form.Visible, "Cancel or duplicate click launched another action.");
        }

        string scrcpy = Path.Combine(args[0],"scrcpy.exe");
        File.WriteAllText(scrcpy,"fixture");
        Check(!FeatureComponents.ScrcpyPair(scrcpy), "USB readiness did not require adjacent adb.");
        File.WriteAllText(Path.Combine(args[0],"adb.exe"),"fixture");
        Check(FeatureComponents.ScrcpyPair(scrcpy) && !FeatureComponents.ScrcpyPair(Path.Combine(args[0],"adb.exe")),
            "Offline extracted folder selection failed.");
        Console.WriteLine("PASS isolated missing components, opt-in, offline, existing installations, Windows language settings, cancellation and extracted USB tools.");
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using PcOnSongBridge;

internal static class UiTests
{
    private static IEnumerable<Control> Children(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (Control descendant in Children(child)) yield return descendant;
        }
    }

    private static async Task Until(Func<bool> condition, string description)
    {
        var time = Stopwatch.StartNew();
        while (!condition())
        {
            if (time.ElapsedMilliseconds > 10000) throw new Exception("Timed out waiting for " + description);
            await Task.Delay(40);
        }
    }

    private static void WriteSilentWave(string path)
    {
        byte[] silence = new byte[48000 * 2];
        using (var writer = new BinaryWriter(File.Create(path)))
        {
            writer.Write(Encoding.ASCII.GetBytes("RIFF"));
            writer.Write(36 + silence.Length);
            writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
            writer.Write(16);
            writer.Write((short)1);
            writer.Write((short)1);
            writer.Write(48000);
            writer.Write(96000);
            writer.Write((short)2);
            writer.Write((short)16);
            writer.Write(Encoding.ASCII.GetBytes("data"));
            writer.Write(silence.Length);
            writer.Write(silence);
        }
    }

    private static async Task Exercise(MainForm form, string dir)
    {
        var controls = Children(form).ToArray();
        Func<string, Button> button = name => controls.OfType<Button>().Single(item => item.Text == name);
        Func<string, bool> hasLabel = value => controls.OfType<Label>().Any(item => item.Text == value);
        var row = (TableLayoutPanel)controls.OfType<Label>().Single(item => item.Text == "Expected output").Parent;
        var outputs = (ComboBox)row.GetControlFromPosition(1, 0);
        var start = button("Start cue audio");
        var refresh = button("Refresh tablets");
        await Until(() => start.Enabled, "tablet discovery");

        button("Check navigation").PerformClick();
        await Until(() => button("Next song").Enabled, "OnSong handler verification");
        button("Next song").PerformClick();
        await Until(() => controls.OfType<TextBox>().Any(item => item.Text.Contains("Sent manual command: next song")), "manual navigation");

        var mode = controls.OfType<ComboBox>().Single(item => item.Items.Contains("OnSong arrow shortcuts (foreground)"));
        var verify = button("Check navigation");
        File.WriteAllText(Path.Combine(dir, "track-navigation"), "");
        File.WriteAllText(Path.Combine(dir, "resolver-error"), "");
        verify.PerformClick();
        await Until(() => verify.Enabled, "failed resolver");
        const string automatic = "Automatic (OnSong links or song swipes)";
        const string swipe = "OnSong song swipes (foreground)";
        if ((string)mode.SelectedItem != automatic || button("Next song").Enabled) throw new Exception("Transport failure selected a fallback mode.");
        File.Delete(Path.Combine(dir, "resolver-error"));
        File.WriteAllText(Path.Combine(dir, "no-link"), "");
        File.WriteAllText(Path.Combine(dir, "other-foreground"), "");
        verify.PerformClick();
        await Until(() => verify.Enabled, "foreground guard");
        if ((string)mode.SelectedItem != automatic || button("Next song").Enabled) throw new Exception("Background app accepted for swipe navigation.");
        File.Delete(Path.Combine(dir, "other-foreground"));
        File.WriteAllText(Path.Combine(dir, "no-viewer"), "");
        verify.PerformClick();
        await Until(() => verify.Enabled, "viewer guard");
        if ((string)mode.SelectedItem != automatic || button("Next song").Enabled) throw new Exception("An OnSong settings screen accepted swipe navigation.");
        File.Delete(Path.Combine(dir, "no-viewer"));
        verify.PerformClick();
        await Until(() => verify.Enabled, "foreground swipe fallback");
        if ((string)mode.SelectedItem != swipe || !button("Next song").Enabled) throw new Exception("Missing song link did not select verified song swipes.");
        if (File.Exists(Path.Combine(dir, "dispatched-navigation"))) throw new Exception("A navigation check sent input to the tablet.");
        button("Next song").PerformClick();
        await Until(() => button("Next song").Enabled && File.Exists(Path.Combine(dir, "dispatched-navigation")), "foreground swipe dispatch");
        if (File.ReadAllText(Path.Combine(dir, "dispatched-navigation")).Trim() != "-s READY123 shell input swipe 1152 1200 448 1200 300")
            throw new Exception("The main window did not send exactly one forward swipe.");
        Console.WriteLine("PASS missing song link selects swipes only for a verified viewer; preflight sends nothing and manual Next swipes once.");

        string failureFlag = Path.Combine(dir, "discovery-error");
        File.WriteAllText(failureFlag, "");
        refresh.PerformClick();
        await Until(() => refresh.Enabled && !start.Enabled && !button("Next song").Enabled, "failed discovery clearing stale tablet");
        File.Delete(failureFlag);
        refresh.PerformClick();
        await Until(() => start.Enabled, "tablet rediscovery");
        start.PerformClick();
        button("Stop").PerformClick();
        await Until(() => start.Enabled && hasLabel("Cue audio off"), "canceled startup");

        start.PerformClick();
        await Until(() => controls.OfType<TextBox>().Any(item => item.Text.Contains("scrcpy started.")), "scrcpy launch");
        if (outputs.Items.Count > 0)
        {
            await Until(() => hasLabel("Waiting for playback"), "pending route without an active Windows stream");
            File.WriteAllText(Path.Combine(dir, "begin-playback"), "");
            await Until(() => hasLabel("Output unverified"), "detected stream without an expected output");
            // Exercise the actual picker. The default playback endpoint can differ between PCs.
            bool matched = false;
            for (int i = 0; i < outputs.Items.Count && !matched; i++)
            {
                outputs.SelectedIndex = i;
                await Until(() => hasLabel("Output matched") || hasLabel("Wrong cue output"), "route comparison");
                matched = hasLabel("Output matched");
            }
            if (!matched) throw new Exception("No listed output matched the silent playback stream.");
            Console.WriteLine("PASS live Windows route in the bridge UI on " + outputs.SelectedItem);
        }
        else
            Console.WriteLine("SKIP live Windows route in UI (no playback device).");

        File.WriteAllText(Path.Combine(dir, "disconnect"), "");
        await Until(() => hasLabel("Cue audio ended"), "simulated disconnect");
        refresh.PerformClick();
        await Until(() => refresh.Enabled && !start.Enabled, "disconnected device removed");
        File.Delete(Path.Combine(dir, "disconnect"));
        refresh.PerformClick();
        await Until(() => start.Enabled, "reconnected tablet");
        start.PerformClick();
        await Until(() => !hasLabel("Starting cue audio…") && button("Stop").Enabled, "second scrcpy launch");
        button("Stop").PerformClick();
        await Until(() => hasLabel("Cue audio off") && start.Enabled, "requested stop");
        if (button("Next song").Enabled) throw new Exception("Navigation verification survived a disconnect.");
        Console.WriteLine("PASS UI discovery failure/retry, manual next, startup cancellation, disconnect, reconnect and stop.");
    }

    private sealed class FakeWifi : WifiCueServices
    {
        internal int Finds, Installs, Starts, Stops;
        internal string Endpoint;
        internal NdiSource Source;
        internal readonly NdiReceiverStatus Status = new NdiReceiverStatus();
        internal List<NdiSource> Advertised = new List<NdiSource> {
            new NdiSource("TCL (Lyrics Projection)", "192.168.30.2:5961"),
            new NdiSource("iPad (Stage Monitor)", "192.168.30.3:5961") };
        internal TaskCompletionSource<bool> Starting, Stopping;
        internal readonly List<string> SearchedHosts = new List<string>();
        internal List<NdiSource> Direct = new List<NdiSource>();
        internal override string FindRuntime() { return Installs == 0 ? null : "official-runtime"; }
        internal override Task InstallAsync(CancellationToken token) { Installs++; return Task.FromResult(true); }
        internal override Task<List<NdiSource>> DiscoverAsync(CancellationToken token)
        { Finds++; return Task.FromResult(Advertised); }
        internal override Task<List<NdiSource>> DiscoverAtAsync(string ip, CancellationToken token)
        {
            if (FindRuntime() == null) throw new InvalidOperationException("Set up Wi-Fi playback before connecting by address.");
            SearchedHosts.Add(ip); return Task.FromResult(Direct);
        }
        internal override string DefaultOutput() { return "default-endpoint"; }
        internal override List<AudioDevice> Outputs()
        { return new List<AudioDevice> { new AudioDevice(0, "Fixture", "default-endpoint", "Fixture speakers"),
            new AudioDevice(1, "Fixture", "iem-endpoint", "Fixture mixer") }; }
        internal override Task StartAsync(string runtime, NdiSource source, string endpoint, CancellationToken token)
        { Starts++; Source = source; Endpoint = endpoint; Status.Running = true; return Starting == null ? Task.FromResult(true) : Starting.Task; }
        internal override NdiReceiverStatus GetStatus() { return Status; }
        internal override Task StopAsync()
        { Stops++; return Stopping == null ? Task.FromResult(true) : Stopping.Task; }
    }

    private static async Task ExerciseWifi(MainForm form, FakeWifi service, string dir)
    {
        var main = Children(form).ToArray();
        var open = main.OfType<Button>().Single(b => b.Text == "Find my tablet");
        var mirror = main.OfType<Button>().Single(b => b.Text == "Start cue audio");
        var tool = main.OfType<TextBox>().Single(box => box.Text == Path.Combine(dir, "scrcpy.exe"));
        string configured = tool.Text;
        tool.Text = Path.Combine(dir, "missing-scrcpy.exe");
        open.PerformClick();
        var dialog = form.OwnedForms.OfType<WifiCuesForm>().Single();
        dialog.Opacity = 0;
        var controls = Children(dialog).ToArray();
        Func<string, Button> button = name => controls.OfType<Button>().Single(b => b.Text == name);
        Func<string, ComboBox> picker = name => controls.OfType<ComboBox>().Single(b => b.AccessibleName == name);
        var source = picker("OnSong Wi-Fi source");
        var output = picker("Wi-Fi cue output");
        var start = button("Start Wi-Fi cues");
        var status = controls.OfType<Label>().Single(l => l.AccessibleRole == AccessibleRole.StatusBar);
        await Until(() => service.Finds > 0 && source.Items.Count == 2, "independent Wi-Fi discovery");
        if (service.Installs != 0 || service.Starts != 0 || source.SelectedItem != null || !button("Set up Wi-Fi playback").Visible)
            throw new Exception("Discovery required setup, auto-opened a source, or guessed between tablets.");
        source.SelectedItem = service.Advertised[0];
        var manualAddress = controls.OfType<TextBox>().Single(box => box.AccessibleName == "Tablet Wi-Fi IP address");
        var expand = controls.OfType<LinkLabel>().Single(link => link.Text == "Enter tablet address");
        if (manualAddress.Visible) throw new Exception("Address search expanded on first run.");
        ((IButtonControl)expand).PerformClick();
        manualAddress.Text = "192.168.30.2:5961";
        button("Find at address").PerformClick();
        if (service.SearchedHosts.Count != 0 || !status.Text.Contains("without a port"))
            throw new Exception("Invalid fallback host reached discovery.");
        manualAddress.Text = "192.168.30.2";
        button("Find at address").PerformClick();
        if (!status.Text.Contains("Set up Wi-Fi playback")) throw new Exception("Missing address lookup runtime left a dead end.");
        button("Set up Wi-Fi playback").PerformClick();
        service.Direct = new List<NdiSource> { service.Advertised[0] };
        button("Find at address").PerformClick();
        await Until(() => source.Items.Count == 1 && source.SelectedItem == service.Direct[0], "explicit tablet address");
        if (!service.SearchedHosts.SequenceEqual(new[] { "192.168.30.2" }) || service.Starts != 0)
            throw new Exception("Address lookup ignored the host or opened the media stream.");
        ((IButtonControl)expand).PerformClick();
        output.SelectedIndex = 1;
        await Until(() => start.Enabled, "playback runtime setup without scrcpy");
        service.Starting = new TaskCompletionSource<bool>();
        start.PerformClick();
        tool.Text = configured;
        if (start.Enabled || mirror.Enabled || service.Source != source.SelectedItem || service.Endpoint != "iem-endpoint")
            throw new Exception("Wi-Fi startup used the wrong route or allowed concurrent playback.");
        service.Starting.SetResult(true);
        service.Starting = null;
        open.PerformClick();
        if (form.OwnedForms.OfType<WifiCuesForm>().Count() != 1) throw new Exception("Opened two receiver windows.");
        service.Status.Connected = true;
        await Until(() => status.Text.Contains("Waiting for audio"), "connected source with no cue");
        if (Preferences.Load(Path.Combine(dir, "settings.xml")).WifiCueSourceAddress != null)
            throw new Exception("Saved an address before audio playback was verified.");
        service.Status.AudioFrames = 40; service.Status.Channels = 2; service.Status.SampleRate = 48000;
        service.Status.LastAudioUtc = DateTime.UtcNow;
        await Until(() => status.Text.Contains("Listening"), "silent audio frames");
        service.Status.Peak = 0.5; service.Status.PlayedFrames = 4800; service.Status.LastSignalUtc = DateTime.UtcNow;
        await Until(() => status.Text.Contains("Audio signal received"), "audible signal feedback");
        if (!status.AccessibilityObject.Name.Contains(status.Text))
            throw new Exception("The Wi-Fi status text was not exposed to assistive technology.");
        var level = controls.OfType<Label>().Single(l => l.AccessibleName == "Wi-Fi audio level");
        if (level.Text.Contains("dropped") || !level.AccessibleDescription.Contains("frames played"))
            throw new Exception("Missing playback counters or misleading warning for clean audio.");
        service.Status.DroppedAudioPackets = 3;
        await Until(() => level.Text.Contains("3 NDI audio packets dropped at receiver"), "NDI receiver queue loss");
        if (level.Text.Contains("output sample frames dropped") || !level.AccessibleDescription.Contains("3 audio packets dropped in the NDI receiver queue"))
            throw new Exception("Attributed receiver queue drops to the Windows output.");
        service.Status.DroppedFrames = 480;
        await Until(() => level.Text.Contains("480 output sample frames dropped") && level.Text.Contains("3 NDI audio packets dropped at receiver"),
            "independent NDI and output loss indicators");
        service.Status.LastSignalUtc = DateTime.UtcNow.AddMinutes(-3);
        service.Status.LastAudioUtc = service.Status.LastSignalUtc;
        await Until(() => status.Text.Contains("Ready for the next cue"), "long silence between cues");
        service.Stopping = new TaskCompletionSource<bool>();
        dialog.Close();
        await Until(() => service.Stops == 1, "receiver cleanup after window close");
        if (!dialog.Visible || mirror.Enabled) throw new Exception("Released playback controls before the receiver stopped.");
        service.Stopping.SetResult(true);
        service.Stopping = null;
        await Until(() => dialog.IsDisposed && mirror.Enabled, "receiver close restores mirrored playback");
        var saved = Preferences.Load(Path.Combine(dir, "settings.xml"));
        if (saved.WifiCueOutputEndpointId != "iem-endpoint" || saved.WifiCueSource != service.Source.Name ||
            saved.WifiCueSourceAddress != "192.168.30.2:5961")
            throw new Exception("Selected source and actual mixer endpoint were not saved.");

        var separate = new FakeWifi { Installs = 1 };
        separate.Advertised.Clear();
        separate.Direct = new List<NdiSource> { new NdiSource(service.Source.Name, "192.168.30.2:6329") };
        using (var reopened = new WifiCuesForm(Preferences.Load(Path.Combine(dir, "settings.xml")), separate, () => true, () => null))
        {
            reopened.Opacity = 0; reopened.Show(form);
            await Until(() => Children(reopened).OfType<ComboBox>().Any(c => c.SelectedItem == separate.Direct[0]), "saved host after a fresh settings load");
            if (separate.Starts != 0 || separate.Finds != 0 || !separate.SearchedHosts.SequenceEqual(new[] { "192.168.30.2" }))
                throw new Exception("Returning tablet lookup waited for multicast, opened playback, or missed the changed port.");
            reopened.Close();
        }

        service.Advertised = new List<NdiSource> { new NdiSource(service.Source.Name, "192.168.30.8:5961") };
        service.Direct = new List<NdiSource> { new NdiSource(service.Source.Name, "192.168.30.2:6327") };
        mirror.PerformClick();
        await Until(() => main.OfType<Button>().Single(b => b.Text == "Stop").Enabled, "mirrored audio starting");
        open.PerformClick();
        dialog = form.OwnedForms.OfType<WifiCuesForm>().Single();
        dialog.Opacity = 0;
        controls = Children(dialog).ToArray();
        await Until(() => picker("OnSong Wi-Fi source").SelectedItem == service.Direct[0], "remembered host recovered new media port");
        if (picker("OnSong Wi-Fi source").Items.Count != 1)
            throw new Exception("Successful fast lookup included unrelated network sources.");
        if (!service.SearchedHosts.SequenceEqual(new[] { "192.168.30.2", "192.168.30.2" }))
            throw new Exception("Fallback reused an old media endpoint or probed an unrelated source.");
        service.Advertised.Add(service.Direct[0]);
        int fullSearches = service.Finds;
        button("Find my tablet").PerformClick();
        await Until(() => picker("OnSong Wi-Fi source").SelectedItem == service.Direct[0], "unique name and endpoint preserved");
        var duplicates = picker("OnSong Wi-Fi source");
        if (service.Finds != fullSearches + 1 || duplicates.Items.Count != 2 || service.SearchedHosts.Count != 2)
            throw new Exception("Explicit refresh skipped the full network or redundantly probed the saved host.");
        foreach (NdiSource advertised in duplicates.Items)
            if (!duplicates.GetItemText(advertised).Contains(advertised.Address))
                throw new Exception("Same-name tablet streams did not display their distinct addresses.");
        if (button("Start Wi-Fi cues").Enabled || service.Starts != 1)
            throw new Exception("Wi-Fi cues overlapped Android mirrored playback.");
        main.OfType<Button>().Single(b => b.Text == "Stop").PerformClick();
        await Until(() => button("Start Wi-Fi cues").Enabled, "native playback after mirror stop");
        service.Starting = new TaskCompletionSource<bool>();
        button("Start Wi-Fi cues").PerformClick();
        dialog.Close();
        service.Starting.SetResult(true);
        await Until(() => dialog.IsDisposed && service.Stops == 2, "closing during Wi-Fi receiver startup");
        service.Starting = null;
        service.Advertised = new List<NdiSource> { new NdiSource("Other Camera", "192.168.30.8:5961"),
            new NdiSource("New tablet (Stage Monitor)", "192.168.30.9:5961") };
        service.Direct = new List<NdiSource> { new NdiSource("Replaced stream", "192.168.30.2:6230") };
        int hostSearches = service.SearchedHosts.Count;
        fullSearches = service.Finds;
        open.PerformClick();
        dialog = form.OwnedForms.OfType<WifiCuesForm>().Single();
        dialog.Opacity = 0;
        controls = Children(dialog).ToArray();
        await Until(() => button("Start Wi-Fi cues").Enabled, "restored receiver output");
        if (picker("OnSong Wi-Fi source").SelectedItem != service.Advertised[1]) throw new Exception("Missed the single likely tablet.");
        if (service.SearchedHosts.Count != hostSearches + 1 || service.Finds != fullSearches + 1)
            throw new Exception("Saved host with a different stream skipped fallback discovery or was checked twice.");
        button("Start Wi-Fi cues").PerformClick();
        service.Stopping = new TaskCompletionSource<bool>();
        form.Close();
        await Until(() => service.Stops == 3, "owner waits for Wi-Fi cleanup");
        if (!form.Visible || !dialog.Visible) throw new Exception("Owner disappeared while the receiver still held its output.");
        service.Stopping.SetResult(true);
        Console.WriteLine("PASS independent first-run Wi-Fi discovery/setup, explicit routing, signal and silent feedback, mutual playback exclusion, close and cancellation.");
    }

    [STAThread]
    public static int Main()
    {
        string dir = AppDomain.CurrentDomain.BaseDirectory;
        string settingsPath = Path.Combine(dir, "settings.xml");
        // Supply a v0.1.0-style file without the new endpoint element.
        File.WriteAllText(settingsPath, "<Preferences><BufferMs>75</BufferMs><Serial>READY123</Serial></Preferences>");
        Preferences settings = Preferences.Load(settingsPath);
        settings.ScrcpyPath = Path.Combine(dir, "scrcpy.exe");
        WriteSilentWave(Path.Combine(dir, "silence.wav"));
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Exception failure = null;
        var wifiService = new FakeWifi();
        using (var form = new MainForm(settings, () => wifiService))
        {
            form.ShowInTaskbar = false;
            form.Opacity = 0;
            form.Shown += async delegate
            {
                try { await Exercise(form, dir); await ExerciseWifi(form, wifiService, dir); }
                catch (Exception error) { failure = error; }
                finally { form.Close(); }
            };
            Application.Run(form);
        }
        if (failure != null) { Console.Error.WriteLine(failure); return 1; }
        var saved = Preferences.Load(settingsPath);
        if (saved.Serial != "READY123" || (saved.OutputName != null && String.IsNullOrEmpty(saved.OutputEndpointId)))
            throw new Exception("Selected tablet or playback endpoint was not saved.");
        Console.WriteLine("PASS v0.1.0 preferences read and current settings persisted.");
        return 0;
    }
}

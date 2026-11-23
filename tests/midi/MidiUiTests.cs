using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Speech.Recognition;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using PcOnSongBridge;

internal static class MidiUiTests
{
    private static void Check(bool ok, string detail) { if (!ok) throw new Exception(detail); }
    private static IEnumerable<Control> Children(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (var nested in Children(child)) yield return nested;
        }
    }
    private static async Task WaitFor(Func<bool> condition, string detail)
    {
        var timer = Stopwatch.StartNew();
        while (!condition() && timer.ElapsedMilliseconds < 4000) await Task.Delay(10);
        Check(condition(), detail);
    }

    private sealed class Mic
    {
        internal readonly AudioDevice Device = new AudioDevice(0, "Fixture talkback");
        internal CancellationToken Token;
        internal int Sessions;
        private Func<MicrophoneResult, CancellationToken, Task> dispatch;
        internal async Task Open(AudioDevice source, RecognizerInfo engine, VoiceCommand[] commands, Action ready,
            Func<MicrophoneResult, CancellationToken, Task> send, Action<MicrophoneResult> unmatched, CancellationToken token)
        {
            Check(source == Device && commands.Select(c => c.Id).SequenceEqual(new[] { "next", "previous" }), "Wrong MIDI input or phrases.");
            Sessions++;
            Token = token;
            dispatch = send;
            ready();
            await Task.Delay(Timeout.Infinite, token);
        }
        internal Task Say(string phrase)
        {
            return dispatch(new MicrophoneResult(-20, phrase, speechEndTimestamp: Stopwatch.GetTimestamp()), Token);
        }
        internal async Task AssertStopped()
        {
            Check(Token.IsCancellationRequested, "The MIDI listener kept its mic open.");
            try { await Say("next"); throw new Exception("A stopped listener accepted a stale command."); }
            catch (OperationCanceledException) { }
        }
    }

    [STAThread]
    private static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        var file = Path.Combine(Path.GetTempPath(), "MidiUiPrefs-" + Guid.NewGuid().ToString("N") + ".xml");
        try
        {
            using (var tablet = new MidiTests.Tablet())
            {
                var mic = new Mic();
                var prefs = Preferences.Load(file);
                var peers = new List<MidiPeer> { tablet.Peer, new MidiPeer("Second fixture", "127.0.0.1", 5006) };
                using (var form = new WirelessMidiForm(prefs, null, token => Task.FromResult(peers), mic.Open))
                {
                    form.ShowInTaskbar = false;
                    form.Opacity = 0;
                    Exception failure = null;
                    form.Shown += async delegate
                    {
                        try { await Exercise(form, file, tablet, mic); }
                        catch (Exception ex) { failure = ex; }
                        finally { form.Close(); }
                    };
                    Application.Run(form);
                    if (failure != null) throw new InvalidOperationException("Wireless MIDI UI failed.", failure);
                }
                tablet.Bye();
                // Remote clock sync and session teardown may appear later. During this short test
                // every outgoing RTP-MIDI command must already have been consumed by ReceiveCC.
                while (tablet.Data.Client.Poll(30000, SelectMode.SelectRead))
                {
                    byte[] packet = tablet.Data.Receive(ref tablet.PcData);
                    Check(packet.Length >= 4 && packet[0] == 255 && packet[1] == 255, "A stale or duplicate MIDI action reached the tablet.");
                }
            }
        }
        finally { File.Delete(file); }
        Console.WriteLine("PASS MIDI tablet choice, learned mappings, continuous voice dispatch, cancellation, restart and close over loopback.");
    }

    private static async Task Exercise(WirelessMidiForm form, string file, MidiTests.Tablet tablet, Mic mic)
    {
        Control[] controls = Children(form).ToArray();
        Func<string, Button> button = name => controls.OfType<Button>().Single(b => b.Text == name);
        var peers = controls.OfType<ComboBox>().Single(x => x.AccessibleName == "MIDI tablet");
        var inputs = controls.OfType<ComboBox>().Single(x => x.AccessibleName == "MIDI command microphone");
        var channel = controls.OfType<NumericUpDown>().Single(x => x.AccessibleName == "MIDI channel");
        var nextCc = controls.OfType<NumericUpDown>().Single(x => x.AccessibleName == "Next song controller");
        var prevCc = controls.OfType<NumericUpDown>().Single(x => x.AccessibleName == "Previous song controller");
        var status = controls.OfType<Label>().Single(x => x.AccessibleRole == AccessibleRole.StatusBar);
        var log = controls.OfType<TextBox>().Single(x => x.AccessibleName == "MIDI activity");
        var connect = button("Connect");
        var next = button("Next song");
        var previous = button("Previous song");
        var listen = button("Start listening");
        await WaitFor(() => peers.Items.Count == 2, "Discovery did not finish.");
        Check(peers.SelectedItem == null && !connect.Enabled && !listen.Enabled && !next.Enabled, "A tablet was guessed or armed during discovery.");
        peers.SelectedItem = tablet.Peer;
        inputs.Items.Add(mic.Device);
        inputs.SelectedItem = mic.Device;
        var handshake = Task.Run(() => { tablet.ControlInvite(true); tablet.DataInvite(true); tablet.Sync(); });
        connect.PerformClick();
        await handshake;
        await WaitFor(() => next.Enabled && listen.Enabled, "Connected tablet did not enable MIDI commands.");
        Check(!peers.Enabled && connect.Text == "Disconnect", "Connected tablet could change under the session.");
        channel.Value = 6;
        nextCc.Value = prevCc.Value;
        Check(!listen.Enabled && !next.Enabled && !previous.Enabled, "Identical navigation controllers were armed.");
        nextCc.Value = 102;
        previous.PerformClick();
        await Task.Run(() => tablet.ReceiveCC(6, 111, 127));
        listen.PerformClick();
        await WaitFor(() => listen.Text == "Stop listening" && status.Text.StartsWith("Listening"), "Microphone never became ready.");
        Check(!next.Enabled && !channel.Enabled && !inputs.Enabled, "Mapping or manual send remained active during listening.");
        await mic.Say("next");
        await mic.Say("previous");
        await Task.Run(() => {
            int first = tablet.ReceiveCC(6, 102, 127);
            Check(tablet.ReceiveCC(6, 111, 127) == ((first + 1) & 65535), "Missing or reordered MIDI voice commands.");
        });
        Check(mic.Sessions == 1 && log.Text.Contains("after detected speech end"), "Lost continuous listening or mislabeled timing.");
        listen.PerformClick();
        await WaitFor(() => listen.Text == "Start listening" && previous.Enabled, "Stop failed to release the mic.");
        await mic.AssertStopped();
        var saved = Preferences.Load(file);
        Check(saved.MidiPeerName == tablet.Peer.Name && saved.MidiNextController == 102 && saved.MidiPreviousController == 111 &&
            saved.MidiChannel == 6, "The selected MIDI mapping was not saved.");
        listen.PerformClick();
        await WaitFor(() => mic.Sessions == 2, "Cannot restart MIDI capture.");
        form.Close();
        await mic.AssertStopped();
    }
}

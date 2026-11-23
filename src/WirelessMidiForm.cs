using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Speech.Recognition;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PcOnSongBridge
{
    internal sealed class WirelessMidiForm : BridgeWindow
    {
        private readonly Preferences preferences;
        private readonly Func<CancellationToken, Task<List<MidiPeer>>> discover;
        private readonly HandsFreeSession voice;
        private readonly ComboBox peers = Picker("MIDI tablet");
        private readonly ComboBox inputs = Picker("MIDI command microphone");
        private readonly Button find = ActionButton("Find MIDI tablets"), connect = ActionButton("Connect");
        private readonly Button next = ActionButton("Next song"), previous = ActionButton("Previous song");
        private readonly Button listen = ActionButton("Start listening");
        private readonly NumericUpDown channel, nextCc, previousCc;
        private readonly Label status = Copy("");
        private readonly TextBox history = new TextBox { AccessibleName = "MIDI activity", ReadOnly = true,
            Multiline = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Top, Height = 88 };
        private readonly System.Windows.Forms.Timer health = new System.Windows.Forms.Timer { Interval = 1000 };
        private RecognizerInfo recognizer;
        private MidiSession session;
        private CancellationTokenSource operation;
        private bool listening, closing;

        internal WirelessMidiForm(Preferences preferences, AudioDevice preferred,
            Func<CancellationToken, Task<List<MidiPeer>>> discover = null, HandsFreeSession voice = null)
        {
            this.preferences = preferences;
            this.discover = discover ?? MidiDiscovery.FindAsync;
            this.voice = voice;
            Text = "Wireless MIDI";
            Font = new Font("Segoe UI", 10f);
            BackColor = SystemColors.Window;
            ForeColor = SystemColors.WindowText;
            AutoScaleMode = AutoScaleMode.Dpi;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(565, 485);
            MinimumSize = new Size(410, 365);

            channel = Number("MIDI channel", 1, 16, preferences.MidiChannel, 16);
            nextCc = Number("Next song controller", 0, 119, preferences.MidiNextController, 110);
            previousCc = Number("Previous song controller", 0, 119, preferences.MidiPreviousController, 111);
            var panel = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), AutoScroll = true, ColumnCount = 1 };
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            Controls.Add(panel);
            var heading = Copy("MIDI tablet");
            heading.Font = new Font(Font, FontStyle.Bold);
            Add(panel, heading); Add(panel, peers); Add(panel, Row(find, connect));
            Add(panel, Copy("In OnSong, open Utilities → Editors → MIDI. Use the song buttons below to teach two triggers, then assign Next Song and Previous Song."));
            Add(panel, Row(Copy("Channel"), channel, Copy("Next CC"), nextCc, Copy("Previous CC"), previousCc));
            Add(panel, Row(next, previous));
            Add(panel, Copy("Command microphone")); Add(panel, inputs); Add(panel, Row(listen));
            status.AccessibleRole = AccessibleRole.StatusBar;
            Add(panel, status); Add(panel, history);
            panel.SizeChanged += delegate { foreach (var l in panel.Controls.OfType<Label>()) l.MaximumSize = new Size(Math.Max(180, panel.ClientSize.Width - 55), 0); };

            find.Click += async delegate { await Find(); };
            connect.Click += async delegate
            {
                if (session != null) { Disconnect(); status.Text = "Disconnected."; EnableControls(); return; }
                var peer = peers.SelectedItem as MidiPeer;
                if (peer == null) return;
                await Run(async token =>
                {
                    status.Text = "Connecting to " + peer.Name + "…";
                    session = await MidiSession.ConnectAsync(peer, token);
                    token.ThrowIfCancellationRequested();
                    preferences.MidiPeerName = peer.Name;
                    SaveMapping();
                    status.Text = "Connected to " + peer.Name + ".";
                });
            };
            peers.SelectedIndexChanged += delegate { EnableControls(); };
            inputs.SelectedIndexChanged += delegate { EnableControls(); };
            foreach (var n in new[] { channel, nextCc, previousCc }) n.ValueChanged += delegate
            {
                if (nextCc.Value == previousCc.Value) status.Text = "Use different controllers for next and previous.";
                else { SaveMapping(); status.Text = session == null ? "Choose a tablet and connect." : "MIDI mapping updated."; }
                EnableControls();
            };
            next.Click += delegate { Manual("next"); };
            previous.Click += delegate { Manual("previous"); };
            listen.Click += async delegate
            {
                if (listening && operation != null) { operation.Cancel(); EnableControls(); return; }
                await Listen();
            };
            health.Tick += delegate
            {
                if (session == null || session.IsConnected) return;
                if (operation != null) operation.Cancel();
                Disconnect(); status.Text = "MIDI connection lost. Reconnect to your tablet."; EnableControls();
            };
            Shown += async delegate
            {
                try
                {
                    var devices = Audio.GetInputs();
                    recognizer = Audio.FindEnglishRecognizer();
                    inputs.Items.AddRange(devices.Cast<object>().ToArray());
                    inputs.SelectedItem = devices.FirstOrDefault(d => preferred != null && (preferred.EndpointId != null
                        ? d.EndpointId == preferred.EndpointId : d.Name == preferred.Name))
                        ?? (devices.Count == 1 ? devices[0] : null);
                }
                catch (Exception error) { Log("Microphone unavailable. " + error.Message); }
                health.Start();
                await Find();
            };
            EnableControls();
        }

        private static Label Copy(string text) { return new Label { Text = text, AutoSize = true, Margin = new Padding(3, 5, 3, 4) }; }
        private static ComboBox Picker(string name) { return new ComboBox { AccessibleName = name, DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Top }; }
        private static Button ActionButton(string text) { return new Button { Text = text, AutoSize = true, MinimumSize = new Size(0, 30) }; }
        private static NumericUpDown Number(string name, int min, int max, int saved, int fallback)
        {
            return new NumericUpDown { AccessibleName = name, Minimum = min, Maximum = max,
                Value = saved >= min && saved <= max ? saved : fallback, Width = 59 };
        }
        private static FlowLayoutPanel Row(params Control[] controls)
        {
            var row = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true, Margin = Padding.Empty };
            row.Controls.AddRange(controls); return row;
        }
        private static void Add(TableLayoutPanel panel, Control control)
        {
            int index = panel.RowCount++;
            panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            panel.Controls.Add(control, 0, index);
        }

        private Task Find()
        {
            return Run(async token =>
            {
                peers.Items.Clear(); status.Text = "Finding MIDI tablets…";
                var candidates = await discover(token);
                token.ThrowIfCancellationRequested();
                peers.Items.AddRange(candidates.Cast<object>().ToArray());
                var saved = candidates.Where(p => p.Name == preferences.MidiPeerName).ToList();
                if (candidates.Count == 1) peers.SelectedIndex = 0;
                else if (saved.Count == 1) peers.SelectedItem = saved[0];
                status.Text = candidates.Count == 0 ? "No MIDI tablets found. On the same Wi-Fi, enable MIDI and Network Session in OnSong’s MIDI settings, then search again." : "Choose your tablet and connect.";
            });
        }

        private void SaveMapping()
        {
            preferences.MidiChannel = (int)channel.Value;
            preferences.MidiNextController = (int)nextCc.Value;
            preferences.MidiPreviousController = (int)previousCc.Value;
            try { preferences.Save(); } catch (Exception error) { Log("Could not save MIDI settings. " + error.Message); }
        }

        private void Send(string id, MicrophoneResult sample, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (closing || session == null || !session.IsConnected) throw new InvalidOperationException("Reconnect the MIDI tablet.");
            if (id != "next" && id != "previous") throw new InvalidOperationException("Unknown MIDI song command.");
            int controller = (int)(id == "next" ? nextCc.Value : previousCc.Value);
            session.SendControlChange((int)channel.Value, controller, 127, token);
            long sent = Stopwatch.GetTimestamp();
            string timing = sample != null && sample.SpeechEndTimestamp.HasValue ? String.Format(" · {0:0} ms after detected speech end",
                (sent - sample.SpeechEndTimestamp.Value) * 1000.0 / Stopwatch.Frequency) : "";
            Log((id == "next" ? "Next song" : "Previous song") + " · MIDI sent on channel " + channel.Value + ", CC " + controller + timing);
        }

        private void Manual(string id)
        {
            if (operation != null) return;
            try { Send(id, null, CancellationToken.None); }
            catch (Exception error) { status.Text = error.Message; EnableControls(); }
        }

        private async Task Listen()
        {
            if (operation != null || session == null || !session.IsConnected) return;
            var mic = inputs.SelectedItem as AudioDevice;
            if (mic == null) return;
            listening = true;
            await Run(async token =>
            {
                status.Text = "Opening microphone…";
                var run = voice ?? Audio.HandsFreeSessionFor(preferences.StandaloneCommandsOnly);
                await run(mic, recognizer, VoiceCommands.SongCommands,
                    () => { if (!token.IsCancellationRequested) status.Text = "Listening on " + mic.DisplayName + "."; },
                    (sample, cancel) => { token.ThrowIfCancellationRequested(); Send(sample.Command, sample, cancel); return Task.FromResult(0); },
                    ignored => { }, token);
            });
        }

        private async Task Run(Func<CancellationToken, Task> action)
        {
            if (closing || operation != null) return;
            using (var current = operation = new CancellationTokenSource())
            {
                EnableControls();
                try { await action(current.Token); }
                catch (OperationCanceledException) { if (!closing && session != null) status.Text = "Listening stopped."; }
                catch (Exception error) { if (!closing) { status.Text = error.Message; Log(error.Message); } }
                finally
                {
                    listening = false; operation = null;
                    if (!IsDisposed) { EnableControls(); if (closing && IsHandleCreated) BeginInvoke(new Action(Close)); }
                }
            }
        }

        private void Log(string text)
        {
            if (history.Lines.Length >= 60) history.Lines = history.Lines.Skip(20).ToArray();
            history.AppendText(DateTime.Now.ToString("HH:mm:ss.fff") + "  " + text + Environment.NewLine);
        }

        private void EnableControls()
        {
            bool idle = !closing && operation == null;
            bool connected = session != null && session.IsConnected;
            peers.Enabled = find.Enabled = idle && session == null;
            connect.Text = session == null ? "Connect" : "Disconnect";
            connect.Enabled = idle && (session != null || peers.SelectedItem != null);
            channel.Enabled = nextCc.Enabled = previousCc.Enabled = inputs.Enabled = idle;
            next.Enabled = previous.Enabled = idle && connected && nextCc.Value != previousCc.Value;
            listen.Enabled = listening ? !closing && operation != null && !operation.IsCancellationRequested :
                next.Enabled && inputs.SelectedItem != null && (recognizer != null || voice != null);
            listen.Text = listening ? (operation != null && operation.IsCancellationRequested ? "Stopping…" : "Stop listening") : "Start listening";
        }

        private void Disconnect() { if (session != null) session.Dispose(); session = null; }
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            closing = true;
            health.Stop();
            if (operation != null) { e.Cancel = true; operation.Cancel(); }
            base.OnFormClosing(e);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { closing = true; health.Dispose(); if (operation != null) operation.Cancel(); Disconnect(); }
            base.Dispose(disposing);
        }
    }
}

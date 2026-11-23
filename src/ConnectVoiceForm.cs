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
    internal sealed class ConnectVoiceForm : Form
    {
        private readonly TextBox address = new TextBox();
        private readonly ComboBox microphone = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
        private readonly Button refreshMic = new Button { Text = "Refresh" };
        private readonly Button check = new Button { Text = "Check tablet" };
        private readonly Button pair = new Button { Text = "Pair with tablet" };
        private readonly Button developerKey = new Button { Text = "Developer key…" };
        private readonly Button listen = new Button { Text = "Start listening" };
        private readonly Label status = new Label { AutoSize = true, MaximumSize = new Size(520, 0) };
        private readonly Label speechHint = new Label { AutoSize = true, MaximumSize = new Size(520, 0),
            AccessibleName = "Microphone availability", AccessibleRole = AccessibleRole.StatusBar };
        private readonly FlowLayoutPanel manual = new FlowLayoutPanel { AutoSize = true, WrapContents = true };
        private readonly VoiceCommand[] commands = VoiceCommands.ForMode(TabletControlMode.Automatic, null).ToArray();
        private bool keyAvailable;
        private ConnectSession session;
        private RecognizerInfo recognizer;
        private CancellationTokenSource operation;
        private bool listening, closing, inputsUnavailable;

        internal ConnectVoiceForm(string tabletAddress, AudioDevice selectedMicrophone, ConnectKeyStore keyStore = null,
            HandsFreeSession listenSession = null, Func<string, ConnectKeyStore, ConnectSession> openSession = null,
            Preferences preferences = null)
        {
            Text = "Wi-Fi voice control";
            AutoScaleMode = AutoScaleMode.Dpi;
            Font = new Font("Segoe UI", 10f);
            BackColor = Color.FromArgb(26, 32, 40);
            ForeColor = Color.WhiteSmoke;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            ShowInTaskbar = false;
            MinimumSize = new Size(540, 430);
            ClientSize = new Size(588, 468);
            keyStore = keyStore ?? new ConnectKeyStore();
            preferences = preferences ?? Preferences.Load();
            openSession = openSession ?? ConnectSession.Open;
            keyAvailable = ConnectSession.ConfiguredKey(keyStore) != null;
            var panel = new TableLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(18), ColumnCount = 1 };
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            Controls.Add(panel);
            AddRow(panel, new Label { Text = "OnSong Connect", AutoSize = true, Font = new Font(Font, FontStyle.Bold) });
            AddRow(panel, new Label { AutoSize = true, MaximumSize = new Size(520, 0),
                Text = "Use the tablet’s private Wi-Fi address on a trusted network. OnSong Connect sends credentials over local HTTP." });
            AddRow(panel, new Label { Text = "Tablet Wi-Fi address", AutoSize = true });
            address.Text = tabletAddress ?? "";
            address.AccessibleName = "Tablet Wi-Fi address";
            address.Dock = DockStyle.Top;
            AddRow(panel, address);
            var pairing = new FlowLayoutPanel { AutoSize = true };
            StyleButton(check); StyleButton(pair); StyleButton(developerKey);
            pairing.Controls.AddRange(new Control[] { check, pair, developerKey });
            AddRow(panel, pairing);
            AddRow(panel, new Label { Text = "Command microphone", AutoSize = true });
            microphone.Dock = DockStyle.Top;
            microphone.AccessibleName = "Command microphone";
            StyleButton(refreshMic);
            refreshMic.AccessibleName = "Refresh microphones";
            var micRow = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Dock = DockStyle.Top, Margin = Padding.Empty };
            micRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            micRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            microphone.Margin = new Padding(0, 4, 8, 0);
            micRow.Controls.Add(microphone, 0, 0);
            micRow.Controls.Add(refreshMic, 1, 0);
            AddRow(panel, micRow);
            AddRow(panel, speechHint);
            StyleButton(listen);
            AddRow(panel, listen);
            foreach (VoiceCommand command in commands)
            {
                var action = new Button { Text = command.Title, AccessibleName = command.Title };
                StyleButton(action);
                action.Click += async (s, e) => await Run(async token => status.Text = (await session.SendAsync(command.Id, token)).Message);
                manual.Controls.Add(action);
            }
            AddRow(panel, manual);
            status.AccessibleRole = AccessibleRole.StatusBar;
            status.TextChanged += delegate { status.AccessibleName = "Wi-Fi voice control. " + status.Text; };
            AddRow(panel, status);
            var close = new Button { Text = "Close", DialogResult = DialogResult.Cancel };
            StyleButton(close);
            AddRow(panel, close);
            CancelButton = close;
            Shown += delegate { RefreshMicrophones(selectedMicrophone); };
            refreshMic.Click += delegate { RefreshMicrophones(microphone.SelectedItem as AudioDevice); };
            address.TextChanged += delegate
            {
                if (session != null) session.Dispose();
                session = null;
                status.Text = InitialStatus();
                UpdateEnabled();
            };
            microphone.SelectedIndexChanged += delegate { UpdateEnabled(); };
            developerKey.Click += delegate
            {
                using (var dialog = new ConnectKeyForm(keyAvailable, keyStore))
                {
                    if (dialog.ShowDialog(this) != DialogResult.OK) return;
                }
                if (session != null) session.Dispose();
                session = null;
                keyAvailable = true;
                status.Text = "Key saved. Choose Check tablet, then pair with your tablet.";
                UpdateEnabled();
            };
            check.Click += async (s, e) => await Run(async token =>
            {
                if (session != null) session.Dispose();
                session = null;
                session = openSession(address.Text.Trim(), keyStore);
                string identity = await session.IdentifyAsync(token);
                status.Text = "Found " + identity + ". " + (keyAvailable ? "Confirm it is your tablet, then pair." : KeyNeeded);
            });
            pair.Click += async (s, e) => await Run(async token =>
            {
                await session.AuthorizeAsync(token);
                status.Text = "Paired with " + session.TabletName + ". Choose Start listening to use the microphone until you stop it.";
            });
            listen.Click += async (s, e) =>
            {
                if (operation != null)
                {
                    if (listening) { operation.Cancel(); UpdateEnabled(); }
                    return;
                }
                listening = true;
                bool standalone = preferences.StandaloneCommandsOnly;
                await Run(token => Listen(listenSession ?? Audio.HandsFreeSessionFor(standalone), standalone, token));
            };
            status.Text = InitialStatus();
            UpdateEnabled();
        }

        private const string KeyNeeded = "Choose Developer key to set up Wi-Fi voice control. Wi-Fi cue audio works independently.";
        private string InitialStatus() { return keyAvailable ? "Check the address, then pair with the tablet on your trusted Wi-Fi network." : KeyNeeded; }

        private void RefreshMicrophones(AudioDevice preferred)
        {
            try
            {
                SetSpeechInputs(Audio.GetInputs(), Audio.FindEnglishRecognizer(), preferred);
            }
            catch (Exception) { SetSpeechInputs(null, null, null); }
        }

        internal void SetSpeechInputs(List<AudioDevice> devices, RecognizerInfo availableRecognizer, AudioDevice preferred)
        {
            recognizer = availableRecognizer;
            inputsUnavailable = devices == null;
            microphone.Items.Clear();
            if (devices != null)
            {
                microphone.Items.AddRange(devices.Cast<object>().ToArray());
                var desired = preferred == null ? null : devices.FirstOrDefault(d =>
                    preferred.EndpointId != null ? d.EndpointId == preferred.EndpointId :
                    d.Id == preferred.Id && d.Name == preferred.Name);
                microphone.SelectedItem = desired ?? (devices.Count == 1 ? devices[0] : null);
            }
            UpdateEnabled();
        }

        private string SpeechHint()
        {
            if (!keyAvailable) return "";
            if (inputsUnavailable)
                return "Windows could not list speech inputs. Click Refresh to try again.";
            if (recognizer == null)
                return "Windows has no English desktop speech recognizer. Add English speech in Windows language settings and click Refresh.";
            if (microphone.Items.Count == 0)
                return "No Windows recording input was found. Connect a microphone and click Refresh.";
            return microphone.SelectedItem == null ? "Choose your command microphone before listening." : "";
        }

        private static void AddRow(TableLayoutPanel panel, Control control)
        {
            int row = panel.RowCount++;
            panel.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            control.Margin = new Padding(2, 3, 2, 4);
            panel.Controls.Add(control, 0, row);
        }

        private static void StyleButton(Button button)
        {
            button.AutoSize = true;
            button.MinimumSize = new Size(0, 32);
            button.FlatStyle = FlatStyle.Flat;
            button.BackColor = Color.FromArgb(42, 55, 69);
            button.ForeColor = Color.WhiteSmoke;
        }

        private void UpdateEnabled()
        {
            bool idle = operation == null;
            bool approved = session != null && session.Approved;
            check.Enabled = idle && !String.IsNullOrWhiteSpace(address.Text);
            pair.Enabled = idle && keyAvailable && session != null && session.TabletName != null && !approved;
            developerKey.Enabled = idle;
            address.Enabled = microphone.Enabled = refreshMic.Enabled = idle;
            manual.Enabled = idle && approved;
            bool stopping = listening && operation != null && operation.IsCancellationRequested;
            listen.Enabled = !closing && !stopping && (listening || (idle && approved && recognizer != null && microphone.SelectedItem is AudioDevice));
            listen.Text = stopping ? "Stopping…" : listening ? "Stop listening" : "Start listening";
            if (!listening) SetSpeechHint(SpeechHint());
        }

        private void SetSpeechHint(string text)
        {
            speechHint.Text = text;
            speechHint.Visible = text.Length != 0;
        }

        private bool IsCurrentListener(CancellationToken token)
        {
            return !closing && !IsDisposed && listening && operation != null && operation.Token == token && !token.IsCancellationRequested;
        }

        private Task Listen(HandsFreeSession run, bool standalone, CancellationToken token)
        {
            string readyHint = "Listening for next song, previous song, scroll up and scroll down." +
                (standalone ? " Speak each command by itself." : "");
            SetSpeechHint("Opening the command microphone…");
            status.Text = "";
            return run((AudioDevice)microphone.SelectedItem, recognizer, commands,
                () =>
                {
                    if (IsCurrentListener(token)) SetSpeechHint(readyHint);
                },
                async (sample, cancel) =>
                {
                    token.ThrowIfCancellationRequested();
                    cancel.ThrowIfCancellationRequested();
                    string phrase = commands.Single(c => c.Id == sample.Command).Phrase;
                    status.Text = "Sending “" + phrase + "”…";
                    SetSpeechHint(readyHint);
                    ConnectCommandResult reply;
                    try { reply = await session.SendAsync(sample.Command, cancel); }
                    catch (InvalidOperationException error)
                    {
                        throw new InvalidOperationException("Heard “" + phrase + "”. " + error.Message, error);
                    }
                    if (IsCurrentListener(token)) status.Text = "“" + phrase + "” · " + FormatReply(sample, reply);
                },
                sample =>
                {
                    if (IsCurrentListener(token) && !Double.IsNegativeInfinity(sample.PeakDbFs))
                        SetSpeechHint("Listening. " + Audio.NoCommandMessage(sample.PeakDbFs));
                }, token);
        }

        internal static string FormatReply(MicrophoneResult sample, ConnectCommandResult reply)
        {
            if (!sample.SpeechEndTimestamp.HasValue || !reply.ConfirmedAt.HasValue) return reply.Message;
            double elapsedMs = (reply.ConfirmedAt.Value - sample.SpeechEndTimestamp.Value) * 1000.0 / Stopwatch.Frequency;
            return String.Format("{0} Tablet confirmation received {1:N0} ms after speech ended.", reply.Message, elapsedMs);
        }

        private async Task Run(Func<CancellationToken, Task> action)
        {
            if (operation != null) return;
            using (var current = operation = new CancellationTokenSource())
            {
                UpdateEnabled();
                try { await action(current.Token); }
                catch (OperationCanceledException)
                {
                    if (!closing) status.Text = current.IsCancellationRequested ? "Listening off." : "Tablet request timed out. Check its Wi-Fi address.";
                }
                catch (Exception error) { if (!closing) status.Text = error.Message; }
                finally
                {
                    listening = false;
                    operation = null;
                    if (!IsDisposed)
                    {
                        UpdateEnabled();
                        if (closing && IsHandleCreated) BeginInvoke((Action)Close);
                    }
                }
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (operation != null)
            {
                e.Cancel = true;
                closing = true;
                operation.Cancel();
            }
            base.OnFormClosing(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                closing = true;
                if (operation != null) operation.Cancel();
                if (session != null) session.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}

using System;
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
        private readonly Button listen = new Button { Text = "Listen for command" };
        private readonly Label status = new Label { AutoSize = true, MaximumSize = new Size(520, 0) };
        private readonly FlowLayoutPanel manual = new FlowLayoutPanel { AutoSize = true, WrapContents = true };
        private readonly VoiceCommand[] commands = VoiceCommands.ForMode(TabletControlMode.Automatic, null).ToArray();
        private bool keyAvailable;
        private ConnectSession session;
        private RecognizerInfo recognizer;
        private CancellationTokenSource operation;
        private bool listening, closing;

        internal ConnectVoiceForm(string tabletAddress, AudioDevice selectedMicrophone, ConnectKeyStore keyStore = null)
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
            StyleButton(listen);
            AddRow(panel, listen);
            foreach (VoiceCommand command in commands)
            {
                var action = new Button { Text = command.Title, AccessibleName = command.Title };
                StyleButton(action);
                action.Click += async (s, e) => await Run(async token => status.Text = await session.SendAsync(command.Id, token));
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
            Shown += delegate { RefreshMicrophones(selectedMicrophone, true); };
            refreshMic.Click += delegate { RefreshMicrophones(microphone.SelectedItem as AudioDevice, true); };
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
                ApplySpeechDiagnostics(false);
                UpdateEnabled();
            };
            check.Click += async (s, e) => await Run(async token =>
            {
                if (session != null) session.Dispose();
                session = null;
                session = ConnectSession.Open(address.Text.Trim(), keyStore);
                string identity = await session.IdentifyAsync(token);
                status.Text = "Found " + identity + ". " + (keyAvailable ? "Confirm it is your tablet, then pair." : KeyNeeded);
            });
            pair.Click += async (s, e) => await Run(async token =>
            {
                await session.AuthorizeAsync(token);
                status.Text = "Paired with " + session.TabletName + ". Choose a command or press Listen for command.";
            });
            listen.Click += async (s, e) =>
            {
                if (listening && operation != null) { operation.Cancel(); return; }
                listening = true;
                await Run(Listen);
            };
            status.Text = InitialStatus();
            UpdateEnabled();
        }

        private const string KeyNeeded = "Choose Developer key to set up Wi-Fi voice control. Wi-Fi cue audio works independently.";
        private string InitialStatus() { return keyAvailable ? "Check the address, then pair with the tablet on your trusted Wi-Fi network." : KeyNeeded; }

        private void RefreshMicrophones(AudioDevice preferred, bool replaceStatus)
        {
            try
            {
                var devices = Audio.GetInputs();
                microphone.Items.Clear();
                microphone.Items.AddRange(devices.Cast<object>().ToArray());
                var desired = preferred == null ? null : devices.FirstOrDefault(d =>
                    preferred.EndpointId != null ? d.EndpointId == preferred.EndpointId :
                    d.Id == preferred.Id && d.Name == preferred.Name);
                microphone.SelectedItem = desired ?? (devices.Count == 1 ? devices[0] : null);
                recognizer = Audio.FindEnglishRecognizer();
                ApplySpeechDiagnostics(replaceStatus);
            }
            catch (Exception) { status.Text = "Windows could not list speech inputs. Manual controls are available after pairing."; }
            UpdateEnabled();
        }

        private void ApplySpeechDiagnostics(bool replaceStatus)
        {
            // Without a key, the key prompt stays as the first step.
            if (!keyAvailable) return;
            if (recognizer == null)
                status.Text = "Windows has no English desktop speech recognizer, so Listen for command is unavailable. " +
                    "Add English speech in Windows language settings and click Refresh. Manual controls work after pairing.";
            else if (microphone.Items.Count == 0)
                status.Text = "No Windows recording input was found. Connect a microphone and click Refresh. Manual controls work after pairing.";
            else if (microphone.SelectedItem == null)
                status.Text = (replaceStatus ? InitialStatus() : status.Text) + " Choose your command microphone before listening.";
            else if (replaceStatus && status.Text.StartsWith("No Windows recording input", StringComparison.Ordinal))
                status.Text = InitialStatus();
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
            listen.Enabled = listening || (idle && approved && recognizer != null && microphone.SelectedItem is AudioDevice);
            listen.Text = listening ? "Cancel" : "Listen for command";
        }

        private async Task Listen(CancellationToken token)
        {
            var device = (AudioDevice)microphone.SelectedItem;
            status.Text = "Preparing microphone…";
            status.Text = await ListenOnce(
                cancel => Audio.TestMicrophoneAsync(device, recognizer, commands, cancel, () => BeginInvoke((Action)(() =>
                {
                    if (listening && !cancel.IsCancellationRequested)
                        status.Text = "Listening. Say next song, previous song, scroll up or scroll down.";
                }))),
                async (id, cancel) =>
                {
                    string phrase = commands.Single(c => c.Id == id).Phrase;
                    status.Text = "Sending “" + phrase + "”…";
                    try { return "“" + phrase + "” · " + await session.SendAsync(id, cancel); }
                    catch (InvalidOperationException error)
                    {
                        throw new InvalidOperationException("Heard “" + phrase + "”. " + error.Message, error);
                    }
                }, token);
        }

        internal static async Task<string> ListenOnce(Func<CancellationToken, Task<MicrophoneResult>> capture,
            Func<string, CancellationToken, Task<string>> send, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var sample = await capture(token);
            token.ThrowIfCancellationRequested();
            if (String.IsNullOrEmpty(sample.Command)) return Audio.NoCommandMessage(sample.PeakDbFs) +
                (Double.IsNegativeInfinity(sample.PeakDbFs) ? "" : " Press Listen for command to try again.");
            string reply = await send(sample.Command, token);
            if (!sample.SpeechEndTimestamp.HasValue || reply.IndexOf("has not confirmed", StringComparison.OrdinalIgnoreCase) >= 0) return reply;
            double elapsedMs = (Stopwatch.GetTimestamp() - sample.SpeechEndTimestamp.Value) * 1000.0 / Stopwatch.Frequency;
            return String.Format("{0} Tablet response received {1:N0} ms after speech ended.", reply, elapsedMs);
        }

        private async Task Run(Func<CancellationToken, Task> action)
        {
            if (operation != null) return;
            using (var current = operation = new CancellationTokenSource())
            {
                UpdateEnabled();
                try { await action(current.Token); }
                catch (OperationCanceledException) { status.Text = current.IsCancellationRequested ? "Stopped." : "Tablet request timed out. Check its Wi-Fi address."; }
                catch (Exception error) { status.Text = error.Message; }
                finally
                {
                    listening = false;
                    operation = null;
                    UpdateEnabled();
                    if (closing && !IsDisposed) BeginInvoke((Action)Close);
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
            if (disposing && session != null) session.Dispose();
            base.Dispose(disposing);
        }
    }
}

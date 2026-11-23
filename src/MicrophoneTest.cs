using System;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Speech.Recognition;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PcOnSongBridge
{
    internal sealed class MicrophoneTest : BridgeWindow
    {
        private readonly ComboBox microphone = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
        private readonly Label speech = Line();
        private readonly Label peak = Line();
        private readonly Label result = Line();
        private readonly Button listen = new Button { Text = "Test input", AutoSize = true };
        private readonly Button cancel = new Button { Text = "Cancel test", AutoSize = true, Enabled = false };
        private readonly Button close = new Button { Text = "Close", AutoSize = true };
        private readonly Func<AudioDevice, RecognizerInfo, CancellationToken, Task<MicrophoneResult>> capture;
        private RecognizerInfo recognizer;
        private CancellationTokenSource active;
        private bool closeRequested;

        public AudioDevice SelectedMicrophone { get { return microphone.SelectedItem as AudioDevice; } }

        public MicrophoneTest(AudioDevice initialDevice, string preferredName = null)
            : this(initialDevice, Audio.TestMicrophoneAsync, preferredName) { }

        internal MicrophoneTest(AudioDevice initialDevice, Func<AudioDevice, RecognizerInfo, CancellationToken, Task<MicrophoneResult>> capture,
            string preferredName = null)
        {
            this.capture = capture;
            Text = "Test microphone";
            Font = new Font("Segoe UI", 10);
            AutoScaleMode = AutoScaleMode.Dpi;
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = false;
            MaximizeBox = false;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(16);

            var panel = new TableLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, Dock = DockStyle.Fill };
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 470));
            Controls.Add(panel);
            Label caption = Line();
            caption.Text = "Recording input";
            panel.Controls.Add(caption);
            microphone.AccessibleName = caption.Text;
            panel.Controls.Add(microphone);
            panel.Controls.Add(speech);
            Label instructions = Line();
            instructions.Text = "Click Test input, then say “next song” or “previous song” within 2.5 seconds. Song selection stays in place.";
            panel.Controls.Add(instructions);
            panel.Controls.Add(peak);
            panel.Controls.Add(result);
            var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
            buttons.Controls.AddRange(new Control[] { listen, cancel, close });
            panel.Controls.Add(buttons);

            microphone.SelectedIndexChanged += delegate { ResetResult(); };
            listen.Click += async delegate { await TestAsync(); };
            cancel.Click += delegate { CancelTest(); };
            close.Click += delegate { Close(); };
            CancelButton = close;
            try
            {
                recognizer = Audio.FindEnglishRecognizer();
                speech.Text = recognizer == null ? "English speech recognition is unavailable. You can still test the signal."
                    : recognizer.Culture.EnglishName + " desktop speech recognizer available.";
            }
            catch (Exception error) { speech.Text = "Speech recognition is unavailable. " + error.Message; }

            ResetResult();
            try
            {
                var devices = Audio.GetInputs();
                microphone.Items.AddRange(devices.ToArray());
                var matches = devices.Where(d => d.Name == (initialDevice == null ? preferredName : initialDevice.Name)).ToArray();
                var current = initialDevice == null ? null : matches.FirstOrDefault(d => d.Id == initialDevice.Id);
                microphone.SelectedItem = current ?? (matches.Length == 1 ? matches[0] : null)
                    ?? (devices.Count == 1 ? devices[0] : null);
                if (devices.Count == 0) result.Text = "Windows has no available recording inputs.";
            }
            catch (Exception error) { result.Text = "Could not list recording inputs. " + error.Message; }
        }

        private static Label Line()
        {
            return new Label { AutoSize = true, MaximumSize = new Size(455, 0), Margin = new Padding(3, 7, 3, 5) };
        }

        private void ResetResult()
        {
            peak.Text = "Peak input level pending.";
            result.Text = SelectedMicrophone == null ? "Select a recording input." : "Ready to test.";
            listen.Enabled = SelectedMicrophone != null;
        }

        private async Task TestAsync()
        {
            AudioDevice device = SelectedMicrophone;
            if (active != null || device == null) return;
            var operation = new CancellationTokenSource();
            active = operation;
            microphone.Enabled = listen.Enabled = false;
            cancel.Enabled = true;
            peak.Text = "Measuring peak input level…";
            result.Text = "Listening. Say “next song” or “previous song” now.";
            try
            {
                MicrophoneResult sample = await capture(device, recognizer, operation.Token);
                operation.Token.ThrowIfCancellationRequested();
                bool silent = Double.IsNegativeInfinity(sample.PeakDbFs);
                peak.Text = silent ? "Peak input level −∞ dBFS (digital silence)."
                    : "Peak input level " + sample.PeakDbFs.ToString("0.0", CultureInfo.CurrentCulture) + " dBFS.";
                if (silent) result.Text = "No input signal. Check the microphone, mute and input routing.";
                else if (recognizer == null) result.Text = "Signal detected. Install an English desktop recognizer to check the phrases.";
                else result.Text = sample.Command == null ? "Signal detected. Neither command was recognized."
                    : "Recognized “" + sample.Command + " song”.";
            }
            catch (OperationCanceledException) { peak.Text = "Peak input level pending."; result.Text = "Test canceled."; }
            catch (Exception error) { peak.Text = "Peak input level unavailable."; result.Text = "Test failed. " + error.Message; }
            finally
            {
                active = null;
                operation.Dispose();
                microphone.Enabled = true;
                listen.Enabled = SelectedMicrophone != null;
                cancel.Enabled = false;
                if (closeRequested) Close();
            }
        }

        private void CancelTest()
        {
            if (active == null) return;
            cancel.Enabled = false;
            result.Text = "Canceling test…";
            active.Cancel();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);
            if (active == null) return;
            e.Cancel = true;
            closeRequested = true;
            CancelTest();
        }
    }
}

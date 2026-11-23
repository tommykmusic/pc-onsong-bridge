using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PcOnSongBridge
{
    internal enum SetupFeature { Wifi, Usb, Speech }

    internal static class FeatureComponents
    {
        internal static bool ScrcpyPair(string path)
        {
            return WindowsToolTrust.IsLocalDrivePath(path) &&
                String.Equals(Path.GetFileName(path), "scrcpy.exe", StringComparison.OrdinalIgnoreCase) &&
                File.Exists(path) && File.Exists(Path.Combine(Path.GetDirectoryName(path), "adb.exe"));
        }

        internal static bool Ready(SetupFeature feature)
        {
            switch (feature)
            {
                case SetupFeature.Wifi: return NdiRuntime.FindLibrary() != null;
                case SetupFeature.Usb: return ScrcpyPair(Preferences.Load().ScrcpyPath) || ScrcpyPair(AndroidBridge.FindScrcpy());
                case SetupFeature.Speech: return Audio.FindEnglishRecognizer() != null;
                default: throw new ArgumentOutOfRangeException("feature");
            }
        }

        internal static void UseScrcpy(string executable)
        {
            if (!ScrcpyPair(executable)) throw new InvalidOperationException("Choose scrcpy.exe from a local extracted folder containing adb.exe.");
            Preferences preferences = Preferences.Load();
            preferences.ScrcpyPath = executable;
            preferences.Save();
        }

        internal static async Task SetUpAsync(SetupFeature feature, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            switch (feature)
            {
                case SetupFeature.Wifi:
                    await NdiRuntime.OpenSetupAsync(token);
                    break;
                case SetupFeature.Usb:
                    string path = await ScrcpyPackage.InstallAsync(token);
                    UseScrcpy(path);
                    break;
                case SetupFeature.Speech:
                    Process.Start(new ProcessStartInfo("ms-settings:regionlanguage") { UseShellExecute = true });
                    break;
                default: throw new ArgumentOutOfRangeException("feature");
            }
        }
    }

    internal sealed class FeatureSetupForm : BridgeWindow
    {
        private readonly Func<SetupFeature, bool> ready;
        private readonly Func<SetupFeature, CancellationToken, Task> setUp;
        private readonly CheckBox[] selects = new CheckBox[3];
        private readonly Label[] states = new Label[3];
        private readonly LinkLabel locate = new LinkLabel { Text = "Use an extracted scrcpy folder…", AutoSize = true };
        private readonly Button apply = new Button { Text = "Set up selected", AutoSize = true };
        private readonly Button refresh = new Button { Text = "Check again", AutoSize = true };
        private readonly Button finish = new Button { AutoSize = true };
        private readonly string finishLabel;
        private CancellationTokenSource operation;
        private bool closing;
        private static readonly string[] Names = { "Wi-Fi cue audio", "Android USB cues and controls", "Voice commands" };
        private static readonly string[] Details = {
            "Downloads the NDI 6 Runtime and opens the vendor’s setup wizard.",
            "Downloads the verified scrcpy package with Android Debug Bridge (ADB).",
            "Opens Windows language settings to add English speech recognition."
        };
        private static readonly string[] Installed = { "NDI 6 Runtime found.", "scrcpy and adb.exe found.", "English desktop speech recognizer found." };

        internal FeatureSetupForm(bool openApp) : this(openApp, FeatureComponents.Ready, FeatureComponents.SetUpAsync) { }

        internal FeatureSetupForm(bool openApp, Func<SetupFeature, bool> check, Func<SetupFeature, CancellationToken, Task> setup)
        {
            ready = check; setUp = setup;
            Text = "PC OnSong Bridge · Optional components";
            Font = new Font("Segoe UI", 10); AutoScaleMode = AutoScaleMode.Font;
            StartPosition = FormStartPosition.CenterScreen; ClientSize = new Size(566, 445);
            MinimumSize = new Size(460, 360);
            finishLabel = openApp ? "Open bridge" : "Finish";
            var outer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Padding = new Padding(18) };
            outer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            outer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            outer.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(outer);
            var rows = new TableLayoutPanel { Dock = DockStyle.Fill, AutoScroll = true, ColumnCount = 1 };
            rows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            outer.Controls.Add(rows, 0, 0);
            AddRow(rows, new Label { Text = "Choose components", AutoSize = true, Font = new Font(Font, FontStyle.Bold) });
            AddRow(rows, new Label { Text = "Select the features you want on this PC. You can finish now and add components later.", AutoSize = true, Dock = DockStyle.Top });
            for (int i = 0; i < selects.Length; i++)
            {
                var feature = (SetupFeature)i;
                selects[i] = new CheckBox { Text = Names[i], Name = "select" + feature, AutoSize = true, Margin = new Padding(3, 12, 3, 2) };
                selects[i].CheckedChanged += delegate { UpdateButtons(); };
                AddRow(rows, selects[i]);
                states[i] = new Label { Name = "status" + feature, AutoSize = true, Dock = DockStyle.Top, Margin = new Padding(24, 0, 5, 2), AccessibleRole = AccessibleRole.StatusBar };
                AddRow(rows, states[i]);
                if (feature == SetupFeature.Usb)
                {
                    locate.Margin = new Padding(24, 0, 3, 2);
                    AddRow(rows, locate);
                }
            }
            var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
            buttons.Controls.Add(finish); buttons.Controls.Add(apply); buttons.Controls.Add(refresh);
            outer.Controls.Add(buttons, 0, 1);
            finish.Text = finishLabel;
            locate.Click += delegate { LocateScrcpy(); };
            apply.Click += async delegate { await ApplyAsync(); };
            refresh.Click += delegate { Inspect(); };
            finish.Click += delegate
            {
                if (operation != null) { operation.Cancel(); return; }
                DialogResult = DialogResult.OK;
                Close();
            };
            Shown += delegate { Inspect(); };
            FormClosing += delegate(object sender, FormClosingEventArgs e)
            {
                if (operation != null) { closing = true; e.Cancel = true; operation.Cancel(); }
            };
        }

        private static void AddRow(TableLayoutPanel layout, Control control)
        {
            int row = layout.RowCount++;
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.Controls.Add(control, 0, row);
        }

        private void Inspect()
        {
            for (int i = 0; i < selects.Length; i++)
            {
                try { SetStatus(i, ready((SetupFeature)i)); }
                catch (Exception) { selects[i].Enabled = true; states[i].Text = "Could not check this component. " + Details[i]; }
            }
            UpdateButtons();
        }

        private void SetStatus(int i, bool present)
        {
            selects[i].Enabled = !present;
            if (present) selects[i].Checked = false;
            states[i].Text = present ? "Ready · " + Installed[i] : Details[i];
        }

        private void UpdateButtons()
        {
            bool selected = Array.Exists(selects, s => s != null && s.Checked && s.Enabled);
            apply.Enabled = operation == null && selected;
            refresh.Enabled = locate.Enabled = operation == null;
            finish.Text = operation != null ? "Stop setup" : selected ?
                (finishLabel == "Open bridge" ? "Skip and open bridge" : "Skip for now") : finishLabel;
        }

        private void LocateScrcpy()
        {
            using (var picker = new OpenFileDialog { Title = "Choose scrcpy.exe in the fully extracted Windows package", Filter = "scrcpy executable|scrcpy.exe" })
            {
                if (picker.ShowDialog(this) != DialogResult.OK) return;
                try { FeatureComponents.UseScrcpy(picker.FileName); SetStatus((int)SetupFeature.Usb, true); }
                catch (Exception error) { states[(int)SetupFeature.Usb].Text = error.Message; }
                UpdateButtons();
            }
        }

        internal async Task ApplyAsync()
        {
            if (operation != null) return;
            bool[] requested = Array.ConvertAll(selects, item => item.Enabled && item.Checked);
            if (!Array.Exists(requested, value => value)) return;
            using (var current = new CancellationTokenSource())
            {
                operation = current;
                foreach (var item in selects) item.Enabled = false;
                UpdateButtons();
                try
                {
                    for (int i = 0; i < requested.Length; i++)
                    {
                        if (!requested[i]) continue;
                        if (current.IsCancellationRequested) break;
                        var feature = (SetupFeature)i;
                        try
                        {
                            if (!ready(feature))
                            {
                                states[i].Text = feature == SetupFeature.Wifi ? "Verifying NDI setup. Review its license in the vendor wizard." :
                                    feature == SetupFeature.Usb ? "Downloading and verifying scrcpy…" : "Opening Windows language settings…";
                                await setUp(feature, current.Token);
                            }
                            bool present = ready(feature);
                            states[i].Text = present ? "Ready · " + Installed[i] : feature == SetupFeature.Speech ?
                                "In Windows, choose English → Language options → Speech recognition (or Speech). After installation, choose Check again." :
                                "Component not detected. Complete setup and choose Check again, or leave this for later.";
                            selects[i].Checked = !present;
                        }
                        catch (OperationCanceledException)
                        {
                            states[i].Text = !current.IsCancellationRequested ? "Setup timed out. Check your connection and try again." :
                                feature == SetupFeature.Wifi ? "Stopped waiting. If the NDI wizard is open, finish or cancel it there." : "Stopped.";
                        }
                        catch (Exception error) { states[i].Text = "Setup incomplete. " + error.Message; }
                    }
                }
                finally
                {
                    operation = null;
                    for (int i = 0; i < selects.Length; i++)
                    {
                        try { selects[i].Enabled = !ready((SetupFeature)i); }
                        catch (Exception) { selects[i].Enabled = true; }
                    }
                    UpdateButtons();
                    if (closing) BeginInvoke((Action)Close);
                }
            }
        }
    }
}

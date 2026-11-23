using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Speech.Recognition;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PcOnSongBridge
{
    internal sealed class MainForm : BridgeWindow
    {
        private static readonly Color Background = Color.FromArgb(19, 25, 32);
        private static readonly Color Surface = Color.FromArgb(29, 38, 48);
        private static readonly Color ControlBackground = Color.FromArgb(42, 54, 67);
        private static readonly Color Foreground = Color.FromArgb(236, 243, 249);
        private static readonly Color Secondary = Color.FromArgb(177, 195, 207);
        private static readonly Color Accent = Color.FromArgb(111, 225, 194);
        private static readonly Color Alert = Color.FromArgb(255, 188, 142);
        private const int HotkeyId = 91;
        private const int HotkeyMessage = 0x0312;
        private const uint ModNoRepeat = 0x4000;

        private readonly Preferences settings;
        private readonly HandsFreeSession liveVoice;
        private readonly TextBox path = Field();
        private readonly ComboBox devices = Picker();
        private readonly Dictionary<string, string> tabletLabels = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly ComboBox output = Picker();
        private readonly ComboBox input = Picker();
        private readonly ComboBox navigation = Picker();
        private readonly NumericUpDown buffer = new NumericUpDown();
        private readonly CheckBox hotkey = new CheckBox();
        private readonly Label status = new Label();
        private readonly Label tabletHelp = Copy("", Secondary);
        private readonly Label route = Copy("Start cue audio to check the Windows output.", Secondary);
        private readonly System.Windows.Forms.Timer routeTimer = new System.Windows.Forms.Timer { Interval = 1500 };
        private bool checkingRoute;
        private readonly Label activity = new Label();
        private readonly Label speechStatus = Copy("", Secondary);
        private readonly TextBox logs = Field();
        private readonly Button browse = MakeButton("Locate scrcpy");
        private readonly Button installTools = MakeButton("Download scrcpy");
        private readonly Button toolsToggle = MakeButton("scrcpy setup");
        private Control toolsRow;
        private CancellationTokenSource packageDownload;
        private readonly Button refresh = MakeButton("Refresh tablets");
        private readonly Button wifi = MakeButton("Wireless setup");
        private readonly Button wifiCues = MakeButton("Find my tablet", true);
        private readonly Label wifiSummary = Copy("Receive OnSong audio from an iPad or Android tablet on this network.", Secondary);
        private readonly Func<WifiCueServices> wifiServices;
        private WifiCuesForm wifiWindow;
        private bool closingAfterWifi;
        private readonly Button start = MakeButton("Start cue audio", true);
        private readonly Button stop = MakeButton("Stop");
        private readonly Button tone = MakeButton("Play quiet test tone");
        private readonly Button refreshAudio = MakeButton("Refresh audio devices");
        private readonly Button verify = MakeButton("Check navigation");
        private readonly Button testMic = MakeButton("Test microphone");
        private readonly Button testCommands = MakeButton("Command library and tests");
        private readonly Button previous = MakeButton("Previous song");
        private readonly Button next = MakeButton("Next song");
        private readonly Button listen = MakeButton("Start listening", true);
        private AndroidBridge bridge;
        private CancellationTokenSource navigationOperation;
        private CommandLibraryForm commandTest;
        private readonly List<CommandHistoryEntry> commandHistory = new List<CommandHistoryEntry>();
        private RecognizerInfo recognizer;
        private bool navigationChecked;
        private int navigationRevision;
        private string verifiedSerial;
        private string pendingWirelessEndpoint;
        private string pendingWirelessService;
        private bool busy;
        private bool listening;
        private bool microphoneOpen;
        private bool tonePlaying;
        private bool startingAudio;
        private bool stopping;
        private bool closing;
        private bool hotkeyRegistered;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hwnd, int id);

        public MainForm() : this(Preferences.Load()) { }

        internal MainForm(Preferences preferences) : this(preferences, null) { }

        internal MainForm(Preferences preferences, Func<WifiCueServices> wifiServices, HandsFreeSession liveVoice = null)
        {
            settings = preferences;
            this.liveVoice = liveVoice;
            this.wifiServices = wifiServices ?? (() => new WifiCueServices());
            Text = "PC OnSong Bridge v" + BridgeRelease.Version;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(930, 740);
            MinimumSize = new Size(690, 540);
            AutoScaleMode = AutoScaleMode.Dpi;
            BackColor = Background;
            ForeColor = Foreground;
            Font = new Font("Segoe UI", 9.5f);

            path.ReadOnly = true;
            devices.FormattingEnabled = true;
            devices.Format += delegate(object sender, ListControlConvertEventArgs e)
            {
                string label;
                if (e.ListItem is string && tabletLabels.TryGetValue((string)e.ListItem, out label))
                    e.Value = label;
            };
            path.Text = File.Exists(settings.ScrcpyPath) ? settings.ScrcpyPath : AndroidBridge.FindScrcpy();
            navigation.Items.AddRange(new object[] { "Automatic (OnSong links or song swipes)",
                "OnSong arrow shortcuts (foreground)", "OnSong song swipes (foreground)", "Fast song shortcuts (foreground)" });
            navigation.SelectedIndex = (int)(settings.ControlMode ??
                (settings.KeyboardMode ? TabletControlMode.ArrowShortcuts : TabletControlMode.Automatic));
            buffer.Minimum = 25;
            buffer.Maximum = 250;
            buffer.Increment = 5;
            buffer.Value = Math.Max(25, Math.Min(250, settings.BufferMs));
            buffer.Width = 64;
            buffer.BackColor = ControlBackground;
            buffer.ForeColor = Foreground;
            hotkey.Text = "Use global F8 to toggle listening";
            hotkey.AutoSize = true;
            hotkey.Margin = new Padding(12, 6, 4, 2);
            hotkey.ForeColor = Secondary;
            status.TextAlign = ContentAlignment.MiddleRight;
            status.Dock = DockStyle.Fill;
            status.Font = new Font(Font, FontStyle.Bold);
            activity.AutoSize = true;
            activity.MaximumSize = new Size(750, 0);
            activity.ForeColor = Foreground;
            activity.Text = "Open the command library to test a phrase, measure a song change, or assign a shortcut.";
            logs.Multiline = true;
            logs.ReadOnly = true;
            logs.ScrollBars = ScrollBars.Vertical;
            logs.Height = 84;
            logs.Font = new Font("Consolas", 9f);

            BuildLayout();
            SetStatus("Cue audio off", Secondary);
            browse.Click += async delegate { await LocateAsync(); };
            installTools.Click += async delegate { await DownloadToolsAsync(); };
            toolsToggle.Click += delegate { ShowToolsRow(!toolsRow.Visible); };
            refresh.Click += async delegate { await RefreshTabletsAsync(); };
            wifi.Click += async delegate { await OpenWirelessAsync(); };
            wifiCues.Click += delegate { OpenWifiCues(); };
            start.Click += async delegate { await StartAsync(); };
            stop.Click += delegate { StopBridge(); };
            tone.Click += async delegate { await TestToneAsync(); };
            refreshAudio.Click += delegate { LoadAudioDevices(); };
            testMic.Click += delegate { OpenMicrophoneTest(); };
            testCommands.Click += async delegate { await OpenCommandTestAsync(); };
            verify.Click += async delegate { await CheckNavigationAsync(); };
            previous.Click += async delegate { await NavigateAsync("previous"); };
            next.Click += async delegate { await NavigateAsync("next"); };
            listen.Click += async delegate { await NavigateAsync(null); };
            hotkey.CheckedChanged += delegate { SetHotkey(hotkey.Checked); };
            devices.SelectedIndexChanged += delegate
            {
                if (Serial != null)
                {
                    tabletHelp.Visible = false;
                    pendingWirelessEndpoint = pendingWirelessService = null;
                }
                InvalidateNavigation();
            };
            navigation.SelectedIndexChanged += delegate { InvalidateNavigation(); };
            input.SelectedIndexChanged += delegate
            {
                if (listening) CancelNavigation();
                UpdateControls();
            };
            routeTimer.Tick += async delegate { await CheckRouteAsync(); };
            output.SelectedIndexChanged += async delegate
            {
                UpdateControls();
                if (routeTimer.Enabled)
                {
                    SetRouteChecking();
                    await CheckRouteAsync();
                }
            };
            Shown += async delegate
            {
                LoadAudioDevices();
                if (File.Exists(path.Text))
                    await RefreshTabletsAsync();
                else
                {
                    tabletHelp.Text = "For Android audio mirroring or Windows microphone control, download scrcpy or locate your existing copy.";
                }
            };
            UpdateControls();
        }

        private void BuildLayout()
        {
            Panel scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(24, 17, 24, 16) };
            Controls.Add(scroll);
            TableLayoutPanel page = Stack();
            page.Dock = DockStyle.Top;
            scroll.Controls.Add(page);

            TableLayoutPanel header = new TableLayoutPanel
            {
                Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 3, Margin = new Padding(0, 0, 0, 13)
            };
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 69));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 31));
            header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            TableLayoutPanel title = Stack();
            title.Dock = DockStyle.Fill;
            title.Controls.Add(Copy("PC OnSong Bridge", Foreground, 22f, true));
            title.Controls.Add(Copy("OnSong cue audio and voice control for Windows", Secondary));
            header.Controls.Add(title, 0, 0);
            header.Controls.Add(status, 1, 0);
            Button about = MakeButton("&About v" + BridgeRelease.Version);
            about.Anchor = AnchorStyles.Right;
            about.Margin = new Padding(10, 0, 0, 0);
            about.Click += delegate { using (var dialog = new AboutForm(Background, Foreground, ControlBackground)) dialog.ShowDialog(this); };
            header.Controls.Add(about, 2, 0);
            page.Controls.Add(header);

            TableLayoutPanel wirelessCues = Card("Wi-Fi cues");
            wirelessCues.Controls.Add(Buttons(wifiCues));
            wirelessCues.Controls.Add(wifiSummary);
            page.Controls.Add(wirelessCues);

            TableLayoutPanel tablet = Card("Android connection and mirrored audio");
            toolsRow = LabeledRow("scrcpy executable", path, browse, installTools);
            tablet.Controls.Add(toolsRow);
            ShowToolsRow(!File.Exists(path.Text));
            tablet.Controls.Add(LabeledRow("Android tablet", devices, refresh, wifi));
            tablet.Controls.Add(tabletHelp);
            tablet.Controls.Add(Buttons(start, stop, Gap(15), Copy("Buffer (ms)", Secondary), buffer, toolsToggle));
            page.Controls.Add(tablet);

            TableLayoutPanel mixer = Card("Android mirror output");
            Control volumeMixer = MakeButton("Open Windows Volume Mixer");
            volumeMixer.Click += delegate { OpenUrl("ms-settings:apps-volume"); };
            mixer.Controls.Add(LabeledRow("Expected output", output, tone, refreshAudio));
            route.MaximumSize = new Size(630, 0);
            mixer.Controls.Add(route);
            mixer.Controls.Add(Buttons(volumeMixer, Copy("Choose scrcpy’s playback device in Windows.", Secondary)));

            TableLayoutPanel voice = Card("Voice navigation");
            voice.Controls.Add(LabeledRow("Microphone", input, testMic));
            voice.Controls.Add(LabeledRow("Tablet control", navigation, verify));
            voice.Controls.Add(Buttons(testCommands, previous, next));
            voice.Controls.Add(Buttons(listen, hotkey));
            voice.Controls.Add(speechStatus);
            voice.Controls.Add(activity);
            page.Controls.Add(voice);
            page.Controls.Add(mixer);

            TableLayoutPanel diagnostics = Card("Activity");
            diagnostics.Controls.Add(logs);
            page.Controls.Add(diagnostics);
        }

        private static TableLayoutPanel Stack()
        {
            TableLayoutPanel table = new TableLayoutPanel
            {
                ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Margin = Padding.Empty, Dock = DockStyle.Top
            };
            table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            return table;
        }

        private static TableLayoutPanel Card(string title)
        {
            TableLayoutPanel card = Stack();
            card.BackColor = Surface;
            card.Padding = new Padding(15, 11, 15, 10);
            card.Margin = new Padding(0, 0, 0, 9);
            Label heading = Copy(title, Foreground, 12f, true);
            heading.Margin = new Padding(0, 0, 0, 5);
            card.Controls.Add(heading);
            return card;
        }

        private static Label Copy(string text, Color color, float size = 9.5f, bool bold = false)
        {
            return new Label
            {
                Text = text, ForeColor = color, AutoSize = true,
                Font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular),
                Margin = new Padding(0, 3, 6, 3), Anchor = AnchorStyles.Left
            };
        }

        private static TextBox Field()
        {
            return new TextBox
            {
                BackColor = ControlBackground, ForeColor = Foreground, BorderStyle = BorderStyle.FixedSingle,
                Dock = DockStyle.Fill, Margin = new Padding(0, 3, 7, 3)
            };
        }

        private static ComboBox Picker()
        {
            return new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList, FlatStyle = FlatStyle.Flat,
                BackColor = ControlBackground, ForeColor = Foreground,
                Dock = DockStyle.Fill, Margin = new Padding(0, 3, 7, 3)
            };
        }

        private static Button MakeButton(string title, bool primary = false)
        {
            Button result = new BridgeButton
            {
                Text = title, AutoSize = true, Height = 30, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                FlatStyle = FlatStyle.Flat, Margin = new Padding(0, 0, 7, 3),
                Padding = new Padding(8, 1, 8, 1), Cursor = Cursors.Hand,
                BackColor = primary ? Accent : ControlBackground, ForeColor = primary ? Background : Foreground
            };
            result.FlatAppearance.BorderSize = 0;
            result.FlatAppearance.MouseOverBackColor = primary ? Color.FromArgb(151, 239, 215) : Color.FromArgb(54, 71, 88);
            return result;
        }

        private sealed class BridgeButton : Button
        {
            protected override void OnPaint(PaintEventArgs e)
            {
                if (Enabled) { base.OnPaint(e); return; }
                e.Graphics.Clear(Color.FromArgb(38, 47, 57));
                TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, Color.FromArgb(151, 168, 180),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            }
        }

        private static Panel Gap(int size)
        {
            return new Panel { Width = size, Height = 1, Margin = Padding.Empty };
        }

        private static FlowLayoutPanel Buttons(params Control[] controls)
        {
            FlowLayoutPanel row = new FlowLayoutPanel
            {
                WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Margin = Padding.Empty, Dock = DockStyle.Top
            };
            foreach (Control control in controls)
            {
                if (control is Label) control.Margin = new Padding(2, 6, 8, 1);
                row.Controls.Add(control);
            }
            return row;
        }

        private static Control LabeledRow(string label, Control field, params Control[] actions)
        {
            TableLayoutPanel row = new TableLayoutPanel
            {
                ColumnCount = actions.Length > 0 ? 3 : 2, Dock = DockStyle.Top, AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0, 0, 0, 3)
            };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 126));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            row.Controls.Add(Copy(label, Secondary), 0, 0);
            row.Controls.Add(field, 1, 0);
            if (actions.Length > 0)
            {
                row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
                row.Controls.Add(Buttons(actions), 2, 0);
            }
            return row;
        }

        private string Serial
        {
            get { return devices.SelectedItem as string; }
        }

        private void UpdateControls()
        {
            bool streaming = bridge != null && bridge.IsRunning;
            bool ready = File.Exists(path.Text);
            toolsToggle.Visible = ready;
            if (!ready && toolsRow != null) ShowToolsRow(true);
            bool selected = ready && Serial != null;
            bool navigating = navigationOperation != null;
            bool idle = !busy && !navigating;
            bool canNavigate = selected && navigationChecked && idle && !stopping;
            browse.Enabled = idle && !streaming && !stopping;
            installTools.Enabled = browse.Enabled;
            refresh.Enabled = ready && idle && !streaming && !stopping;
            wifi.Enabled = ready && idle && !stopping;
            devices.Enabled = idle && !streaming && !stopping;
            buffer.Enabled = !busy && !streaming && !stopping;
            start.Enabled = selected && !streaming && idle && !stopping && (wifiWindow == null || !wifiWindow.PlaybackBusy);
            wifiCues.Enabled = !closing && !closingAfterWifi;
            if (wifiWindow != null && !wifiWindow.IsDisposed) wifiWindow.RefreshAvailability();
            stop.Enabled = (streaming || startingAudio) && !stopping;
            refreshAudio.Enabled = !navigating && !tonePlaying;
            testMic.Enabled = idle && !tonePlaying;
            testCommands.Enabled = idle && !stopping && !tonePlaying;
            output.Enabled = !tonePlaying;
            tone.Enabled = output.SelectedItem != null && !tonePlaying && !navigating;
            navigation.Enabled = idle;
            input.Enabled = !navigating;
            verify.Enabled = selected && idle && !stopping;
            previous.Enabled = canNavigate;
            next.Enabled = canNavigate;
            bool speechAvailable = recognizer != null || liveVoice != null;
            bool stoppingListener = listening && navigationOperation.IsCancellationRequested;
            hotkey.Enabled = speechAvailable;
            listen.Enabled = listening ? !stoppingListener : canNavigate && speechAvailable && input.SelectedItem != null && !tonePlaying;
            listen.Text = stoppingListener ? "Stopping…" : listening ? "Stop listening" : "Start listening";
            speechStatus.ForeColor = listening ? Accent : Secondary;
            if (listening)
            {
                speechStatus.Text = stoppingListener ? "Stopping listening…" : microphoneOpen
                    ? "Listening on " + input.SelectedItem + "." : "Opening microphone…";
            }
            else if (!speechAvailable)
                speechStatus.Text = "Install an English desktop speech recognizer in Windows, then refresh audio devices.";
            else if (!selected || !navigationChecked)
                speechStatus.Text = "Select your tablet and choose Check navigation to enable listening.";
            else if (input.SelectedItem == null)
                speechStatus.Text = "Select your talkback microphone to enable listening.";
            else
                speechStatus.Text = "Listening is off. Start listening to use voice commands.";
        }

        private void SetStatus(string message, Color color)
        {
            status.Text = message;
            status.ForeColor = color;
        }

        private void OpenWifiCues()
        {
            if (closing || closingAfterWifi) return;
            if (wifiWindow != null && !wifiWindow.IsDisposed)
            {
                if (wifiWindow.WindowState == FormWindowState.Minimized) wifiWindow.WindowState = FormWindowState.Normal;
                wifiWindow.Activate();
                return;
            }
            wifiWindow = new WifiCuesForm(settings, wifiServices(),
                () => !closing && !startingAudio && !stopping && (bridge == null || !bridge.IsRunning),
                () => input.SelectedItem as AudioDevice);
            wifiWindow.PlaybackChanged += delegate
            {
                if (closing || wifiWindow == null) return;
                if (wifiWindow.PlaybackBusy) SetStatus("Wi-Fi cues", Accent);
                else if (status.Text == "Wi-Fi cues") SetStatus("Cue audio off", Secondary);
                wifiCues.Text = wifiWindow.PlaybackBusy ? "Open Wi-Fi cues" : "Find my tablet";
                wifiSummary.Text = wifiWindow.PlaybackBusy ? wifiWindow.Summary : "Receive OnSong audio from an iPad or Android tablet on this network.";
                UpdateControls();
            };
            wifiWindow.FormClosed += delegate
            {
                wifiWindow = null;
                if (status.Text == "Wi-Fi cues") SetStatus("Cue audio off", Secondary);
                wifiCues.Text = "Find my tablet";
                wifiSummary.Text = "Receive OnSong audio from an iPad or Android tablet on this network.";
                if (closingAfterWifi) { BeginInvoke(new Action(Close)); return; }
                UpdateControls();
            };
            wifiWindow.Show(this);
        }

        private void ShowToolsRow(bool show)
        {
            toolsRow.Visible = show;
            toolsToggle.Text = show ? "Hide scrcpy setup" : "scrcpy setup";
        }

        private void OpenMicrophoneTest()
        {
            if (!testMic.Enabled) return;
            busy = true;
            UpdateControls();
            try
            {
                using (var dialog = new MicrophoneTest(input.SelectedItem as AudioDevice))
                {
                    dialog.ShowDialog(this);
                    AudioDevice chosen = dialog.SelectedMicrophone;
                    if (chosen == null) return;
                    var available = Audio.GetInputs();
                    var match = available.Find(item => item.Id == chosen.Id && item.Name == chosen.Name);
                    SetAudioDevices(input, available, null);
                    input.SelectedItem = match;
                }
            }
            catch (Exception error) { WriteLog("Microphone test: " + error.Message); }
            finally { busy = false; if (!closing) UpdateControls(); }
        }

        private async Task OpenCommandTestAsync()
        {
            if (!testCommands.Enabled) return;
            if (verify.Enabled && (!navigationChecked || verifiedSerial != Serial))
                await CheckNavigationAsync();
            if (closing || !testCommands.Enabled) return;

            string serial = verifiedSerial;
            string selectedSerial = Serial;
            TabletControlMode mode = (TabletControlMode)navigation.SelectedIndex;
            AudioDevice mic = input.SelectedItem as AudioDevice;
            RecognizerInfo engine = recognizer;
            bool online = navigationChecked && serial != null && serial == Serial;
            Func<IEnumerable<VoiceCommand>, Action, CancellationToken, Task<MicrophoneResult>> capture = null;
            if (mic != null && engine != null)
                capture = (commands, onListening, token) => Audio.TestMicrophoneAsync(mic, engine, commands, token, onListening);
            Func<VoiceCommand, string, Action, CancellationToken, Task<CommandDelivery>> dispatch = null;
            if (online) dispatch = (command, baseline, onAcknowledged, token) =>
                DispatchAsync(serial, command, mode, true, token, baseline, onAcknowledged);
            Func<CancellationToken, Task<string>> prepareSong = null;
            if (online && mode != TabletControlMode.SongSwipes)
                prepareSong = async token => {
                    try { return await Bridge().ReadChartIdentityAsync(serial, token); }
                    catch (InvalidOperationException) { return null; }
                };
            Func<VoiceShortcut, CancellationToken, Task<string>> teach = null;
            if (selectedSerial != null && File.Exists(path.Text))
                teach = (shortcut, token) => {
                    if (closing || stopping || selectedSerial != Serial)
                        throw new InvalidOperationException("Tablet selection changed. Reopen the command library.");
                    return Bridge().TeachShortcutAsync(selectedSerial, shortcut, token);
                };
            busy = true;
            UpdateControls();
            try
            {
                using (var dialog = new CommandLibraryForm(settings, mode, capture, dispatch, commandHistory,
                    mic == null ? null : mic.DisplayName,
                    online ? devices.GetItemText(devices.SelectedItem) + " · " + navigation.Text : null,
                    online ? null : (Serial == null ? "Connect and select a tablet to send a test." : activity.Text), teach, prepareSong))
                {
                    commandTest = dialog;
                    dialog.ShowDialog(this);
                    string summary = dialog.LastSummary;
                    if (!String.IsNullOrEmpty(summary) && !closing)
                    {
                        activity.Text = summary;
                        WriteLog(summary);
                    }
                }
            }
            catch (Exception error) { WriteLog("Song command test failed. " + error.Message); }
            finally { commandTest = null; busy = false; if (!closing) UpdateControls(); }
        }

        private void WriteLog(string message)
        {
            if (closing || IsDisposed) return;
            if (InvokeRequired)
            {
                OnUi(delegate { WriteLog(message); });
                return;
            }
            if (logs.TextLength > 16000)
            {
                string current = logs.Text;
                int cut = current.IndexOf('\n', Math.Min(current.Length - 1, current.Length / 2));
                logs.Text = cut >= 0 && cut + 1 < current.Length ? current.Substring(cut + 1) : "";
            }
            logs.AppendText(DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + "   " + message.Trim() + Environment.NewLine);
        }

        private void OnUi(Action work)
        {
            if (closing || !IsHandleCreated || IsDisposed) return;
            try { BeginInvoke(work); }
            catch (InvalidOperationException) { }
        }

        private async Task LocateAsync()
        {
            using (OpenFileDialog picker = new OpenFileDialog())
            {
                picker.Title = "Locate scrcpy.exe from its extracted Windows folder";
                picker.Filter = "scrcpy executable|scrcpy.exe";
                if (picker.ShowDialog(this) != DialogResult.OK) return;
                if (!File.Exists(Path.Combine(Path.GetDirectoryName(picker.FileName), "adb.exe")))
                {
                    WriteLog("Choose scrcpy.exe from the extracted package with adb.exe in the same folder.");
                    return;
                }
                SelectTools(picker.FileName);
                await RefreshTabletsAsync();
            }
        }

        private async Task DownloadToolsAsync()
        {
            if (!installTools.Enabled) return;
            var download = new CancellationTokenSource();
            packageDownload = download;
            busy = true;
            installTools.Text = "Downloading…";
            UpdateControls();
            WriteLog("Downloading the official Windows scrcpy package and checking its SHA-256.");
            try
            {
                string executable = await ScrcpyPackage.InstallAsync(download.Token);
                if (closing) return;
                SelectTools(executable);
                WriteLog("scrcpy is ready.");
            }
            catch (OperationCanceledException) { return; }
            catch (Exception error)
            {
                WriteLog("scrcpy setup failed. " + error.Message + " You can also select Locate scrcpy.");
                return;
            }
            finally
            {
                packageDownload = null;
                download.Dispose();
                busy = false;
                if (!closing)
                {
                    installTools.Text = "Download scrcpy";
                    UpdateControls();
                }
            }
            await RefreshTabletsAsync();
        }

        private void SelectTools(string executable)
        {
            if (bridge != null) bridge.Dispose();
            bridge = null;
            devices.Items.Clear();
            path.Text = executable;
            ShowToolsRow(false);
            InvalidateNavigation();
            SavePreferences();
        }

        private AndroidBridge Bridge()
        {
            if (bridge == null)
            {
                bridge = new AndroidBridge(path.Text);
                AndroidBridge instance = bridge;
                bridge.Log += WriteLog;
                bridge.Stopped += delegate
                {
                    OnUi(delegate
                    {
                        if (bridge != instance || instance.IsRunning) return;
                        bool requested = stopping;
                        stopping = false;
                        routeTimer.Stop();
                        route.Text = "Start cue audio to check the Windows output.";
                        route.ForeColor = Secondary;
                        CancelNavigation();
                        InvalidateNavigation();
                        SetStatus(requested ? "Cue audio off" : "Cue audio ended", requested ? Secondary : Alert);
                        UpdateControls();
                    });
                };
            }
            return bridge;
        }

        private async Task RefreshTabletsAsync()
        {
            if (busy || !File.Exists(path.Text)) return;
            busy = true;
            UpdateControls();
            try
            {
                string previous = pendingWirelessEndpoint ?? Serial ?? settings.Serial;
                string result = await Bridge().ListDevicesAsync();
                if (closing) return;
                List<string> available = new List<string>();
                tabletLabels.Clear();
                foreach (string line in result.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    string[] parts = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length < 2 || parts[1] != "device" || tabletLabels.ContainsKey(parts[0])) continue;
                    string serial = parts[0];
                    string model = parts.FirstOrDefault(part => part.StartsWith("model:", StringComparison.Ordinal));
                    string transport = AndroidBridge.IsNetworkSerial(serial) ? "Wi-Fi / LAN"
                        : parts.Any(part => part.StartsWith("usb:", StringComparison.Ordinal)) ? "USB" : "";
                    var details = new[] { model == null ? null : model.Substring(6).Replace('_', ' '), transport, serial };
                    tabletLabels.Add(serial, String.Join(" · ", details.Where(part => !String.IsNullOrEmpty(part))));
                    available.Add(serial);
                }
                devices.Items.Clear();
                devices.Items.AddRange(available.Cast<object>().ToArray());
                if (previous != null && available.Contains(previous))
                    devices.SelectedItem = previous;
                else if (pendingWirelessService != null && available.Contains(pendingWirelessService))
                    devices.SelectedItem = pendingWirelessService;
                else if (available.Count == 1 && pendingWirelessEndpoint == null)
                    devices.SelectedIndex = 0;
                bool unauthorized = result.IndexOf("unauthorized", StringComparison.OrdinalIgnoreCase) >= 0;
                tabletHelp.Visible = available.Count == 0 || Serial == null;
                tabletHelp.Text = available.Count > 0 ? "Select the tablet running OnSong."
                    : unauthorized ? "Unlock the tablet, approve USB debugging, then refresh."
                    : "Connect the tablet with USB debugging enabled, or use Wireless setup.";
                WriteLog(available.Count == 0
                    ? "No authorized tablet found. Unlock it and approve USB debugging, or use Wireless setup."
                    : available.Count + (available.Count == 1 ? " authorized tablet found." : " authorized tablets found. Select the one running OnSong."));
                if (unauthorized)
                    WriteLog("An Android device is waiting for debugging approval.");
            }
            catch (Exception error)
            {
                if (closing) return;
                devices.Items.Clear();
                InvalidateNavigation();
                tabletHelp.Visible = true;
                tabletHelp.Text = "Tablet discovery failed. Check the connection and refresh.";
                WriteLog("Could not refresh tablets. " + error.Message);
            }
            finally { busy = false; if (!closing) UpdateControls(); }
        }

        private async Task StartAsync()
        {
            if (!start.Enabled) return;
            busy = true;
            startingAudio = true;
            UpdateControls();
            SetStatus("Starting cue audio…", Secondary);
            try
            {
                string serial = Serial;
                await Bridge().StartAsync(serial, (int)buffer.Value);
                startingAudio = false;
                if (closing || stopping) return;
                if (!Bridge().IsRunning)
                    throw new InvalidOperationException("scrcpy stopped before the audio stream opened.");
                SetRouteChecking();
                routeTimer.Start();
                WriteLog("scrcpy started. Tap a cue in OnSong to check the audio path.");
                SavePreferences();
                await CheckRouteAsync();
            }
            catch (OperationCanceledException)
            {
                stopping = false;
                if (!closing) SetStatus("Cue audio off", Secondary);
            }
            catch (Exception error)
            {
                if (!closing) SetStatus(stopping ? "Cue audio off" : "Cue audio unavailable", stopping ? Secondary : Alert);
                stopping = false;
                WriteLog(error.Message);
            }
            finally { startingAudio = false; busy = false; if (!closing) UpdateControls(); }
        }

        private void StopBridge()
        {
            if (bridge == null || (!bridge.IsRunning && !startingAudio)) return;
            stopping = true;
            SetStatus("Stopping cue audio…", Secondary);
            CancelNavigation();
            UpdateControls();
            try { bridge.Stop(); }
            catch (Exception error)
            {
                stopping = false;
                WriteLog(error.Message);
                UpdateControls();
            }
        }

        private void LoadAudioDevices()
        {
            recognizer = null;
            try { recognizer = Audio.FindEnglishRecognizer(); }
            catch (Exception error) { WriteLog("Windows could not list speech recognizers. " + error.Message); }
            if (recognizer == null) hotkey.Checked = false;
            UpdateControls();
            try
            {
                AudioDevice selected = output.SelectedItem as AudioDevice;
                string savedOutput = selected == null ? settings.OutputName : selected.Name;
                string savedEndpoint = selected == null ? settings.OutputEndpointId : selected.EndpointId;
                string savedInput = (input.SelectedItem as AudioDevice == null) ? settings.InputName : ((AudioDevice)input.SelectedItem).Name;
                SetAudioDevices(output, Audio.GetOutputs(), savedOutput, savedEndpoint);
                SetAudioDevices(input, Audio.GetInputs(), savedInput);
            }
            catch (Exception error) { WriteLog(error.Message); }
        }

        private static void SetAudioDevices(ComboBox picker, List<AudioDevice> devices, string previous, string endpointId = null)
        {
            picker.Items.Clear();
            picker.Items.AddRange(devices.Cast<object>().ToArray());
            AudioDevice[] matches = devices.Where(item => String.IsNullOrEmpty(endpointId) ? item.Name == previous
                : String.Equals(item.EndpointId, endpointId, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length == 1) picker.SelectedItem = matches[0];
        }

        private void SetRouteChecking()
        {
            SetStatus("Checking cue output…", Secondary);
            route.Text = "Waiting for the scrcpy playback session…";
            route.ForeColor = Secondary;
        }

        private async Task CheckRouteAsync()
        {
            int? processId = bridge == null ? null : bridge.SessionProcessId;
            if (checkingRoute || !processId.HasValue || stopping || closing) return;
            AudioDevice selected = output.SelectedItem as AudioDevice;
            checkingRoute = true;
            try
            {
                PlaybackRouteResult result = await Task.Run(() => PlaybackRoute.Inspect(processId.Value,
                    selected == null ? null : selected.EndpointId));
                if (closing || stopping || bridge == null || bridge.SessionProcessId != processId
                    || !Object.ReferenceEquals(selected, output.SelectedItem)) return;

                Color color = Secondary;
                string summary;
                switch (result.State)
                {
                    case PlaybackRouteState.Matched: summary = "Output matched"; color = Accent; break;
                    case PlaybackRouteState.Mismatch: summary = "Wrong cue output"; color = Alert; break;
                    case PlaybackRouteState.Multiple: summary = "Multiple cue outputs"; color = Alert; break;
                    case PlaybackRouteState.Pending: summary = "Waiting for playback"; break;
                    case PlaybackRouteState.Unselected: summary = "Output unverified"; break;
                    default: summary = "Output unverified"; color = Alert; break;
                }
                string message = result.Message;
                if (selected != null && String.IsNullOrEmpty(selected.EndpointId))
                {
                    summary = "Output unverified";
                    color = Alert;
                    message += " Windows could not identify the selected output. Refresh audio devices.";
                }
                if (route.Text != message) WriteLog(message);
                route.Text = message;
                route.ForeColor = color;
                SetStatus(summary, color);
            }
            catch (Exception error)
            {
                if (closing || stopping || bridge == null || bridge.SessionProcessId != processId) return;
                string message = "Windows could not check the playback route. " + error.Message;
                if (route.Text != message) WriteLog(message);
                route.Text = message;
                route.ForeColor = Alert;
                SetStatus("Output unverified", Alert);
            }
            finally { checkingRoute = false; }
        }

        private async Task TestToneAsync()
        {
            AudioDevice device = output.SelectedItem as AudioDevice;
            if (device == null || tonePlaying) return;
            tonePlaying = true;
            UpdateControls();
            try
            {
                await Audio.PlayTestToneAsync(device);
                WriteLog("Sent the test tone to " + device.DisplayName + ".");
            }
            catch (Exception error) { WriteLog(error.Message); }
            finally { tonePlaying = false; if (!closing) UpdateControls(); }
        }

        private void InvalidateNavigation()
        {
            navigationRevision++;
            if (listening) CancelNavigation();
            if (commandTest != null && !commandTest.IsDisposed) commandTest.Close();
            if (navigationChecked)
                activity.Text = "Check navigation again before listening.";
            navigationChecked = false;
            verifiedSerial = null;
            UpdateControls();
        }

        private async Task CheckNavigationAsync()
        {
            if (!verify.Enabled) return;
            busy = true;
            navigationChecked = false;
            verifiedSerial = null;
            UpdateControls();
            string serial = Serial;
            TabletControlMode mode = (TabletControlMode)navigation.SelectedIndex;
            int revision = navigationRevision;
            activity.Text = "Checking tablet control…";
            try
            {
                if (mode == TabletControlMode.Automatic && !await Bridge().HasSongLinksAsync(serial))
                {
                    if (closing || revision != navigationRevision || stopping) return;
                    mode = TabletControlMode.SongSwipes;
                    WriteLog("OnSong has no handler for a song link. Checking the song viewer for swipe control.");
                }
                string instruction;
                switch (mode)
                {
                    case TabletControlMode.SongSwipes:
                        await Bridge().CheckSwipeAsync(serial);
                        instruction = "Song viewer ready. Test both directions in a practice set with Swipe Through Sets enabled.";
                        break;
                    case TabletControlMode.ArrowShortcuts:
                        await Bridge().CheckKeyboardAsync(serial);
                        instruction = "In OnSong Shortcuts, assign Right Arrow to Next Song and Left Arrow to Previous Song. Try both manual buttons in a test set.";
                        break;
                    case TabletControlMode.FastSongShortcuts:
                        await Bridge().CheckFastSongShortcutsAsync(serial);
                        instruction = "Fast shortcuts selected. In the command library, test Next song (" +
                            VoiceCommands.NextSongShortcut + ") and Previous song (" + VoiceCommands.PreviousSongShortcut + ").";
                        break;
                    default:
                        instruction = "OnSong link found. Verify both directions in a test set.";
                        break;
                }
                if (closing || revision != navigationRevision || stopping) return;
                navigation.SelectedIndex = (int)mode;
                activity.Text = instruction;
                verifiedSerial = serial;
                navigationChecked = true;
            }
            catch (Exception error)
            {
                if (!closing && revision == navigationRevision)
                    activity.Text = "Navigation check failed. " + (error.Message.Length <= 150 ? error.Message : "See Activity.");
                WriteLog(error.Message);
            }
            finally { busy = false; if (!closing) UpdateControls(); }
        }

        private async Task NavigateAsync(string command)
        {
            bool useVoice = command == null;
            if (closing) return;
            if (useVoice && listening)
            {
                if (!navigationOperation.IsCancellationRequested) CancelNavigation();
                return;
            }
            if (!(useVoice ? listen.Enabled : next.Enabled)) return;
            string serial = Serial;
            if (serial != verifiedSerial) return;
            var operation = new CancellationTokenSource();
            CancellationToken token = operation.Token;
            navigationOperation = operation;
            listening = useVoice;
            UpdateControls();
            try
            {
                TabletControlMode mode = (TabletControlMode)navigation.SelectedIndex;
                VoiceCommand[] commands = VoiceCommands.ForMode(mode, settings.VoiceBindings).ToArray();
                if (!useVoice)
                {
                    await SendNavigationAsync(serial, commands.First(item => item.Id == command), mode, null, token);
                    return;
                }
                HandsFreeSession run = liveVoice ?? Audio.ListenHandsFreeAsync;
                activity.Text = "Say a ready phrase from the command library.";
                await run((AudioDevice)input.SelectedItem, recognizer, commands,
                    delegate
                    {
                        if (closing || navigationOperation != operation || token.IsCancellationRequested) return;
                        microphoneOpen = true;
                        UpdateControls();
                    },
                    (sample, dispatchToken) =>
                    {
                        token.ThrowIfCancellationRequested();
                        return SendNavigationAsync(serial, commands.First(item => item.Id == sample.Command),
                            mode, sample, dispatchToken);
                    },
                    sample =>
                    {
                        if (!closing && !token.IsCancellationRequested && sample.PeakDbFs >= -1)
                            activity.Text = Audio.NoCommandMessage(sample.PeakDbFs);
                    }, token);
            }
            catch (OperationCanceledException)
            {
                if (!closing && navigationChecked) activity.Text = useVoice ? "Listening stopped." : "Navigation canceled.";
            }
            catch (Exception error)
            {
                if (!closing) activity.Text = (useVoice ? "Listening stopped. " : "Navigation failed. ") +
                    (error.Message.Length <= 150 ? error.Message : "See Activity.");
                WriteLog(error.Message);
            }
            finally
            {
                listening = false;
                microphoneOpen = false;
                navigationOperation = null;
                operation.Dispose();
                if (!closing) UpdateControls();
            }
        }

        private async Task SendNavigationAsync(string serial, VoiceCommand action, TabletControlMode mode,
            MicrophoneResult sample, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            activity.Text = "Sending “" + action.Phrase + "”…";
            CommandDelivery delivery = await DispatchAsync(serial, action, mode, false, token);
            if (closing) return;
            activity.Text = delivery.Message;
            string timingDetail = "";
            if (sample != null && sample.SpeechEndTimestamp.HasValue)
            {
                double toMilliseconds = 1000.0 / Stopwatch.Frequency;
                activity.Text += String.Format(CultureInfo.CurrentCulture, "  Sent to Android in {0:N0} ms (chart not verified).",
                    (delivery.Acknowledged - sample.SpeechEndTimestamp.Value) * toMilliseconds);
                timingDetail = String.Format(CultureInfo.CurrentCulture,
                    "  Detected speech end to dispatch {0:N0} ms. Dispatch to Android acknowledgment {1:N0} ms.",
                    (delivery.DispatchStarted - sample.SpeechEndTimestamp.Value) * toMilliseconds,
                    (delivery.Acknowledged - delivery.DispatchStarted) * toMilliseconds);
            }
            WriteLog("Sent " + (sample != null ? "voice" : "manual") + " command: " + action.Phrase.ToLowerInvariant() + ". " + activity.Text + timingDetail);
        }

        private Task<CommandDelivery> DispatchAsync(string serial, VoiceCommand command, TabletControlMode mode, bool observe,
            CancellationToken token, string baseline = null, Action onAcknowledged = null)
        {
            token.ThrowIfCancellationRequested();
            if (closing || stopping || !navigationChecked || serial != verifiedSerial || serial != Serial)
                throw new InvalidOperationException("Tablet control changed. Check navigation and try again.");
            return CommandDispatch.SendAsync(Bridge(), serial, mode, command, observe, token, baseline, onAcknowledged);
        }

        private void CancelNavigation()
        {
            if (commandTest != null && !commandTest.IsDisposed) commandTest.CancelPending();
            if (navigationOperation != null)
            {
                navigationOperation.Cancel();
                if (!closing) UpdateControls();
            }
        }

        private void SetHotkey(bool enabled)
        {
            if (enabled && !hotkeyRegistered)
            {
                hotkeyRegistered = RegisterHotKey(Handle, HotkeyId, ModNoRepeat, (uint)Keys.F8);
                if (!hotkeyRegistered)
                {
                    hotkey.Checked = false;
                    WriteLog("Windows could not register F8. Use the listening button.");
                }
            }
            else if (!enabled && hotkeyRegistered)
            {
                UnregisterHotKey(Handle, HotkeyId);
                hotkeyRegistered = false;
            }
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == HotkeyMessage && message.WParam.ToInt32() == HotkeyId)
            {
                if (hotkeyRegistered && !closing)
                    BeginInvoke(new Action(async delegate
                    {
                        if (!hotkeyRegistered || closing) return;
                        if (listening || (!busy && !OwnedForms.Any(form => form.Modal || form.OwnedForms.Any(child => child.Modal))))
                            await NavigateAsync(null);
                    }));
                return;
            }
            base.WndProc(ref message);
        }

        private void SavePreferences()
        {
            settings.ScrcpyPath = path.Text;
            settings.Serial = Serial ?? settings.Serial;
            if (input.SelectedItem != null) settings.InputName = ((AudioDevice)input.SelectedItem).Name;
            AudioDevice selectedOutput = output.SelectedItem as AudioDevice;
            if (selectedOutput != null)
            {
                settings.OutputName = selectedOutput.Name;
                settings.OutputEndpointId = selectedOutput.EndpointId;
            }
            settings.BufferMs = (int)buffer.Value;
            settings.ControlMode = (TabletControlMode)navigation.SelectedIndex;
            settings.KeyboardMode = settings.ControlMode == TabletControlMode.ArrowShortcuts;
            try { settings.Save(); }
            catch (Exception error) { WriteLog("Could not save settings. " + error.Message); }
        }

        private async Task OpenWirelessAsync()
        {
            if (!wifi.Enabled) return;
            bool refreshAfter;
            busy = true;
            UpdateControls();
            try
            {
                using (WirelessDialog dialogue = new WirelessDialog(Bridge()))
                {
                    refreshAfter = dialogue.ShowDialog(this) == DialogResult.OK;
                    if (refreshAfter)
                    {
                        pendingWirelessEndpoint = dialogue.ConnectedEndpoint;
                        pendingWirelessService = dialogue.ConnectedServiceSerial;
                    }
                }
            }
            catch (Exception error) { WriteLog(error.Message); return; }
            finally { busy = false; if (!closing) UpdateControls(); }
            if (!refreshAfter) return;
            if (bridge != null && bridge.IsRunning)
                WriteLog("Cue audio continues on the current tablet connection. To switch, click Stop, then Refresh tablets and select the Wi-Fi tablet.");
            else await RefreshTabletsAsync();
        }

        private void OpenUrl(string url)
        {
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception error) { WriteLog(error.Message); }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);
            if (wifiWindow != null && !wifiWindow.IsDisposed)
            {
                e.Cancel = true;
                closingAfterWifi = true;
                CancelNavigation();
                wifiWindow.Close();
                return;
            }
            if (e.Cancel) return;
            SavePreferences();
            closing = true;
            if (packageDownload != null) packageDownload.Cancel();
            routeTimer.Dispose();
            SetHotkey(false);
            CancelNavigation();
            if (bridge != null) bridge.Dispose();
        }

        internal sealed class WirelessDialog : BridgeWindow
        {
            private readonly AndroidBridge bridge;
            private readonly ComboBox pairingEndpoint = Picker();
            private readonly TextBox pairingCode = Field();
            private readonly ComboBox connectionEndpoint = Picker();
            private readonly Label feedback = Copy("", Secondary);
            private readonly Button pair = MakeButton("Pair");
            private readonly Button connect = MakeButton("Connect", true);
            private readonly Button find = MakeButton("Find tablets");
            private readonly Button connected = MakeButton("Show connected tablets");
            private CancellationTokenSource discovery;
            private bool working;
            public string ConnectedEndpoint { get; private set; }
            public string ConnectedServiceSerial { get; private set; }

            public WirelessDialog(AndroidBridge bridge)
            {
                this.bridge = bridge;
                Text = "Wireless tablet setup";
                ClientSize = new Size(660, 363);
                AutoScroll = true;
                FormBorderStyle = FormBorderStyle.FixedDialog;
                StartPosition = FormStartPosition.CenterParent;
                MaximizeBox = false;
                MinimizeBox = false;
                ShowInTaskbar = false;
                BackColor = Surface;
                ForeColor = Foreground;
                Font = new Font("Segoe UI", 9.5f);
                Padding = new Padding(16);
                AutoScaleMode = AutoScaleMode.Dpi;
                TableLayoutPanel screen = Stack();
                Controls.Add(screen);
                screen.Controls.Add(Copy("Previously paired tablet", Foreground, 12f, true));
                bool live = bridge.IsRunning;
                if (live) connected.Text = "Return to live session";
                Label instructions = Copy(live
                    ? "With the tablet and PC on the same network, enable Wireless debugging. Choose the tablet’s connection address to prepare Wi-Fi. Your current cue audio continues until you click Stop."
                    : "Enable Wireless debugging on the tablet and join the same network. Check the connected tablets first. If yours is missing, select its connection below or type the address from the main Wireless debugging screen.", Secondary);
                instructions.MaximumSize = new Size(610, 0);
                screen.Controls.Add(instructions);
                screen.Controls.Add(Buttons(connected, find));
                connectionEndpoint.DropDownStyle = pairingEndpoint.DropDownStyle = ComboBoxStyle.DropDown;
                screen.Controls.Add(LabeledRow("Connection IP:port", connectionEndpoint, connect));
                Label first = Copy("Pair this PC for the first time", Foreground, 12f, true);
                first.Margin = new Padding(0, 10, 0, 2);
                screen.Controls.Add(first);
                Label hint = Copy("On the tablet, open Pair device with pairing code. Click Find tablets, choose the matching address or type it, then enter the six-digit code.", Secondary);
                hint.MaximumSize = new Size(610, 0);
                screen.Controls.Add(hint);
                pairingCode.MaxLength = 6;
                pairingCode.UseSystemPasswordChar = true;
                screen.Controls.Add(LabeledRow("Pairing IP:port", pairingEndpoint));
                screen.Controls.Add(LabeledRow("Pairing code", pairingCode, pair));
                feedback.MaximumSize = new Size(610, 0);
                screen.Controls.Add(feedback);
                Shown += async delegate { await FindAsync(); };
                find.Click += async delegate { await FindAsync(); };
                connected.Click += delegate { DialogResult = live ? DialogResult.Cancel : DialogResult.OK; };
                pair.Click += async delegate { await PairAsync(); };
                connect.Click += async delegate { await ConnectAsync(); };
            }

            private void Working(bool value)
            {
                working = value;
                pair.Enabled = !value;
                connect.Enabled = !value;
                find.Enabled = connected.Enabled = !value;
                connectionEndpoint.Enabled = pairingEndpoint.Enabled = pairingCode.Enabled = !value;
            }

            private static string Address(ComboBox picker)
            {
                var item = picker.SelectedItem as WirelessService;
                return item == null ? picker.Text.Trim() : item.Endpoint;
            }

            private static void UpdateChoices(ComboBox picker, WirelessService[] services)
            {
                var previous = picker.SelectedItem as WirelessService;
                string manual = previous == null ? picker.Text : "";
                picker.Items.Clear();
                picker.Items.AddRange(services);
                picker.SelectedIndex = -1;
                picker.Text = manual;
                if (previous != null)
                    picker.SelectedItem = services.FirstOrDefault(item => item.Name == previous.Name && item.Endpoint == previous.Endpoint);
                else if (String.IsNullOrWhiteSpace(manual) && services.Length == 1)
                    picker.SelectedIndex = 0;
            }

            private async Task FindAsync()
            {
                var scan = new CancellationTokenSource();
                discovery = scan;
                Working(true);
                feedback.Text = "Looking for wireless tablets…";
                try
                {
                    var services = await bridge.DiscoverWirelessAsync(scan.Token);
                    if (scan.IsCancellationRequested) return;
                    UpdateChoices(connectionEndpoint, services.Where(item => !item.Pairing).ToArray());
                    UpdateChoices(pairingEndpoint, services.Where(item => item.Pairing).ToArray());
                    feedback.Text = services.Count == 0
                        ? "No wireless addresses advertised. Enter the current address from the tablet, or check the main tablet list."
                        : "Match the address to your tablet before choosing Connect or Pair.";
                }
                catch (OperationCanceledException) { }
                catch (Exception error)
                {
                    if (scan.IsCancellationRequested) return;
                    UpdateChoices(connectionEndpoint, new WirelessService[0]);
                    UpdateChoices(pairingEndpoint, new WirelessService[0]);
                    feedback.Text = "Discovery unavailable. Enter the address from the tablet. " + error.Message;
                }
                finally
                {
                    discovery = null;
                    if (!scan.IsCancellationRequested) Working(false);
                    scan.Dispose();
                }
            }

            private async Task PairAsync()
            {
                Working(true);
                feedback.Text = "Pairing…";
                try
                {
                    await bridge.PairAsync(Address(pairingEndpoint), pairingCode.Text.Trim());
                    feedback.Text = bridge.IsRunning
                        ? "Paired. Click Find tablets, then choose its connection address to prepare Wi-Fi for after the current session."
                        : "Paired. Choose Show connected tablets. If your tablet is missing, return here and choose its connection address.";
                }
                catch (Exception error) { feedback.Text = error.Message; }
                finally { pairingCode.Clear(); Working(false); }
            }

            private async Task ConnectAsync()
            {
                Working(true);
                feedback.Text = "Connecting…";
                try
                {
                    string endpoint = Address(connectionEndpoint);
                    await bridge.ConnectAsync(endpoint);
                    ConnectedEndpoint = endpoint;
                    var service = connectionEndpoint.SelectedItem as WirelessService;
                    if (service != null) ConnectedServiceSerial = service.Name + "._adb-tls-connect._tcp";
                    Working(false);
                    DialogResult = DialogResult.OK;
                }
                catch (Exception error) { feedback.Text = error.Message; }
                finally { Working(false); }
            }

            protected override void OnFormClosing(FormClosingEventArgs e)
            {
                if (discovery != null) discovery.Cancel();
                else if (working) e.Cancel = true;
                base.OnFormClosing(e);
            }
        }
    }
}

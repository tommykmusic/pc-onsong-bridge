using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PcOnSongBridge
{
    internal class WifiCueServices
    {
        private NdiReceiver receiver;
        private int gainDb;

        internal virtual string FindRuntime() { return NdiRuntime.FindLibrary(); }
        internal virtual Task InstallAsync(CancellationToken token) { return NdiRuntime.OpenSetupAsync(token); }
        internal virtual Task<List<NdiSource>> DiscoverAsync(CancellationToken token) { return NdiDiscovery.FindAsync(token); }
        internal virtual Task<List<NdiSource>> DiscoverAtAsync(string tabletIp, CancellationToken token) { return NdiDiscovery.FindAtAddressAsync(tabletIp, token); }
        internal virtual List<AudioDevice> Outputs() { return Audio.GetOutputs(); }
        internal virtual string DefaultOutput() { return PlaybackRoute.GetDefaultOutputEndpointId(); }
        internal virtual Task StartAsync(string runtime, NdiSource source, string endpointId, CancellationToken token)
        {
            receiver = new NdiReceiver(runtime);
            receiver.SetGainDb(gainDb);
            return receiver.StartAsync(source, endpointId, token);
        }
        internal virtual void SetCueGain(int value)
        {
            gainDb = value;
            if (receiver != null) receiver.SetGainDb(value);
        }
        internal virtual NdiReceiverStatus GetStatus() { return receiver.GetStatus(); }
        internal virtual async Task StopAsync()
        {
            if (receiver == null) return;
            var closing = receiver;
            receiver = null;
            try { await closing.StopAsync(); }
            finally { closing.Dispose(); }
        }
    }

    internal sealed class WifiCuesForm : BridgeWindow
    {
        private enum Activity { Idle, Finding, Installing, Starting, Receiving, Stopping }
        private static readonly string[] TabletNames = { "OnSong", "Lyrics Projection", "Stage Monitor" };
        private const string AnnounceAgain = "If your tablet is already streaming, tap Stop Streaming and Go Live again during the search.";
        private readonly Preferences preferences;
        private readonly WifiCueServices services;
        private readonly Func<bool> canPlay;
        private readonly Func<AudioDevice> microphone;
        private readonly ComboBox sources = Picker("OnSong Wi-Fi source");
        private readonly HashSet<string> duplicateSourceNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly ComboBox outputs = Picker("Wi-Fi cue output");
        private readonly Button find = Button("Find my tablet");
        private readonly LinkLabel enterAddress = new LinkLabel { Text = "Enter tablet address", AutoSize = true, Margin = new Padding(13, 4, 0, 4) };
        private readonly TextBox tabletIp = new TextBox { AccessibleName = "Tablet Wi-Fi IP address", Dock = DockStyle.Top };
        private readonly Button findAt = Button("Find at address");
        private readonly Control addressRow;
        private readonly Button refreshOutputs = Button("Refresh outputs");
        private readonly TrackBar gain = new TrackBar
        {
            AccessibleName = "NDI cue gain", AccessibleDescription = "Decibels applied to the OnSong stream before Windows playback.",
            Minimum = NdiReceiver.MinGainDb, Maximum = NdiReceiver.MaxGainDb, SmallChange = 1, LargeChange = 3,
            Width = 195, AutoSize = false, Height = 30, TickFrequency = 6, Margin = Padding.Empty
        };
        private readonly Label gainValue = Label("");
        private readonly Button install = Button("Set up Wi-Fi playback");
        private readonly Button start = Button("Start Wi-Fi cues");
        private readonly Button stop = Button("Stop Wi-Fi cues");
        private readonly Button voice = Button("Wi-Fi voice control…");
        private readonly LinkLabel vendor = new LinkLabel { Text = "NDI website", AutoSize = true, Margin = new Padding(0, 6, 0, 3) };
        private readonly Label status = Label("");
        private readonly Label level = Label("");
        private readonly ToolTip audioDetails = new ToolTip();
        private readonly Label blocker = Label("");
        private readonly Label setupInfo = Label("Wi-Fi playback needs the official NDI Runtime for Windows. Run setup, then return here.");
        private readonly Label voiceInfo = Label("");
        private readonly System.Windows.Forms.Timer meter = new System.Windows.Forms.Timer { Interval = 250 };
        private string runtime;
        private bool closeRequested, released, hasSession, savedSource;
        private Activity current;
        private CancellationTokenSource operation;
        internal event Action PlaybackChanged;
        internal bool PlaybackBusy { get { return current == Activity.Starting || current == Activity.Receiving || current == Activity.Stopping; } }
        internal string Summary { get { return status.Text; } }

        internal WifiCuesForm(Preferences preferences, WifiCueServices services, Func<bool> canPlay, Func<AudioDevice> microphone)
        {
            this.preferences = preferences;
            this.services = services;
            this.canPlay = canPlay;
            this.microphone = microphone;
            Text = "Wi-Fi cues";
            Font = new Font("Segoe UI", 9.5f);
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(700, 610);
            MinimumSize = new Size(465, 350);
            status.AccessibleName = "Wi-Fi cue status";
            status.AccessibleRole = AccessibleRole.StatusBar;
            level.AccessibleName = "Wi-Fi audio level";
            status.Font = new Font(Font, FontStyle.Bold);
            var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
            var layout = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Padding = new Padding(18, 12, 18, 14), Margin = Padding.Empty };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            scroll.Controls.Add(layout);
            Controls.Add(scroll);
            var heading = Label("Receive cue audio over Wi-Fi");
            heading.Font = new Font("Segoe UI", 16, FontStyle.Bold);
            var directions = Label("On the tablet, join the same network as this PC. In OnSong, open External Video → Live, enable Send Audio and NDI®, then tap Go Live.");
            var sourceLabel = Label("Tablet stream");
            addressRow = SelectRow(tabletIp, findAt);
            addressRow.Visible = false;
            string savedIp = Host(preferences.WifiCueSourceAddress);
            tabletIp.Text = IsTabletIp(savedIp) ? savedIp : "";
            var outputLabel = Label("Windows output");
            gain.Value = preferences.WifiCueGainDb >= gain.Minimum && preferences.WifiCueGainDb <= gain.Maximum
                ? preferences.WifiCueGainDb : 0;
            UpdateGain();
            gain.ValueChanged += delegate
            {
                UpdateGain();
                preferences.WifiCueGainDb = gain.Value;
                try { preferences.Save(); }
                catch (Exception ex) { SetStatus("Could not remember the cue gain. " + ex.Message); }
            };
            vendor.LinkClicked += delegate
            {
                try { Process.Start(new ProcessStartInfo("https://ndi.video/") { UseShellExecute = true }); }
                catch (Exception ex) { SetStatus(ex.Message); }
            };
            UpdateVoiceInfo();
            var sections = new Control[] { heading, directions, Row(sourceLabel, enterAddress), SelectRow(sources, find), addressRow, outputLabel, SelectRow(outputs, refreshOutputs),
                Row(Label("NDI cue gain"), gain, gainValue), setupInfo, Row(install, vendor), Row(start, stop), blocker, status, level, voiceInfo, Row(voice) };
            foreach (var c in sections) layout.Controls.Add(c);
            layout.SizeChanged += delegate
            {
                int width = Math.Max(140, layout.ClientSize.Width - layout.Padding.Horizontal - 10);
                foreach (Label label in layout.Controls.OfType<Label>()) label.MaximumSize = new Size(width, 0);
            };
            if (!SystemInformation.HighContrast)
            {
                BackColor = Color.FromArgb(29, 38, 48); ForeColor = Color.FromArgb(236, 243, 249);
                var field = Color.FromArgb(42, 54, 67); var accent = Color.FromArgb(111, 225, 194);
                foreach (ComboBox box in new[] { sources, outputs }) { box.BackColor = field; box.ForeColor = ForeColor; box.FlatStyle = FlatStyle.Flat; }
                tabletIp.BackColor = field; tabletIp.ForeColor = ForeColor; tabletIp.BorderStyle = BorderStyle.FixedSingle;
                foreach (Button b in new[] { find, findAt, refreshOutputs, install, start, stop, voice })
                {
                    b.FlatStyle = FlatStyle.Flat; b.FlatAppearance.BorderSize = 0;
                    b.BackColor = b == start ? accent : field; b.ForeColor = b == start ? BackColor : ForeColor;
                }
                status.ForeColor = enterAddress.LinkColor = enterAddress.VisitedLinkColor = vendor.LinkColor = vendor.VisitedLinkColor = accent;
            }
            sources.FormattingEnabled = true;
            sources.Format += delegate(object sender, ListControlConvertEventArgs e)
            {
                var s = e.ListItem as NdiSource;
                if (s != null) e.Value = duplicateSourceNames.Contains(s.Name) ?
                    s.Name + "  ·  " + (String.IsNullOrWhiteSpace(s.Address) ? "address unavailable" : s.Address) : s.Name;
            };
            sources.SelectedIndexChanged += delegate { RefreshAvailability(); };
            outputs.SelectedIndexChanged += delegate { RefreshAvailability(); };
            find.Click += async delegate { await FindAsync(); };
            enterAddress.LinkClicked += delegate
            {
                addressRow.Visible = !addressRow.Visible;
                enterAddress.Text = addressRow.Visible ? "Hide tablet address" : "Enter tablet address";
                if (addressRow.Visible)
                {
                    SetStatus("Enter the tablet’s IPv4 address from its Wi-Fi settings, then click Find at address.");
                    tabletIp.Focus(); tabletIp.SelectAll();
                }
            };
            findAt.Click += async delegate { await FindAsync(tabletIp.Text.Trim()); };
            tabletIp.KeyDown += delegate(object sender, KeyEventArgs e)
            {
                if (e.KeyCode != Keys.Enter) return;
                e.SuppressKeyPress = true; findAt.PerformClick();
            };
            install.Click += async delegate { await InstallAsync(); };
            refreshOutputs.Click += delegate { LoadOutputs(); };
            start.Click += async delegate { await StartAsync(); };
            stop.Click += async delegate { await RequestStopAsync(); };
            meter.Tick += async delegate { await ReadStatusAsync(); };
            voice.Click += delegate
            {
                var source = sources.SelectedItem as NdiSource;
                string address = VoiceAddress(source, tabletIp.Text, preferences.WifiCueSourceAddress);
                using (var dialog = new ConnectVoiceForm(address, microphone(), preferences: preferences)) dialog.ShowDialog(this);
                UpdateVoiceInfo();
            };
            Shown += async delegate
            {
                Rectangle area = Screen.FromControl(this).WorkingArea;
                if (Width > area.Width || Height > area.Height)
                {
                    Size = new Size(Math.Min(Width, area.Width), Math.Min(Height, area.Height));
                    Location = new Point(Math.Max(area.Left, Math.Min(Left, area.Right - Width)), Math.Max(area.Top, Math.Min(Top, area.Bottom - Height)));
                }
                LoadOutputs();
                await FindAsync(opening: true);
            };
            RefreshAvailability();
        }

        private void UpdateVoiceInfo()
        {
            voiceInfo.Text = ConnectSession.ConfiguredKey() == null ?
                "To try microphone commands over Wi-Fi, open Wi-Fi voice control and enter your OnSong developer key." :
                "For Windows microphone commands over the network, open Wi-Fi voice control. You don’t need to wait for the stream search.";
        }

        private void UpdateGain()
        {
            services.SetCueGain(gain.Value);
            gainValue.Text = gain.Value.ToString("+0;-0;0") + " dB";
        }

        private static string Host(string address)
        {
            if (String.IsNullOrWhiteSpace(address)) return "";
            Uri uri;
            return Uri.TryCreate(address.Contains("://") ? address : "http://" + address, UriKind.Absolute, out uri) ? uri.DnsSafeHost.Trim('[', ']') : "";
        }
        private static bool IsTabletIp(string text)
        {
            IPAddress parsed;
            return IPAddress.TryParse(text, out parsed) && parsed.AddressFamily == AddressFamily.InterNetwork && parsed.ToString() == text;
        }

        internal static string VoiceAddress(NdiSource selected, string entered, string saved)
        {
            var candidates = new[] { Host(selected == null ? null : selected.Address), (entered ?? "").Trim(), Host(saved) };
            foreach (string candidate in candidates)
            {
                IPAddress address;
                if (IPAddress.TryParse(candidate, out address) && address.ToString() == candidate &&
                    ConnectSession.IsPrivateTabletAddress(address)) return candidate;
            }
            return "";
        }
        private static Label Label(string text)
        { return new Label { Text = text, AutoSize = true, Margin = new Padding(0, 4, 0, 4), Anchor = AnchorStyles.Left }; }
        private sealed class CueButton : Button
        {
            protected override void OnPaint(PaintEventArgs e)
            {
                if (Enabled || SystemInformation.HighContrast) { base.OnPaint(e); return; }
                e.Graphics.Clear(Color.FromArgb(38, 47, 57));
                TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, Color.FromArgb(151, 168, 180),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
            }
        }
        private static Button Button(string text)
        { return new CueButton { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(9, 2, 9, 2), Margin = new Padding(0, 2, 7, 2) }; }
        private static ComboBox Picker(string name)
        { return new ComboBox { AccessibleName = name, Dock = DockStyle.Top, DropDownStyle = ComboBoxStyle.DropDownList, Margin = new Padding(0, 1, 0, 3) }; }
        private static Control SelectRow(Control picker, Button action)
        {
            var row = new TableLayoutPanel { AutoSize = true, ColumnCount = 2, Dock = DockStyle.Top, Margin = Padding.Empty };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            picker.Margin = new Padding(0, 4, 8, 0);
            row.Controls.Add(picker, 0, 0);
            row.Controls.Add(action, 1, 0);
            return row;
        }
        private static FlowLayoutPanel Row(params Control[] children)
        {
            var row = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Top, Margin = Padding.Empty };
            row.Controls.AddRange(children);
            return row;
        }

        private void SetStatus(string text)
        {
            if (status.Text == text) return;
            status.Text = text;
            status.AccessibleName = "Wi-Fi cue status. " + text;
            if (PlaybackBusy && PlaybackChanged != null) PlaybackChanged();
        }
        private void State(Activity state)
        {
            current = state;
            RefreshAvailability();
            if (PlaybackChanged != null) PlaybackChanged();
        }
        internal void RefreshAvailability()
        {
            bool idle = !closeRequested && current == Activity.Idle;
            bool otherAudio = !canPlay();
            gain.Enabled = !closeRequested && current != Activity.Stopping;
            find.Enabled = sources.Enabled = outputs.Enabled = refreshOutputs.Enabled = idle;
            enterAddress.Enabled = tabletIp.Enabled = findAt.Enabled = idle;
            install.Visible = vendor.Visible = setupInfo.Visible = String.IsNullOrEmpty(runtime);
            install.Enabled = idle;
            start.Enabled = idle && !otherAudio && !String.IsNullOrEmpty(runtime) && sources.SelectedItem != null && outputs.SelectedItem != null;
            stop.Enabled = !closeRequested && (current == Activity.Starting || current == Activity.Receiving) && !(operation != null && operation.IsCancellationRequested);
            // OnSong Connect is independent of NDI discovery and playback setup. Musicians can pair and test
            // voice commands while the stream search runs or the runtime installer is open.
            voice.Enabled = !closeRequested && current != Activity.Starting && current != Activity.Stopping;
            blocker.Text = otherAudio && !PlaybackBusy ? "Stop Android mirrored cue audio in the main window before starting Wi-Fi playback." : "";
            blocker.Visible = blocker.Text.Length > 0;
        }
        private void LoadOutputs()
        {
            try
            {
                var selected = outputs.SelectedItem as AudioDevice;
                string keep = selected != null ? selected.EndpointId : preferences.WifiCueOutputEndpointId ?? preferences.OutputEndpointId;
                if (String.IsNullOrWhiteSpace(keep)) keep = services.DefaultOutput();
                var devices = services.Outputs().Where(device => !String.IsNullOrWhiteSpace(device.EndpointId)).ToArray();
                outputs.Items.Clear(); outputs.Items.AddRange(devices);
                var matches = devices.Where(device => String.Equals(keep, device.EndpointId, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (matches.Length == 1) outputs.SelectedItem = matches[0];
            }
            catch (Exception ex) { outputs.Items.Clear(); SetStatus("Could not list Windows outputs. " + ex.Message); }
            RefreshAvailability();
        }

        private async Task FindAsync(string address = null, bool opening = false)
        {
            if (current != Activity.Idle || closeRequested) return;
            if (address != null && !IsTabletIp(address))
            {
                SetStatus("Enter the IP address shown in the tablet’s Wi-Fi settings, without a port.");
                tabletIp.Focus(); return;
            }
            var previous = sources.SelectedItem as NdiSource;
            string keep = previous == null ? preferences.WifiCueSource : previous.Name;
            string keepAddress = previous == null ? preferences.WifiCueSourceAddress : previous.Address;
            var cancel = operation = new CancellationTokenSource();
            State(Activity.Finding);
            sources.Items.Clear();
            duplicateSourceNames.Clear();
            SetStatus((address == null ? "Looking for tablet streams. " : "Checking " + address + ". ") + AnnounceAgain);
            try
            {
                runtime = services.FindRuntime();
                RefreshAvailability();
                var found = address == null ? await FindAvailableAsync(opening, cancel.Token) : await services.DiscoverAtAsync(address, cancel.Token);
                cancel.Token.ThrowIfCancellationRequested();
                foreach (var group in found.GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
                    duplicateSourceNames.Add(group.Key);
                foreach (NdiSource source in found.OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase)) sources.Items.Add(source);
                var named = found.Where(s => Same(s.Name, keep)).ToList();
                var exact = named.Where(s => !String.IsNullOrEmpty(keepAddress) && Same(s.Address, keepAddress)).ToList();
                var sameDevice = named.Where(s => !String.IsNullOrEmpty(Host(keepAddress)) && Same(Host(s.Address), Host(keepAddress))).ToList();
                if (exact.Count == 1) sources.SelectedItem = exact[0];
                else if (sameDevice.Count == 1) sources.SelectedItem = sameDevice[0];
                else if (named.Count == 1 && String.IsNullOrEmpty(keepAddress)) sources.SelectedItem = named[0];
                else if (named.Count == 0 || address != null)
                {
                    var likely = found.Where(s => TabletNames.Any(
                        hint => s.Name.IndexOf(hint, StringComparison.OrdinalIgnoreCase) >= 0)).ToList();
                    if (likely.Count == 1) sources.SelectedItem = likely[0];
                }
                SetStatus(found.Count == 0 ? "No stream found. Check the Wi-Fi network, then search again. " + AnnounceAgain :
                    outputs.Items.Count == 0 ? "Tablet stream found. Connect a Windows playback device, then click Refresh outputs." :
                    found.Count + (found.Count == 1 ? " stream found. " : " streams found. ") +
                    "Choose the source shown in OnSong and the Windows output feeding your mixer.");
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { SetStatus("Could not find tablets. " + ex.Message); }
            finally { Finish(cancel, Activity.Idle); }
        }

        private static bool Same(string left, string right) { return String.Equals(left, right, StringComparison.OrdinalIgnoreCase); }

        private async Task<List<NdiSource>> FindAvailableAsync(bool opening, CancellationToken token)
        {
            string remembered = Host(preferences.WifiCueSourceAddress);
            bool canRetry = !String.IsNullOrEmpty(runtime) && IsTabletIp(remembered);
            List<NdiSource> direct = null;
            if (opening && canRetry && !String.IsNullOrWhiteSpace(preferences.WifiCueSource))
            {
                SetStatus("Checking your saved tablet at " + remembered + "…");
                try { direct = await services.DiscoverAtAsync(remembered, token); }
                catch (OperationCanceledException) { throw; }
                catch (Exception) { direct = new List<NdiSource>(); }
                token.ThrowIfCancellationRequested();
                var matches = direct.Where(s => Same(s.Name, preferences.WifiCueSource) && Same(Host(s.Address), remembered)).ToList();
                if (matches.Count == 1 || matches.Count(s => Same(s.Address, preferences.WifiCueSourceAddress)) == 1) return direct;
                SetStatus("Looking for tablet streams on this network. " + AnnounceAgain);
            }
            List<NdiSource> found;
            try { found = new List<NdiSource>(await services.DiscoverAsync(token)); }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { if (!canRetry || (direct != null && direct.Count == 0)) throw; found = new List<NdiSource>(); }
            token.ThrowIfCancellationRequested();
            if (direct == null)
            {
                if (!canRetry || found.Any(s => Same(Host(s.Address), remembered))) return found;
                SetStatus("Checking the saved tablet at " + remembered + ". " + AnnounceAgain);
                try { direct = await services.DiscoverAtAsync(remembered, token); }
                catch (OperationCanceledException) { throw; }
                catch (Exception) { if (found.Count == 0) throw; return found; }
            }
            foreach (var source in direct)
                if (!found.Any(s => Same(s.Name, source.Name) && Same(s.Address, source.Address))) found.Add(source);
            return found;
        }

        private async Task InstallAsync()
        {
            if (current != Activity.Idle || closeRequested) return;
            var cancel = operation = new CancellationTokenSource();
            State(Activity.Installing);
            SetStatus("Opening the official NDI Runtime setup. Complete its installer to enable playback.");
            try
            {
                await services.InstallAsync(cancel.Token);
                cancel.Token.ThrowIfCancellationRequested();
                runtime = services.FindRuntime();
                SetStatus(String.IsNullOrEmpty(runtime) ? "Setup closed before the runtime became available. Run Set up Wi-Fi playback to try again." :
                    "Playback is ready. Choose your tablet stream and Windows output, then click Start Wi-Fi cues.");
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { SetStatus("Playback setup did not finish. " + ex.Message); }
            finally { Finish(cancel, Activity.Idle); }
        }

        private async Task StartAsync()
        {
            if (!start.Enabled || current != Activity.Idle || !canPlay()) return;
            var stream = (NdiSource)sources.SelectedItem;
            var device = (AudioDevice)outputs.SelectedItem;
            var cancel = operation = new CancellationTokenSource();
            bool started = false;
            hasSession = true;
            savedSource = false;
            State(Activity.Starting);
            level.Text = "";
            level.AccessibleDescription = null;
            audioDetails.SetToolTip(level, null);
            SetStatus("Opening " + stream.Name + " on " + device.DisplayName + "…");
            try
            {
                await services.StartAsync(runtime, stream, device.EndpointId, cancel.Token);
                cancel.Token.ThrowIfCancellationRequested();
                started = true;
                preferences.WifiCueOutputEndpointId = device.EndpointId;
                SetStatus("Listening for " + stream.Name + " → " + device.DisplayName);
                try { preferences.Save(); } catch (Exception ex) { level.Text = "Could not remember your output. " + ex.Message; }
                meter.Start();
            }
            catch (OperationCanceledException) { SetStatus("Wi-Fi cue playback stopped."); }
            catch (Exception ex) { SetStatus("Could not start Wi-Fi cues. " + ex.Message); }
            try { if (!started) await ReleaseAsync(); }
            finally { Finish(cancel, started ? Activity.Receiving : Activity.Idle); }
        }

        private async Task ReadStatusAsync()
        {
            if (current != Activity.Receiving) return;
            string error = null;
            try
            {
                var s = services.GetStatus();
                if (!String.IsNullOrEmpty(s.Error) || !s.Running)
                {
                    await StopAsync(String.IsNullOrEmpty(s.Error) ? "The Wi-Fi receiver stopped." : s.Error);
                    return;
                }
                string destination = ((AudioDevice)outputs.SelectedItem).DisplayName;
                if (!savedSource && s.Connected && s.PlayedFrames > 0)
                {
                    savedSource = true;
                    var stream = (NdiSource)sources.SelectedItem;
                    string endpoint = String.IsNullOrEmpty(Host(stream.Address)) ? null : stream.Address;
                    if (!Same(preferences.WifiCueSource, stream.Name) || !Same(preferences.WifiCueSourceAddress, endpoint))
                    {
                        preferences.WifiCueSource = stream.Name; preferences.WifiCueSourceAddress = endpoint;
                        string savedIp = Host(endpoint);
                        tabletIp.Text = IsTabletIp(savedIp) ? savedIp : "";
                        try { preferences.Save(); }
                        catch (Exception ex) { level.Text = "Could not remember the tablet. " + ex.Message; }
                    }
                }
                bool hasSignal = s.LastSignalUtc != DateTime.MinValue && DateTime.UtcNow - s.LastSignalUtc < TimeSpan.FromSeconds(2);
                SetStatus(!s.Connected ? "Waiting for the tablet stream. Keep OnSong live." :
                    s.AudioFrames == 0 ? "Tablet connected. Waiting for audio. Check Send Audio in OnSong." :
                    hasSignal ? "Audio signal received → " + destination :
                    s.LastSignalUtc != DateTime.MinValue ? "Ready for the next cue → " + destination + ". Last signal " + s.LastSignalUtc.ToLocalTime().ToString("T") + "." :
                    "Listening → " + destination + ". Tap an enabled song or section cue in OnSong.");
                var measurements = new List<string>();
                if (s.AudioFrames > 0)
                {
                    measurements.Add(String.Format("{0} Hz · {1} channels · {2}", s.SampleRate, s.Channels,
                        s.Peak > 0 ? "Input " + (20 * Math.Log10(s.Peak)).ToString("0.0") + " dBFS" : "Silence"));
                    if (s.OutputPeak > 0)
                        measurements.Add("Cue output " + (20 * Math.Log10(s.OutputPeak)).ToString("0.0") + " dBFS");
                    if (s.OutputClipped) measurements.Add("Clipping — lower NDI cue gain");
                }
                if (s.DroppedAudioPackets > 0) measurements.Add(String.Format("{0:N0} NDI audio packets dropped at receiver", s.DroppedAudioPackets));
                if (s.DroppedFrames > 0) measurements.Add(String.Format("{0:N0} output sample frames dropped", s.DroppedFrames));
                level.Text = String.Join(" · ", measurements);
                string counts = String.Format("{0:N0} audio packets received. {1:N0} audio packets dropped in the NDI receiver queue. " +
                    "{2:N0} output sample frames played. {3:N0} output sample frames dropped.",
                    s.AudioFrames, s.DroppedAudioPackets, s.PlayedFrames, s.DroppedFrames);
                if (level.AccessibleDescription != counts)
                {
                    level.AccessibleDescription = counts;
                    audioDetails.SetToolTip(level, counts);
                }
            }
            catch (Exception ex) { error = "Wi-Fi cue playback ended. " + ex.Message; }
            if (error != null) await StopAsync(error);
        }
        private async Task RequestStopAsync()
        {
            if (current == Activity.Starting && operation != null)
            {
                operation.Cancel(); RefreshAvailability(); return;
            }
            if (current == Activity.Receiving) await StopAsync("Wi-Fi cue playback stopped.");
        }
        private async Task StopAsync(string message)
        {
            if (current != Activity.Receiving) return;
            meter.Stop();
            State(Activity.Stopping);
            SetStatus(message);
            await ReleaseAsync();
            level.Text = "";
            State(Activity.Idle);
            FinishClosing();
        }
        private async Task ReleaseAsync()
        {
            if (!hasSession) return;
            try { await services.StopAsync(); }
            catch (Exception ex) { SetStatus("Receiver cleanup: " + ex.Message); }
            finally { hasSession = false; }
        }
        private void Finish(CancellationTokenSource cancel, Activity next)
        {
            operation = null;
            cancel.Dispose();
            State(next);
            FinishClosing();
        }
        private void FinishClosing()
        {
            if (!closeRequested || operation != null || hasSession || released) return;
            released = true;
            BeginInvoke(new Action(Close));
        }
        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);
            if (released || (operation == null && !hasSession)) { meter.Dispose(); audioDetails.Dispose(); return; }
            e.Cancel = true;
            closeRequested = true;
            RefreshAvailability();
            if (operation != null) operation.Cancel();
            else if (current == Activity.Receiving) BeginInvoke(new Action(async delegate { await RequestStopAsync(); }));
        }
    }
}

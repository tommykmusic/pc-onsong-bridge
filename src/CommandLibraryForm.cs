using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PcOnSongBridge
{
    internal sealed class CommandLibraryForm : BridgeWindow
    {
        private sealed class Item
        {
            internal OnSongAction Action;
            internal VoiceBinding Binding;
            internal VoiceCommand Command;
            internal string ActionId { get { return Action == null ? "custom" : Action.Id; } }
            internal string Key { get { return Binding == null ? ActionId : Binding.Id; } }
            internal string Category { get { return Action == null ? "Custom" : Action.Category; } }
            internal string Title { get { return Binding != null ? Binding.Phrase : Action == null ? "New custom action" : Action.Title; } }
            internal string SearchText;
            public override string ToString() { return (Command == null ? "○  " : "●  ") + Title + "  ·  " + Category; }
        }

        private const string AllCategories = "All categories";
        private static readonly string[] NativeOrder = { "next", "previous", "scroll-up", "scroll-down" };
        private readonly Preferences preferences;
        private readonly TabletControlMode mode;
        private readonly Func<IEnumerable<VoiceCommand>, Action, CancellationToken, Task<MicrophoneResult>> capture;
        private readonly Func<VoiceCommand, string, Action, CancellationToken, Task<CommandDelivery>> dispatch;
        private readonly Func<VoiceShortcut, CancellationToken, Task<string>> teachShortcut;
        private readonly Func<CancellationToken, Task<string>> prepareSong;
        private readonly List<CommandHistoryEntry> history;
        private readonly string microphoneName, tabletLabel, readinessNote;
        private readonly TabControl tabs = new TabControl { Dock = DockStyle.Fill };
        private readonly TextBox search = new TextBox { Dock = DockStyle.Fill, AccessibleName = "Search commands" };
        private readonly ComboBox category = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList, AccessibleName = "Category" };
        private readonly CheckBox readyOnly = new CheckBox { Text = "Ready only", AutoSize = true };
        private readonly ListBox list = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false, AccessibleName = "Commands", HorizontalScrollbar = true };
        private readonly Label title = Line(true);
        private readonly Label status = Line(false);
        private readonly Label shortcutHelp = Line(false);
        private readonly LinkLabel reference = new LinkLabel { Text = "OnSong reference", AutoSize = true };
        private readonly TextBox phrase = Field("Voice phrase");
        private readonly TextBox shortcut = Field("Keyboard shortcut");
        private readonly Label shortcutCaption = Caption("OnSong keyboard shortcut");
        private readonly Button save = Button("Save mapping"), add = Button("Add phrase"), remove = Button("Remove mapping");
        private readonly Button teach = Button("Teach shortcut to tablet");
        private readonly Button custom = Button("New custom action");
        private readonly Button speak = Button("Speak and send"), send = Button("Send once"), cancel = Button("Cancel test");
        private readonly Button worked = Button("It worked"), failed = Button("No effect");
        private readonly TextBox result = Readout("Current test result");
        private readonly ListBox historyList = new ListBox { Dock = DockStyle.Fill, IntegralHeight = false, HorizontalScrollbar = true, AccessibleName = "Test history" };
        private readonly TextBox detail = Readout("Recorded test details");
        private readonly Label count = Line(false);
        private readonly List<Item> items = new List<Item>();
        private Item selected;
        private bool rebuilding, makingMapping, closing;
        private CancellationTokenSource operation;
        private CommandHistoryEntry pendingFeedback, last;

        internal CommandLibraryForm(Preferences prefs, TabletControlMode mode,
            Func<IEnumerable<VoiceCommand>, Action, CancellationToken, Task<MicrophoneResult>> capture,
            Func<VoiceCommand, string, Action, CancellationToken, Task<CommandDelivery>> dispatch, List<CommandHistoryEntry> history,
            string microphoneName = null, string tabletLabel = null, string readinessNote = null,
            Func<VoiceShortcut, CancellationToken, Task<string>> teach = null, Func<CancellationToken, Task<string>> prepareSong = null)
        {
            this.preferences = prefs;
            this.mode = mode;
            this.capture = capture;
            this.dispatch = dispatch;
            this.teachShortcut = teach;
            this.prepareSong = prepareSong;
            this.history = history;
            this.microphoneName = microphoneName;
            this.tabletLabel = tabletLabel;
            this.readinessNote = readinessNote;
            Text = "Command library and soundcheck";
            Font = new Font("Segoe UI", 9.5f);
            AutoScaleDimensions = new SizeF(96, 96);
            AutoScaleMode = AutoScaleMode.Dpi;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(945, 615);
            MinimumSize = new Size(510, 355);
            ShowInTaskbar = false;
            var library = new TabPage("Commands");
            var log = new TabPage("Test history");
            tabs.TabPages.Add(library);
            tabs.TabPages.Add(log);
            Controls.Add(tabs);
            BuildLibrary(library);
            BuildHistory(log);
            if (!SystemInformation.HighContrast) ApplyPalette();
            category.Items.Add(AllCategories);
            category.Items.AddRange(OnSongActions.All.Select(a => a.Category).Concat(new[] { "Custom" }).Distinct().OrderBy(x => x).ToArray());
            category.SelectedIndex = 0;
            Populate("next");
            RefreshHistory();

            search.TextChanged += delegate { Filter(null); };
            category.SelectedIndexChanged += delegate { Filter(null); };
            readyOnly.CheckedChanged += delegate { Filter(null); };
            list.SelectedIndexChanged += delegate { if (!rebuilding) SelectItem(list.SelectedItem as Item); };
            phrase.TextChanged += delegate { RefreshButtons(); };
            shortcut.TextChanged += delegate { RefreshButtons(); };
            custom.Click += delegate { StartMapping(true); };
            add.Click += delegate { StartMapping(false); };
            remove.Click += delegate { SaveMapping(true); };
            save.Click += delegate { SaveMapping(false); };
            this.teach.Click += async delegate { await TeachAsync(); };
            reference.LinkClicked += delegate
            {
                if (selected == null || selected.Action == null) return;
                try { Process.Start(new ProcessStartInfo(selected.Action.SourceUrl) { UseShellExecute = true }); }
                catch (Exception ex) { result.Text = ex.Message; }
            };
            speak.Click += async delegate { await TestAsync(true); };
            send.Click += async delegate { await TestAsync(false); };
            cancel.Click += delegate { CancelPending(); };
            worked.Click += delegate { Feedback(true); };
            failed.Click += delegate { Feedback(false); };
            historyList.SelectedIndexChanged += delegate { ShowHistoryDetails(); };
            Shown += delegate
            {
                Rectangle area = Screen.FromControl(this).WorkingArea;
                if (Width > area.Width || Height > area.Height)
                {
                    MinimumSize = new Size(Math.Min(MinimumSize.Width, area.Width), Math.Min(MinimumSize.Height, area.Height));
                    Size = new Size(Math.Min(Width, area.Width), Math.Min(Height, area.Height));
                    Location = new Point(Math.Max(area.Left, Math.Min(Left, area.Right - Width)), Math.Max(area.Top, Math.Min(Top, area.Bottom - Height)));
                }
            };
        }

        internal string LastSummary { get { return last == null ? null : last.Summary; } }

        private void ApplyPalette()
        {
            var background = Color.FromArgb(19, 25, 32);
            var surface = Color.FromArgb(29, 38, 48);
            var fieldColor = Color.FromArgb(42, 54, 67);
            var foreground = Color.FromArgb(236, 243, 249);
            var muted = Color.FromArgb(177, 195, 207);
            var accent = Color.FromArgb(111, 225, 194);
            BackColor = background;
            ForeColor = foreground;
            tabs.DrawMode = TabDrawMode.OwnerDrawFixed;
            tabs.DrawItem += delegate(object sender, DrawItemEventArgs e)
            {
                bool current = e.Index == tabs.SelectedIndex;
                using (var brush = new SolidBrush(current ? surface : background)) e.Graphics.FillRectangle(brush, e.Bounds);
                TextRenderer.DrawText(e.Graphics, tabs.TabPages[e.Index].Text, tabs.Font, e.Bounds,
                    current ? accent : muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
                if (tabs.Focused && current) e.DrawFocusRectangle();
            };
            foreach (TabPage page in tabs.TabPages)
            {
                page.UseVisualStyleBackColor = false;
                page.BackColor = surface;
                page.ForeColor = foreground;
            }
            foreach (var field in new[] { phrase, shortcut, search, result, detail })
            {
                field.BackColor = fieldColor;
                field.ForeColor = foreground;
                field.BorderStyle = BorderStyle.FixedSingle;
            }
            foreach (var box in new[] { list, historyList })
            {
                box.BackColor = background;
                box.ForeColor = foreground;
                box.BorderStyle = BorderStyle.FixedSingle;
            }
            category.BackColor = fieldColor;
            category.ForeColor = foreground;
            category.FlatStyle = FlatStyle.Flat;
            title.ForeColor = foreground;
            status.ForeColor = count.ForeColor = shortcutHelp.ForeColor = muted;
            reference.LinkColor = reference.VisitedLinkColor = accent;
            reference.ActiveLinkColor = foreground;
            foreach (Button button in new[] { save, add, remove, teach, custom, speak, send, cancel, worked, failed })
            {
                button.UseVisualStyleBackColor = false;
                button.FlatStyle = FlatStyle.Flat;
                button.BackColor = button == speak ? accent : fieldColor;
                button.ForeColor = button == speak ? background : foreground;
                button.FlatAppearance.BorderColor = Color.FromArgb(92, 113, 129);
                button.FlatAppearance.MouseOverBackColor = button == speak ? Color.FromArgb(151, 239, 215) : Color.FromArgb(54, 71, 88);
            }
        }

        private static Label Line(bool heading)
        {
            var label = new Label { AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 4, 3, 3) };
            if (heading) label.Font = new Font("Segoe UI", 14f, FontStyle.Bold);
            return label;
        }
        private static Button Button(string text) { return new Button { Text = text, AutoSize = true, Margin = new Padding(0, 2, 5, 2) }; }
        private static TextBox Field(string name) { return new TextBox { Dock = DockStyle.Top, AccessibleName = name }; }
        private static TextBox Readout(string name)
        {
            return new TextBox { AccessibleName = name, Dock = DockStyle.Fill, Multiline = true, ReadOnly = true,
                ScrollBars = ScrollBars.Vertical, BackColor = SystemColors.Window };
        }
        private static FlowLayoutPanel Row(params Control[] controls)
        {
            var row = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, Margin = Padding.Empty, WrapContents = true };
            row.Controls.AddRange(controls);
            return row;
        }
        private static Label Caption(string text)
        {
            var label = Line(false);
            label.Text = text;
            return label;
        }
        private static TableLayoutPanel Column()
        {
            var panel = new TableLayoutPanel { ColumnCount = 1, Dock = DockStyle.Fill, Margin = Padding.Empty };
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            return panel;
        }
        private static void Place(TableLayoutPanel panel, Control control, bool stretch = false)
        {
            int row = panel.RowCount++;
            panel.RowStyles.Add(new RowStyle(stretch ? SizeType.Percent : SizeType.AutoSize, stretch ? 100 : 0));
            panel.Controls.Add(control, 0, row);
        }

        private void BuildLibrary(TabPage page)
        {
            var columns = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Padding = new Padding(8) };
            columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 37));
            columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 63));
            page.Controls.Add(columns);
            var left = Column();
            left.Padding = new Padding(0, 0, 8, 0);
            columns.Controls.Add(left, 0, 0);
            Place(left, Caption("Search actions and phrases")); Place(left, search);
            Place(left, category); Place(left, readyOnly);
            Place(left, list, true); Place(left, count); Place(left, custom);
            var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
            columns.Controls.Add(scroll, 1, 0);
            var right = Column();
            right.Dock = DockStyle.Top;
            right.AutoSize = true;
            right.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            right.Padding = new Padding(7, 0, 5, 4);
            scroll.Controls.Add(right);
            Place(right, title); Place(right, status); Place(right, reference);
            Place(right, Caption("Voice phrase")); Place(right, phrase);
            Place(right, shortcutCaption); Place(right, shortcut);
            Place(right, shortcutHelp); Place(right, Row(save, add, remove));
            Place(right, Row(teach));
            Place(right, Row(speak, send, cancel)); Place(right, Row(worked, failed));
            result.Height = 130;
            result.Dock = DockStyle.Top;
            Place(right, result);
            right.SizeChanged += delegate
            {
                int w = Math.Max(110, right.ClientSize.Width - right.Padding.Horizontal - 18);
                if (status.MaximumSize.Width == w) return;
                title.MaximumSize = status.MaximumSize = shortcutHelp.MaximumSize = new Size(w, 0);
            };
        }

        private void BuildHistory(TabPage page)
        {
            var panel = Column();
            panel.Padding = new Padding(10);
            page.Controls.Add(panel);
            Place(panel, Caption("Tests in this bridge session"));
            panel.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
            panel.Controls.Add(historyList, 0, panel.RowCount++);
            panel.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
            panel.Controls.Add(detail, 0, panel.RowCount++);
        }

        private void Populate(string key)
        {
            items.Clear();
            var bindings = preferences.VoiceBindings ?? new List<VoiceBinding>();
            var commands = VoiceCommands.ForMode(mode, bindings).ToDictionary(c => c.Id);
            foreach (var action in OnSongActions.All)
            {
                var matches = bindings.Where(b => b != null && b.ActionId == action.Id).ToList();
                VoiceCommand native;
                commands.TryGetValue(action.Id, out native);
                if (native != null || matches.Count == 0) items.Add(new Item { Action = action, Command = native });
                foreach (var binding in matches) AddBinding(action, binding, commands);
            }
            foreach (var binding in bindings.Where(b => b != null && b.ActionId == "custom")) AddBinding(null, binding, commands);
            var builtIns = NativeOrder.SelectMany(id => items.Where(item => item.Key == id)).ToList();
            var configured = builtIns.Concat(items.Where(item => item.Binding != null).OrderBy(item => item.Title)).ToList();
            foreach (var item in configured) items.Remove(item);
            items.InsertRange(0, configured);
            foreach (var item in items)
                item.SearchText = String.Join(" ", item.Title, item.Category, item.Action == null ? "" : item.Action.Title,
                    item.Action == null ? "" : VoiceCommands.SuggestedPhrase(item.Action), item.Command == null ? "" : item.Command.Phrase,
                    item.Binding == null ? "" : item.Binding.Shortcut);
            Filter(key);
        }

        private void AddBinding(OnSongAction action, VoiceBinding binding, Dictionary<string, VoiceCommand> commands)
        {
            VoiceCommand command;
            commands.TryGetValue(binding.Id ?? "", out command);
            items.Add(new Item { Action = action, Binding = binding, Command = command });
        }

        private void Filter(string key)
        {
            if (rebuilding) return;
            string keep = key ?? (selected == null ? null : selected.Key);
            string[] words = Regex.Split(search.Text.Trim(), @"[^\p{L}\p{M}\p{Nd}]+").Where(word => word.Length != 0).ToArray();
            rebuilding = true;
            list.BeginUpdate();
            try
            {
                list.Items.Clear();
                foreach (var item in items)
                {
                    if (readyOnly.Checked && item.Command == null) continue;
                    if (category.SelectedIndex > 0 && item.Category != (string)category.SelectedItem) continue;
                    if (words.Any(word => item.SearchText.IndexOf(word, StringComparison.CurrentCultureIgnoreCase) < 0)) continue;
                    int i = list.Items.Add(item);
                    if (item.Key == keep) list.SelectedIndex = i;
                }
                if (list.SelectedIndex < 0 && list.Items.Count > 0) list.SelectedIndex = 0;
                count.Text = list.Items.Count + " shown  ·  ● Ready   ○ Needs mapping";
            }
            finally { list.EndUpdate(); rebuilding = false; }
            SelectItem(list.SelectedItem as Item);
        }

        private void SelectItem(Item item)
        {
            selected = item;
            makingMapping = item != null && item.Binding == null && item.Command == null;
            title.Text = item == null ? "No matching commands" : item.Action == null ? item.Title : item.Action.Title;
            reference.Visible = item != null && item.Action != null && !String.IsNullOrEmpty(item.Action.SourceUrl);
            phrase.Text = item == null ? "" : item.Binding != null ? item.Binding.Phrase : item.Command != null ? item.Command.Phrase : VoiceCommands.SuggestedPhrase(item.Action);
            shortcut.Text = item == null ? "" : item.Binding != null ? item.Binding.Shortcut : item.Command == null ? "" : item.Command.Shortcut;
            ShowSelection();
        }

        private void ShowSelection()
        {
            if (selected == null)
            {
                status.Text = "Try another search or category.";
                shortcutHelp.Text = result.Text = "";
                RefreshButtons();
                return;
            }
            bool editable = selected.Binding != null || makingMapping;
            phrase.ReadOnly = shortcut.ReadOnly = !editable;
            status.Text = selected.Category + "  ·  " + (selected.Command == null || makingMapping ? "Needs mapping" :
                selected.Binding != null ? "Mapped shortcut" : "Built-in bridge command");
            shortcutHelp.Text = editable ? "Assign the same shortcut in OnSong’s Actions Editor → Shortcuts if your tablet supports the action. For example, use Ctrl+Shift+K." :
                "This command uses the selected tablet control mode. Add a phrase to create a separate keyboard mapping.";
            if (!makingMapping && selected.Command != null && !String.IsNullOrEmpty(selected.Command.Shortcut))
                shortcutHelp.Text = "Open Actions Editor → Shortcuts on the tablet. Click Teach shortcut to tablet, then choose the matching OnSong action there.";
            if (selected.Binding == null && !makingMapping && selected.Command != null && !String.IsNullOrEmpty(selected.Command.Shortcut))
                shortcutHelp.Text = "In OnSong’s Actions Editor → Shortcuts, teach this key and assign Navigation → Song → " +
                    (selected.Command.Id == "next" ? "Next." : "Previous.");
            if (editable && (selected.ActionId == "custom" || selected.ActionId == "navigation-song-switch" || selected.ActionId == "navigation-section-switch"))
                shortcutHelp.Text += " For a named song or section, choose Navigation → Song or Section → Switch in OnSong and select its target.";
            if (selected.Action != null && !String.IsNullOrEmpty(selected.Action.DefaultShortcut))
                shortcutHelp.Text += " OnSong documents " + selected.Action.DefaultShortcut + ".";
            var record = history.LastOrDefault(h => h.CommandId == selected.Key);
            result.Text = record != null ? Details(record) : selected.Command == null || makingMapping
                ? "Save a phrase and the shortcut assigned on your tablet to enable testing." : Readiness();
            RefreshButtons();
        }

        private string Readiness()
        {
            if (dispatch == null) return String.IsNullOrWhiteSpace(readinessNote) ? "Connect a tablet and check navigation to send a test." : readinessNote;
            if (capture == null) return "Manual testing is available. Select a microphone and an English desktop recognizer for voice tests.";
            if (prepareSong != null && selected != null && (selected.Key == "next" || selected.Key == "previous"))
                return "Select Speak and send. The soundcheck reads the starting chart. Wait for Listening, then say the phrase.";
            return "Select Speak and send, wait for Listening, then say the displayed phrase. Send once tests the action without the microphone.";
        }

        private void StartMapping(bool free)
        {
            if (operation != null) return;
            if (free)
            {
                selected = new Item();
                rebuilding = true;
                list.ClearSelected();
                rebuilding = false;
                title.Text = "New custom action";
                reference.Visible = false;
            }
            makingMapping = true;
            if (free) phrase.Text = "";
            else
            {
                string suggested = selected.Action == null ? selected.Title : VoiceCommands.SuggestedPhrase(selected.Action);
                switch (selected.ActionId)
                {
                    case "next": suggested = "go to next song"; break;
                    case "previous": suggested = "go to previous song"; break;
                    case "scroll-up": suggested = "scroll chart up"; break;
                    case "scroll-down": suggested = "scroll chart down"; break;
                }
                phrase.Text = suggested;
            }
            shortcut.Text = "";
            ShowSelection();
            phrase.Focus();
            phrase.SelectAll();
        }

        private bool IsDirty()
        {
            return makingMapping || (selected != null && selected.Binding != null &&
                (phrase.Text != selected.Binding.Phrase || shortcut.Text != selected.Binding.Shortcut));
        }

        private void SaveMapping(bool deleting)
        {
            if (selected == null || operation != null) return;
            var before = preferences.VoiceBindings;
            var next = before == null ? new List<VoiceBinding>() : new List<VoiceBinding>(before);
            var old = makingMapping ? null : selected.Binding;
            try
            {
                string key = selected.ActionId;
                if (deleting && old == null) return;
                if (old != null) next.Remove(old);
                if (!deleting)
                {
                    var binding = VoiceCommands.CreateBinding(selected.ActionId, phrase.Text.Trim(), shortcut.Text.Trim(), next,
                        old == null ? null : old.Id);
                    next.Add(binding);
                    key = binding.Id;
                }
                preferences.VoiceBindings = next;
                try { preferences.Save(); }
                catch { preferences.VoiceBindings = before; throw; }
                rebuilding = true;
                search.Clear(); category.SelectedIndex = 0; readyOnly.Checked = false;
                rebuilding = false;
                Populate(key);
                result.Text = deleting ? "Mapping removed." : "Mapping saved. Confirm that this shortcut triggers the intended action in OnSong.";
            }
            catch (Exception ex) { result.Text = ex.Message; }
        }

        private void RefreshButtons()
        {
            bool idle = operation == null && !closing;
            bool editable = selected != null && (selected.Binding != null || makingMapping);
            bool testable = idle && selected != null && selected.Command != null && !IsDirty() && dispatch != null;
            save.Enabled = idle && editable && IsDirty();
            add.Enabled = idle && selected != null && !makingMapping;
            remove.Enabled = idle && !makingMapping && selected != null && selected.Binding != null;
            save.Visible = editable;
            add.Visible = selected != null && !makingMapping;
            remove.Visible = selected != null && selected.Binding != null && !makingMapping;
            bool hasShortcut = selected != null && selected.Command != null && !String.IsNullOrEmpty(selected.Command.Shortcut);
            shortcut.Visible = shortcutCaption.Visible = editable || hasShortcut;
            teach.Visible = hasShortcut && !makingMapping;
            teach.Enabled = idle && hasShortcut && !IsDirty() && teachShortcut != null;
            phrase.Enabled = shortcut.Enabled = idle && selected != null;
            speak.Enabled = testable && capture != null;
            send.Enabled = testable;
            cancel.Enabled = operation != null && !operation.IsCancellationRequested;
            cancel.Visible = operation != null;
            custom.Enabled = list.Enabled = search.Enabled = category.Enabled = readyOnly.Enabled = idle;
            bool confirmable = idle && selected != null && pendingFeedback != null && pendingFeedback.CommandId == selected.Key;
            worked.Enabled = failed.Enabled = worked.Visible = failed.Visible = confirmable;
        }

        private async Task TeachAsync()
        {
            if (operation != null || !teach.Enabled || teachShortcut == null || selected == null || selected.Command == null) return;
            var tokenSource = new CancellationTokenSource();
            operation = tokenSource;
            cancel.Text = "Cancel teaching";
            RefreshButtons();
            try
            {
                var key = VoiceShortcut.Parse(selected.Command.Shortcut);
                result.Text = "Checking the tablet’s Shortcuts editor and teaching " + key.Label + "…";
                result.Text = await teachShortcut(key, tokenSource.Token);
            }
            catch (OperationCanceledException) { result.Text = "Shortcut teaching canceled. Check the tablet’s Shortcuts editor."; }
            catch (Exception ex) { result.Text = ex.Message; }
            finally
            {
                operation = null;
                tokenSource.Dispose();
                cancel.Text = "Cancel test";
                RefreshButtons();
                if (closing) Close();
            }
        }

        private async Task TestAsync(bool voice)
        {
            if (selected == null || operation != null || !(voice ? speak.Enabled : send.Enabled)) return;
            VoiceCommand command = selected.Command;
            var tokenSource = new CancellationTokenSource();
            operation = tokenSource;
            pendingFeedback = null;
            var record = new CommandHistoryEntry(command.Id, selected.Action == null ? selected.Title : selected.Action.Title, command.Phrase, voice);
            record.ContextLabel = String.Join(" · ", new[] { mode.ToString(), tabletLabel, voice ? microphoneName : null, command.Shortcut }.Where(s => !String.IsNullOrWhiteSpace(s)));
            record.ContextKey = record.ContextLabel + "|" + command.Phrase;
            last = record;
            history.Add(record);
            bool dispatchStarted = false;
            bool acknowledged = false;
            bool song = command.Id == "next" || command.Id == "previous";
            string oldStatus = status.Text;
            string failure = "Soundcheck failed · Nothing sent";
            RefreshButtons();
            try
            {
                string startingChart = null;
                if (song && prepareSong != null)
                {
                    record.Result = "Reading starting chart";
                    status.Text = voice ? "Preparing · Wait for Listening" : "Preparing · Reading starting chart";
                    result.Text = voice ? "Reading the starting chart. Keep it open and wait for Listening before speaking." :
                        "Reading the starting chart before sending the command.";
                    record.PreparationStarted = Stopwatch.GetTimestamp();
                    try { startingChart = await prepareSong(tokenSource.Token); }
                    finally { record.PreparationEnded = Stopwatch.GetTimestamp(); }
                    tokenSource.Token.ThrowIfCancellationRequested();
                }
                if (voice)
                {
                    failure = "Capture failed · Nothing sent";
                    var available = VoiceCommands.ForMode(mode, preferences.VoiceBindings ?? new List<VoiceBinding>());
                    status.Text = "Opening microphone · Wait for Listening";
                    result.Text = status.Text;
                    record.Result = "Opening microphone";
                    bool capturing = true;
                    Action ready = delegate
                    {
                        if (!capturing || operation != tokenSource || tokenSource.IsCancellationRequested) return;
                        status.Text = "Listening · Say “" + command.Phrase + "” now";
                        result.Text = status.Text;
                        record.Result = "Listening";
                    };
                    MicrophoneResult sample;
                    try
                    {
                        sample = await capture(available, delegate
                        {
                            if (InvokeRequired) BeginInvoke(ready);
                            else ready();
                        }, tokenSource.Token);
                    }
                    finally { capturing = false; }
                    tokenSource.Token.ThrowIfCancellationRequested();
                    record.SpeechStart = sample.SpeechStartTimestamp;
                    record.SpeechEnd = sample.SpeechEndTimestamp;
                    var heard = available.FirstOrDefault(c => c.Id == sample.Command);
                    record.Heard = heard == null ? null : heard.Phrase;
                    if (heard == null || heard.Id != command.Id)
                    {
                        record.Result = heard == null ? "No command recognized · Nothing sent" : "Different phrase recognized · Nothing sent";
                        if (heard == null && Double.IsNegativeInfinity(sample.PeakDbFs)) record.Note = "The selected microphone returned silence.";
                        return;
                    }
                }
                tokenSource.Token.ThrowIfCancellationRequested();
                dispatchStarted = true;
                record.Result = "Checking tablet and sending";
                status.Text = "Sending command";
                result.Text = voice ? "Heard “" + record.Heard + "”. Checking the tablet and sending…" : "Checking the tablet and sending…";
                record.Delivery = await dispatch(command, startingChart, delegate
                {
                    acknowledged = true;
                    record.Result = "Sent";
                    bool observing = song && (prepareSong == null || startingChart != null);
                    status.Text = observing ? "Sent · Checking chart change" : "Sent";
                    result.Text = observing ? "Android accepted the command. Reading the chart to confirm the change…" : "Android accepted the command.";
                    if (observing) cancel.Text = "Stop observation";
                }, tokenSource.Token);
                if (record.Delivery == null) throw new InvalidOperationException("The tablet returned no receipt.");
                record.Result = record.Delivery.ChartObserved.HasValue ? "Sent · Chart change observed" : "Sent · Effect unconfirmed";
                record.Note = String.Join(Environment.NewLine, new[] { record.Delivery.Message, record.Delivery.ObservationNote }.Where(s => !String.IsNullOrWhiteSpace(s)));
                if (!closing) pendingFeedback = record;
            }
            catch (OperationCanceledException)
            {
                record.Result = acknowledged ? "Sent · Observation canceled" : dispatchStarted ? "Canceled · Delivery unconfirmed" : "Canceled · Nothing sent";
            }
            catch (Exception ex)
            {
                record.Result = acknowledged ? "Sent · Effect unconfirmed" : dispatchStarted ? "Delivery unconfirmed" : failure;
                record.Note = ex.Message;
            }
            finally
            {
                operation = null;
                tokenSource.Dispose();
                status.Text = oldStatus;
                cancel.Text = "Cancel test";
                result.Text = Details(record);
                RefreshHistory();
                RefreshButtons();
                if (closing) Close();
            }
        }

        private string Details(CommandHistoryEntry entry)
        {
            var lines = new[] { entry.Outcome, entry.Voice ? "Expected “" + entry.Phrase + "”" +
                (entry.Heard == null ? "" : " · Heard “" + entry.Heard + "”") : "Manual command",
                entry.Timings, entry.RepeatedTiming(history), entry.Note, entry.ContextLabel };
            return String.Join(Environment.NewLine, lines.Where(s => !String.IsNullOrWhiteSpace(s)));
        }

        private void Feedback(bool value)
        {
            var entry = pendingFeedback;
            if (entry == null || operation != null) return;
            entry.Worked = value;
            entry.FeedbackAt = Stopwatch.GetTimestamp();
            pendingFeedback = null;
            result.Text = Details(entry);
            RefreshHistory();
            RefreshButtons();
        }

        private void RefreshHistory()
        {
            historyList.BeginUpdate();
            try
            {
                historyList.Items.Clear();
                foreach (var entry in history.AsEnumerable().Reverse())
                    historyList.Items.Add(entry.When.ToString("T") + "   " + entry.Title + "   ·   " + (entry.Voice ? "Voice" : "Manual") + "   ·   " + entry.Outcome);
                if (historyList.Items.Count > 0) historyList.SelectedIndex = 0;
            }
            finally { historyList.EndUpdate(); }
            ShowHistoryDetails();
        }

        private void ShowHistoryDetails()
        {
            int i = historyList.SelectedIndex;
            detail.Text = i >= 0 && i < history.Count ? Details(history[history.Count - i - 1]) : "Completed tests will appear here.";
        }

        internal void CancelPending()
        {
            if (operation == null || operation.IsCancellationRequested) return;
            operation.Cancel();
            cancel.Enabled = false;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);
            if (operation == null || e.Cancel) return;
            e.Cancel = true;
            closing = true;
            CancelPending();
        }
    }
}

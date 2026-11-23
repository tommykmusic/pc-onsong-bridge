using System;
using System.Drawing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace PcOnSongBridge
{
    internal sealed class SongCommandTest : BridgeWindow
    {
        private static readonly Color Background = Color.FromArgb(19, 25, 32);
        private static readonly Color Surface = Color.FromArgb(29, 38, 48);
        private static readonly Color TextColor = Color.FromArgb(236, 243, 249);
        private static readonly Color Secondary = Color.FromArgb(177, 195, 207);
        private static readonly Color Accent = Color.FromArgb(111, 225, 194);

        private sealed class CommandRow
        {
            public string Action;
            public Label Heard;
            public Label Outcome;
            public Button Speak;
            public Button Moved;
            public Button Stayed;
            public string Summary;
            public bool AwaitingConfirmation;
        }

        private readonly CommandRow[] rows;
        private readonly Button cancel;
        private readonly Func<CancellationToken, Task<string>> capture;
        private readonly Func<string, CancellationToken, Task<string>> dispatch;
        private readonly string movementHint;
        private CancellationTokenSource active;
        private CommandRow current;
        private bool dispatchStarted;
        private bool closing;

        public SongCommandTest(Func<CancellationToken, Task<string>> capture,
            Func<string, CancellationToken, Task<string>> dispatch, string microphoneName = null, string tabletLabel = null,
            string movementHint = "Try a middle song in the set.")
        {
            if (capture == null) throw new ArgumentNullException("capture");
            if (dispatch == null) throw new ArgumentNullException("dispatch");
            SuspendLayout();
            this.capture = capture;
            this.dispatch = dispatch;
            this.movementHint = movementHint;
            Text = "Test song commands";
            Font = new Font("Segoe UI", 10f);
            BackColor = Background;
            ForeColor = TextColor;
            AutoScaleDimensions = new SizeF(96f, 96f);
            AutoScaleMode = AutoScaleMode.Dpi;
            StartPosition = FormStartPosition.CenterParent;
            MinimizeBox = false;
            ClientSize = new Size(620, 440);
            MinimumSize = new Size(405, 280);

            var footer = new FlowLayoutPanel
            {
                AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft,
                Padding = new Padding(8), WrapContents = false
            };
            var close = MakeButton("Close");
            close.Click += delegate { Close(); };
            cancel = MakeButton("Cancel pending test");
            cancel.Click += delegate { CancelPending(); };
            footer.Controls.AddRange(new Control[] { close, cancel });
            CancelButton = close;

            var scroll = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(16, 12, 16, 0) };
            var contents = Stack();
            scroll.Controls.Add(contents);
            Controls.Add(scroll);
            Controls.Add(footer);
            contents.Controls.Add(Line("Open a practice set on the tablet and start on a middle song. Click Speak and send, then say the phrase within 2.5 seconds. A match sends one live command.", TextColor));
            if (!String.IsNullOrWhiteSpace(microphoneName)) contents.Controls.Add(Line("Microphone  ·  " + microphoneName, Secondary));
            if (!String.IsNullOrWhiteSpace(tabletLabel)) contents.Controls.Add(Line("Tablet  ·  " + tabletLabel, Secondary));
            rows = new[] { AddCommand(contents, "next", "Next song"), AddCommand(contents, "previous", "Previous song") };
            RefreshButtons();
            ResumeLayout(true);
        }

        public string ResultSummary
        {
            get
            {
                string[] results = rows.Where(row => row.Summary != null)
                    .Select(row => row.Action + " song — " + row.Summary).ToArray();
                return results.Length == 0 ? null : "Song command test  ·  " + String.Join("  |  ", results);
            }
        }

        protected override void OnLoad(EventArgs e)
        {
            Size desktop = Screen.FromControl(Owner ?? this).WorkingArea.Size;
            var available = new Size(Math.Max(1, desktop.Width - 16), Math.Max(1, desktop.Height - 16));
            MinimumSize = new Size(Math.Min(MinimumSize.Width, available.Width), Math.Min(MinimumSize.Height, available.Height));
            Size = new Size(Math.Min(Width, available.Width), Math.Min(Height, available.Height));
            base.OnLoad(e);
        }

        private static TableLayoutPanel Stack()
        {
            var panel = new TableLayoutPanel
            {
                ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
                Dock = DockStyle.Top, Margin = Padding.Empty
            };
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            return panel;
        }

        private static Label Line(string text, Color color)
        {
            return new Label { Text = text, ForeColor = color, AutoSize = true, Dock = DockStyle.Fill, Margin = new Padding(0, 2, 0, 3) };
        }

        private static Button MakeButton(string text)
        {
            return new Button
            {
                Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, FlatStyle = FlatStyle.Flat,
                ForeColor = TextColor, BackColor = Color.FromArgb(42, 54, 67), Padding = new Padding(6, 1, 6, 1),
                Margin = new Padding(0, 2, 7, 2)
            };
        }

        private CommandRow AddCommand(TableLayoutPanel contents, string action, string title)
        {
            var row = new CommandRow
            {
                Action = action, Speak = MakeButton("Speak and send"), Moved = MakeButton("It moved"),
                Stayed = MakeButton("It didn't move"), Heard = Line("Ready to listen.", Secondary), Outcome = Line("", TextColor)
            };
            var card = Stack();
            card.BackColor = Surface;
            card.Padding = new Padding(12, 8, 12, 9);
            card.Margin = new Padding(0, 9, 0, 0);
            var heading = Line(title, TextColor);
            heading.Font = new Font(Font, FontStyle.Bold);
            card.Controls.Add(heading);
            card.Controls.Add(row.Heard);
            card.Controls.Add(row.Outcome);
            row.Outcome.Visible = false;
            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = Padding.Empty
            };
            row.Speak.AccessibleName = "Speak and send " + title.ToLowerInvariant();
            row.Speak.BackColor = Accent;
            row.Speak.ForeColor = Background;
            buttons.Controls.AddRange(new Control[] { row.Speak, row.Moved, row.Stayed });
            card.Controls.Add(buttons);
            contents.Controls.Add(card);

            row.Speak.Click += async delegate { await TestAsync(row); };
            row.Moved.Click += delegate { Confirm(row, true); };
            row.Stayed.Click += delegate { Confirm(row, false); };
            return row;
        }

        private static void Result(CommandRow row, string message, string summary)
        {
            row.Outcome.Text = message;
            row.Outcome.Visible = message.Length > 0;
            row.Summary = summary;
        }

        private async Task TestAsync(CommandRow row)
        {
            if (active != null || closing) return;
            // Once the next attempt starts, an observation cannot confirm an earlier command.
            foreach (var earlier in rows)
            {
                if (earlier.AwaitingConfirmation)
                    Result(earlier, "Sent. Movement unconfirmed.", "sent, movement unconfirmed");
                earlier.AwaitingConfirmation = false;
            }
            var test = new CancellationTokenSource();
            current = row;
            active = test;
            dispatchStarted = false;
            row.Heard.Text = "Listening. Say “" + row.Action + " song” now.";
            Result(row, "", "listening");
            RefreshButtons();
            try
            {
                string heard = await capture(test.Token);
                test.Token.ThrowIfCancellationRequested();
                if (heard != "next" && heard != "previous")
                {
                    row.Heard.Text = "No command recognized.";
                    Result(row, "Nothing sent. Try again.", "no command recognized or sent");
                    return;
                }
                row.Heard.Text = "Heard “" + heard + " song”.";
                if (heard != row.Action)
                {
                    Result(row, "Expected “" + row.Action + " song”. Nothing sent.", "heard " + heard + " song, nothing sent");
                    return;
                }
                dispatchStarted = true;
                Result(row, "Sending to OnSong…", "recognized, delivery unconfirmed");
                string receipt = await dispatch(heard, test.Token);
                if (String.IsNullOrWhiteSpace(receipt))
                    throw new InvalidOperationException("Android returned no delivery confirmation.");
                if (closing)
                {
                    row.Summary = "sent, movement unconfirmed";
                    return;
                }
                row.AwaitingConfirmation = true;
                Result(row, "Sent. Did the tablet move to the " + row.Action + " song?", "sent, movement unconfirmed");
            }
            catch (OperationCanceledException)
            {
                if (!closing) ShowCanceled(row);
            }
            catch (Exception error)
            {
                if (closing) return;
                if (!dispatchStarted) row.Heard.Text = "Could not check the microphone.";
                Result(row, (dispatchStarted ? "Delivery unconfirmed. " : "Nothing sent. ") + error.Message,
                    dispatchStarted ? "recognized, delivery unconfirmed" : "capture failed, nothing sent");
            }
            finally
            {
                active = null;
                current = null;
                test.Dispose();
                if (!closing) RefreshButtons();
                else if (!IsDisposed && !Disposing && IsHandleCreated) BeginInvoke(new Action(Close));
            }
        }

        private void Confirm(CommandRow row, bool moved)
        {
            if (active != null || closing || !row.AwaitingConfirmation) return;
            row.AwaitingConfirmation = false;
            Result(row, moved ? "Sent. You confirmed the song moved."
                : "Sent. You saw no movement. " + movementHint,
                moved ? "sent, user confirmed movement" : "sent, user reported no movement");
            RefreshButtons();
        }

        private void RefreshButtons()
        {
            cancel.Visible = active != null;
            cancel.Enabled = active != null && !active.IsCancellationRequested;
            foreach (var row in rows)
            {
                row.Speak.Enabled = active == null;
                row.Moved.Visible = row.Stayed.Visible = row.AwaitingConfirmation;
                row.Moved.Enabled = row.Stayed.Enabled = active == null && row.AwaitingConfirmation;
            }
        }

        private void ShowCanceled(CommandRow row)
        {
            if (!dispatchStarted) row.Heard.Text = "Listening canceled.";
            Result(row, dispatchStarted ? "Delivery unconfirmed. Check the tablet before retrying." : "Nothing sent.",
                dispatchStarted ? "recognized, delivery unconfirmed" : "canceled, nothing sent");
        }

        public void CancelPending()
        {
            if (active == null || active.IsCancellationRequested) return;
            ShowCanceled(current);
            cancel.Enabled = false;
            active.Cancel();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            base.OnFormClosing(e);
            if (e.Cancel) return;
            closing = true;
            if (active != null)
            {
                // Keep the modal owner disabled until the microphone or ADB command has finished.
                e.Cancel = true;
                CancelPending();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                closing = true;
                CancelPending();
            }
            base.Dispose(disposing);
        }
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using PcOnSongBridge;

internal static class CommandTests
{
    [DllImport("user32.dll")]
    private static extern bool SetProcessDPIAware();

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control control in root.Controls)
        {
            yield return control;
            foreach (Control child in Descendants(control)) yield return child;
        }
    }

    private static void Check(bool ok, string reason)
    {
        if (!ok) throw new Exception(reason);
    }

    private static async Task Exercise(Form owner)
    {
        string heard = "next";
        string failure = null;
        int captures = 0;
        var sent = new List<string>();
        TaskCompletionSource<string> pendingCapture = null;
        TaskCompletionSource<string> pendingSend = null;
        CancellationToken activeToken = CancellationToken.None;
        using (var dialog = new SongCommandTest(token =>
        {
            captures++;
            activeToken = token;
            if (failure == "capture") throw new Exception("Input disconnected.");
            return pendingCapture == null ? Task.FromResult(heard) : pendingCapture.Task;
        }, (command, token) =>
        {
            sent.Add(command);
            activeToken = token;
            if (failure == "send") throw new Exception("Tablet offline.");
            return pendingSend == null ? Task.FromResult("Android accepted the command.") : pendingSend.Task;
        }, "Laptop microphone", "Practice tablet"))
        {
            dialog.ShowInTaskbar = false;
            dialog.Opacity = 0;
            dialog.Show(owner);
            var buttons = Descendants(dialog).OfType<Button>().ToArray();
            var next = buttons.Single(b => b.AccessibleName == "Speak and send next song");
            var previous = buttons.Single(b => b.AccessibleName == "Speak and send previous song");
            var moved = next.Parent.Controls.OfType<Button>().Single(b => b.Text == "It moved");
            var stayed = previous.Parent.Controls.OfType<Button>().Single(b => b.Text == "It didn't move");
            var cancel = buttons.Single(b => b.Text == "Cancel pending test");
            Check(dialog.ResultSummary == null && sent.Count == 0 && captures == 0, "Opening the dialog started a test.");
            Check(!moved.Enabled && !stayed.Enabled && !cancel.Enabled, "Idle buttons are incorrect.");
            Check(!moved.Visible && !stayed.Visible && !cancel.Visible, "Idle observation or cancel button visible.");
            using (Graphics graphics = dialog.CreateGraphics())
            {
                Rectangle work = Screen.FromControl(dialog).WorkingArea;
                Check(dialog.Width <= work.Width && dialog.Height <= work.Height, "Initial dialog exceeds the desktop.");
                if (work.Width >= 680 * graphics.DpiX / 96 && work.Height >= 540 * graphics.DpiY / 96)
                {
                    var view = Descendants(dialog).OfType<Panel>().Single(p => p.GetType() == typeof(Panel));
                    Rectangle previousBounds = view.RectangleToClient(previous.RectangleToScreen(previous.ClientRectangle));
                    Check(!view.VerticalScroll.Visible && view.ClientRectangle.Contains(previousBounds),
                        "Previous song is below the fold at " + graphics.DpiY + " DPI.");
                }
                Console.WriteLine("PASS initial song command layout at " + graphics.DpiY + " DPI.");
            }

            next.PerformClick();
            Check(sent.SequenceEqual(new[] { "next" }) && moved.Enabled, "Matching phrase did not dispatch exactly once.");
            Check(moved.Visible, "Observation buttons stayed hidden after delivery.");
            Check(dialog.ResultSummary.Contains("sent, movement unconfirmed"), "Delivery implied observed movement.");
            moved.PerformClick();
            Check(dialog.ResultSummary.Contains("user confirmed movement") && !moved.Enabled, "Manual confirmation was lost.");
            heard = "previous";
            next.PerformClick();
            Check(sent.Count == 1 && dialog.ResultSummary.Contains("heard previous song, nothing sent"), "Wrong phrase dispatched.");
            previous.PerformClick();
            Check(sent.SequenceEqual(new[] { "next", "previous" }) && stayed.Enabled, "Previous command did not dispatch.");
            stayed.PerformClick();
            Check(dialog.ResultSummary.Contains("user reported no movement"), "Reported lack of movement was lost.");

            heard = null;
            next.PerformClick();
            Check(sent.Count == 2 && dialog.ResultSummary.Contains("no command recognized or sent"), "Silence dispatched.");
            failure = "capture";
            next.PerformClick();
            Check(sent.Count == 2 && dialog.ResultSummary.Contains("capture failed"), "Capture failure dispatched.");
            failure = "send";
            heard = "next";
            next.PerformClick();
            Check(sent.Count == 3 && !moved.Enabled && dialog.ResultSummary.Contains("recognized, delivery unconfirmed"), "Dispatch failure marked as sent.");
            failure = null;

            pendingCapture = new TaskCompletionSource<string>();
            next.PerformClick();
            Check(!previous.Enabled && cancel.Enabled, "Tests can overlap.");
            cancel.PerformClick();
            Check(activeToken.IsCancellationRequested, "Cancel did not reach capture.");
            pendingCapture.SetResult("next");
            await Task.Yield();
            Check(sent.Count == 3 && next.Enabled && dialog.ResultSummary.Contains("canceled, nothing sent"), "Late capture dispatched after cancellation.");
            pendingCapture = null;

            pendingSend = new TaskCompletionSource<string>();
            next.PerformClick();
            dialog.CancelPending();
            Check(activeToken.IsCancellationRequested && !moved.Enabled, "Cancel did not reach pending dispatch.");
            pendingSend.SetCanceled();
            await Task.Yield();
            Check(sent.Count == 4 && dialog.ResultSummary.Contains("delivery unconfirmed") && !moved.Enabled, "Canceled dispatch reported a known result.");
            pendingSend = null;

            next.PerformClick();
            Check(moved.Enabled, "Retry was unavailable.");
            previous.PerformClick(); // Heard next; the earlier tablet observation is now stale.
            Check(!moved.Enabled, "An earlier command can still be confirmed after another test.");
            dialog.Scale(new SizeF(1.5f, 1.5f));
            Check(next.Width >= next.PreferredSize.Width && previous.Width >= previous.PreferredSize.Width,
                "Speak buttons clipped at 150 percent layout scale.");

            pendingCapture = new TaskCompletionSource<string>();
            next.PerformClick();
            int beforeClose = sent.Count;
            dialog.Close();
            Check(activeToken.IsCancellationRequested, "Close did not cancel capture.");
            Check(dialog.Visible && !next.Enabled && !previous.Enabled, "Close returned control while capture still owned the input.");
            pendingCapture.SetResult("next");
            await Task.Yield();
            Check(sent.Count == beforeClose && dialog.ResultSummary.Contains("canceled, nothing sent"), "Closed dialog dispatched a command.");
        }
        CheckModalCloseDuringDispatch(owner, false);
        CheckModalCloseDuringDispatch(owner, true);
        Console.WriteLine("PASS live command workflow, both manual outcomes, mismatch, silence, failures, retry, cancellation, close and scaled controls.");
    }

    private static void CheckModalCloseDuringDispatch(Form owner, bool acknowledged)
    {
        var pending = new TaskCompletionSource<string>();
        bool closingWaited = false;
        bool released = false;
        CancellationToken token = CancellationToken.None;
        using (var dialog = new SongCommandTest(t => Task.FromResult("next"), async (a, t) =>
        {
            token = t;
            try { return await pending.Task; }
            finally { released = true; }
        }))
        {
            dialog.ShowInTaskbar = false;
            dialog.Opacity = 0;
            dialog.Shown += delegate
            {
                var speak = Descendants(dialog).OfType<Button>().Single(b => b.AccessibleName == "Speak and send next song");
                speak.PerformClick();
                dialog.Close();
                closingWaited = token.IsCancellationRequested && dialog.Visible && !speak.Enabled;
                // Return to the message loop before releasing the pending dispatch.
                owner.BeginInvoke(new Action(() =>
                {
                    if (acknowledged) pending.SetResult("Android accepted the command.");
                    else pending.SetCanceled();
                }));
            };
            dialog.ShowDialog(owner);
            Check(closingWaited && released, "Modal returned to main app before ADB cleanup.");
            string expected = acknowledged ? "sent, movement unconfirmed" : "recognized, delivery unconfirmed";
            Check(dialog.ResultSummary.Contains(expected), "Closing lost the actual delivery outcome.");
        }
    }

    private static async Task ExerciseLibrary(Form owner)
    {
        string folder = Path.Combine(Path.GetTempPath(), "OnSongLibraryTests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string file = Path.Combine(folder, "prefs.xml");
        try
        {
            var preferences = Preferences.Load(file);
            var history = new List<CommandHistoryEntry>();
            var sent = new List<string>();
            string heard = "next";
            long second = Stopwatch.Frequency;
            long speechStart = Stopwatch.GetTimestamp() - 3 * second;
            int grammarCount = 0;
            TaskCompletionSource<CommandDelivery> pending = null;
            TaskCompletionSource<string> pendingTeach = null;
            string taught = null;
            using (var dialog = new CommandLibraryForm(preferences, TabletControlMode.SongSwipes, (commands, listening, token) =>
            {
                listening();
                grammarCount = commands.Count();
                return Task.FromResult(new MicrophoneResult(-7, heard, speechStart, speechStart + second));
            }, (command, baseline, accepted, token) =>
            {
                sent.Add(command.Id);
                return pending == null ? Task.FromResult(new CommandDelivery("Android accepted it.", speechStart + second,
                    speechStart + second * 2, speechStart + second * 3, "Chart snapshot changed.")) : pending.Task;
            }, history, "Fixture mic", "Fixture tablet", null, (key, token) =>
            {
                taught = key.Label;
                if (pendingTeach == null) return Task.FromResult("Choose the matching OnSong action on the tablet.");
                token.Register(() => pendingTeach.TrySetCanceled());
                return pendingTeach.Task;
            }))
            {
                dialog.Opacity = 0;
                dialog.Show(owner);
                var all = Descendants(dialog).ToArray();
                Func<string, Button> button = name => all.OfType<Button>().Single(b => b.Text == name);
                Func<string, TextBox> field = name => all.OfType<TextBox>().Single(b => b.AccessibleName == name);
                var commands = all.OfType<ListBox>().Single(b => b.AccessibleName == "Commands");
                var search = field("Search commands");
                var speak = button("Speak and send");
                int total = commands.Items.Count;
                Check(total >= OnSongActions.All.Length && sent.Count == 0 && history.Count == 0, "Opening the library lost commands or sent input.");
                var natives = new[] { "Next song", "Previous song", "Scroll up", "Scroll down" };
                for (int i = 0; i < natives.Length; i++)
                    Check(commands.Items[i].ToString().Contains(natives[i]), "Expected visible built-in " + natives[i]);
                Check(commands.SelectedIndex == 0 && commands.TopIndex == 0, "Expected Next song selected at the top.");
                button("Add phrase").PerformClick();
                Check(field("Voice phrase").Text == "go to next song", "A new native alias reused the reserved phrase.");
                search.Text = "next section";
                Check(commands.Items.Count == 1 && field("Voice phrase").Text == "next section" && !speak.Enabled,
                    "Word order in the catalog title interfered with search or the phrase suggestion.");
                search.Text = "pause backing track";
                Check(commands.Items.Count == 1 && field("Voice phrase").Text == "pause backing track", "Missing backing track phrase.");
                foreach (var action in OnSongActions.All.Where(a => !new[] { "next", "previous", "scroll-up", "scroll-down" }.Contains(a.Id)))
                    VoiceCommands.CreateBinding(action.Id, VoiceCommands.SuggestedPhrase(action), "F8", null);
                search.Text = "next song";
                Check(commands.Items.Count > 0 && commands.Items.Count < total && speak.Enabled, "Native phrase search or testability failed.");
                speak.PerformClick();
                Check(grammarCount >= 4 && sent.SequenceEqual(new[] { "next" }) && history.Single().Delivery.ChartObserved.HasValue,
                    "Spoken command did not use the full grammar and selected action.");
                Check(field("Current test result").Text.Contains("≤") && history[0].Worked == null, "Device observation implied a manual result or omitted the upper bound.");
                button("It worked").PerformClick();
                Check(history[0].Worked == true && history[0].Timings.Contains("reaction time"), "User feedback timing was lost.");
                speak.PerformClick();
                Check(field("Current test result").Text.Contains("2 comparable tests"), "Comparable observations missing median/range.");
                heard = "previous";
                speak.PerformClick();
                Check(sent.Count == 2 && history.Last().Delivery == null && history.Last().Heard != null, "Wrong recognized phrase dispatched.");

                button("New custom action").PerformClick();
                field("Voice phrase").Text = "show rehearsal notes";
                field("Keyboard shortcut").Text = "Ctrl+Shift+K";
                button("Save mapping").PerformClick();
                var saved = Preferences.Load(file).VoiceBindings;
                Check(saved.Count == 1 && saved[0].Phrase == "show rehearsal notes", "Named phrase and shortcut failed to persist.");
                Check(sent.Count == 2 && button("Send once").Enabled, "Saving sent input or failed to enable the manual test.");
                int trialsBeforeTeaching = history.Count;
                var teach = button("Teach shortcut to tablet");
                Check(teach.Visible && teach.Enabled, "Saved mapping cannot teach its shortcut.");
                teach.PerformClick();
                Check(taught == "Ctrl+Shift+K" && field("Current test result").Text.Contains("matching OnSong action"),
                    "Teaching did not send the saved shortcut to the editor.");
                pendingTeach = new TaskCompletionSource<string>();
                teach.PerformClick();
                Check(!teach.Enabled && !button("Send once").Enabled && button("Cancel teaching").Enabled, "Teaching allowed concurrent dispatch.");
                button("Cancel teaching").PerformClick();
                await Task.Yield();
                Check(teach.Enabled && field("Current test result").Text.Contains("canceled") && history.Count == trialsBeforeTeaching && sent.Count == 2,
                    "Teaching created an action/latency trial or failed to cancel.");
                button("Send once").PerformClick();
                Check(sent.Last() == saved[0].Id && !history.Last().Voice, "Custom mapping did not dispatch manually.");
                button("No effect").PerformClick();
                Check(history.Last().Worked == false, "Unsuccessful manual result was lost.");
                field("Voice phrase").Text = "next song";
                button("Save mapping").PerformClick();
                Check(Preferences.Load(file).VoiceBindings.Single().Phrase == "show rehearsal notes" && !button("Send once").Enabled,
                    "Invalid duplicate phrase changed a saved binding or dispatched the previous one.");
                field("Voice phrase").Text = "show rehearsal notes";
                pending = new TaskCompletionSource<CommandDelivery>();
                button("Send once").PerformClick();
                dialog.Close();
                Check(dialog.Visible, "Closing failed to await an in-flight dispatch.");
                pending.SetResult(new CommandDelivery("Sent", Stopwatch.GetTimestamp(), Stopwatch.GetTimestamp(), null, "Observation canceled."));
                await Task.Yield();
                Check(!dialog.Visible && history.Last().Delivery != null && history.Last().Worked == null,
                    "A cancellation after acknowledgment lost the sent receipt.");
            }
            using (var offline = new CommandLibraryForm(Preferences.Load(file), TabletControlMode.SongSwipes, null, null,
                history, null, null, "Connect a tablet to test"))
            {
                offline.Opacity = 0; offline.Show(owner);
                var all = Descendants(offline).ToArray();
                all.OfType<TextBox>().Single(x => x.AccessibleName == "Search commands").Text = "rehearsal notes";
                Check(all.OfType<ListBox>().Single(x => x.AccessibleName == "Commands").Items.Count == 1, "Saved mapping missing on reopen.");
                Check(!all.OfType<Button>().Single(x => x.Text == "Send once").Enabled, "Offline library allowed sending.");
                Check(!all.OfType<Button>().Single(x => x.Text == "Teach shortcut to tablet").Enabled, "Offline library allowed teaching.");
                var tabs = all.OfType<TabControl>().Single();
                tabs.SelectedIndex = 1;
                Check(all.OfType<ListBox>().Single(x => x.AccessibleName == "Test history").Items.Count == history.Count,
                    "Session history was not retained across dialogs.");
                tabs.SelectedIndex = 0;
                all.OfType<Button>().Single(x => x.Text == "Remove mapping").PerformClick();
                Check(Preferences.Load(file).VoiceBindings.Count == 0, "Removing a mapping did not persist.");
                offline.Close();
            }
            Console.WriteLine("PASS searchable action catalog, offline mappings, full voice grammar, selected action, latency history, manual feedback and cancel after acknowledgment.");
        }
        finally { File.Delete(file); Directory.Delete(folder); }
    }

    private static async Task ExercisePreparedSongSoundcheck(Form owner)
    {
        var history = new List<CommandHistoryEntry>();
        var sends = new List<string>();
        var teaches = new List<string>();
        var baseline = new TaskCompletionSource<string>();
        var speech = new TaskCompletionSource<MicrophoneResult>();
        var receipt = new TaskCompletionSource<CommandDelivery>();
        int preparations = 0, captures = 0;
        Action microphoneReady = null;
        using (var dialog = new CommandLibraryForm(new Preferences(), TabletControlMode.FastSongShortcuts, (commands, listening, token) =>
        {
            captures++;
            microphoneReady = listening;
            return speech.Task;
        }, (command, startingChart, acknowledged, token) =>
        {
            sends.Add(command.Id + "@" + startingChart);
            acknowledged();
            return receipt.Task;
        }, history, "Fixture mic", "Fixture tablet", null, (shortcut, token) =>
        {
            teaches.Add(shortcut.Label);
            return Task.FromResult("Choose the action.");
        }, token =>
        {
            preparations++;
            return baseline.Task;
        }))
        {
            dialog.Opacity = 0; dialog.Show(owner);
            var children = Descendants(dialog).ToArray();
            Func<string, Button> button = name => children.OfType<Button>().Single(b => b.Text == name);
            Func<string, TextBox> box = name => children.OfType<TextBox>().Single(b => b.AccessibleName == name);
            var speak = button("Speak and send");
            var manual = button("Send once");
            var readout = box("Current test result");
            var key = box("Keyboard shortcut");
            var search = box("Search commands");
            Check(key.Visible && key.ReadOnly && key.Text == "Ctrl+Shift+N", "Fast native next shortcut was not shown read-only.");
            button("Teach shortcut to tablet").PerformClick();
            Check(teaches.SequenceEqual(new[] { "Ctrl+Shift+N" }) && history.Count == 0, "Teaching a native key created a song trial.");
            speak.PerformClick();
            manual.PerformClick();
            Check(preparations == 1 && captures == 0 && sends.Count == 0 && !manual.Enabled && readout.Text.Contains("wait for Listening"),
                "Soundcheck armed or sent before reading the starting chart.");
            baseline.SetResult("chart-one");
            await Task.Yield();
            Check(captures == 1 && sends.Count == 0 && readout.Text.Contains("Opening microphone"),
                "Soundcheck announced Listening before the recording device opened.");
            var prompted = new TaskCompletionSource<bool>();
            readout.TextChanged += delegate { if (readout.Text.StartsWith("Listening")) prompted.TrySetResult(true); };
            await Task.Run(microphoneReady);
            Check(await Task.WhenAny(prompted.Task, Task.Delay(3000)) == prompted.Task && readout.Text.Contains("next song"),
                "Microphone stage did not signal when to speak.");
            long start = Stopwatch.GetTimestamp();
            speech.SetResult(new MicrophoneResult(-11, "next", start, start));
            await Task.Yield();
            Check(preparations == 1 && sends.SequenceEqual(new[] { "next@chart-one" }) && !speak.Enabled &&
                readout.Text.Contains("accepted") && button("Stop observation").Enabled, "Post-speech send was delayed or observation looked like recording.");
            long accepted = Stopwatch.GetTimestamp();
            receipt.SetResult(new CommandDelivery("Sent", start, accepted, accepted, ""));
            await Task.Yield();
            Check(history.Single().PreparationEnded <= history[0].SpeechStart && history[0].Timings.Contains("before the test"),
                "Pre-recording measurement was missing or overlapped the spoken trial.");

            search.Text = "previous song";
            Check(key.Text == "Ctrl+Shift+P" && key.ReadOnly, "Fast previous shortcut not shown.");
            button("Teach shortcut to tablet").PerformClick();
            Check(teaches.Last() == "Ctrl+Shift+P", "Previous row sent the wrong teaching key.");
            baseline = new TaskCompletionSource<string>();
            manual.PerformClick();
            Check(preparations == 2 && sends.Count == 1, "Manual previous used a stale chart baseline.");
            baseline.SetResult("chart-two");
            await Task.Yield();
            Check(sends.Last() == "previous@chart-two" && captures == 1, "Manual previous lost its own chart identity or opened a mic.");

            search.Text = "scroll up";
            manual.PerformClick();
            Check(preparations == 2 && sends.Last() == "scroll-up@", "A scroll trial ran an unnecessary starting-chart read.");
            search.Text = "next song";
            baseline = new TaskCompletionSource<string>();
            speak.PerformClick();
            baseline.SetException(new InvalidOperationException("Preparation failed"));
            await Task.Yield();
            Check(captures == 1 && sends.Count == 3 && readout.Text.Contains("Preparation failed"), "Failed preparation still armed the mic or sent a key.");
            baseline = new TaskCompletionSource<string>();
            speak.PerformClick();
            dialog.Close();
            Check(dialog.Visible && !speak.Enabled, "Closing did not wait for the preparation task.");
            baseline.SetResult("after-cancellation");
            await Task.Yield();
            Check(!dialog.Visible && captures == 1 && sends.Count == 3 && history.Last().Outcome.Contains("Nothing sent"),
                "A late starting chart armed or sent after closing.");
        }
        Console.WriteLine("PASS native shortcut teaching, per-trial preparation before listening, timing, signed direction dispatch, observation status, failure and close.");
    }

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--desktop-dpi")) SetProcessDPIAware();
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Exception failure = null;
        using (var owner = new Form { Opacity = 0, ShowInTaskbar = false })
        {
            owner.Shown += async delegate
            {
                try { await Exercise(owner); await ExerciseLibrary(owner); await ExercisePreparedSongSoundcheck(owner); }
                catch (Exception error) { failure = error; }
                finally { owner.Close(); }
            };
            Application.Run(owner);
        }
        if (failure == null) return 0;
        Console.Error.WriteLine(failure);
        return 1;
    }
}

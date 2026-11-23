using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Diagnostics;
using System.Speech.AudioFormat;
using System.Speech.Recognition;
using System.Speech.Synthesis;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using PcOnSongBridge;

internal static class VoiceTests
{
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--latency")
        {
            LatencyTests.Run();
            return;
        }
        var all = CatalogAudit.BuildGrammarFixture();
        if (args.Length == 2 && args[0] == "--catalog-audit")
        {
            CatalogAudit.Run(args[1], all, Audio.FindEnglishRecognizer());
            return;
        }
        CheckCommandLibrary();
        try
        {
            Audio.AnalyzeRecording(new byte[0], 16000, 1, null, new CancellationToken(true));
            throw new InvalidOperationException("Canceled voice input reached the recognizer.");
        }
        catch (OperationCanceledException) { }
        Console.WriteLine("PASS Canceled recognition");
        CheckLevel(new byte[16], Double.NegativeInfinity);
        CheckLevel(new byte[] { 0, 0, 0, 64 }, -6.0206);
        CheckLevel(new byte[] { 0, 128, 0, 0 }, 0);
        Console.WriteLine("PASS Digital silence, half scale and negative full scale without a recognizer");
        CheckSpeechEndpoint();

        var inputs = Audio.GetInputs();
        const string shortName = "Microphone Array (AMD Audio Dev";
        const string fullName = "Microphone Array (AMD Audio Device)";
        var named = new AudioDevice(3, shortName, "endpoint", fullName);
        Require(named.Name == shortName && named.Id == 3 && named.ToString() == fullName,
            "Displaying an endpoint name must preserve its WinMM device identity.");
        Require(new AudioDevice(3, shortName).ToString() == shortName,
            "An endpoint without a friendly name lost its WinMM label.");
        var selected = inputs.FirstOrDefault();
        using (var dialog = new MicrophoneTest(selected))
        {
            dialog.ShowInTaskbar = false;
            dialog.Opacity = 0;
            dialog.Show();
            Require(dialog.Text == "Test microphone", "Missing microphone test UI.");
            Require(selected == null ? dialog.SelectedMicrophone == null :
                dialog.SelectedMicrophone.Id == selected.Id && dialog.SelectedMicrophone.Name == selected.Name,
                "Microphone picker lost the requested input.");
            if (selected != null)
            {
                var picker = Descendants(dialog).OfType<ComboBox>().Single();
                Require(picker.GetItemText(picker.SelectedItem) == selected.DisplayName,
                    "The microphone picker must display the endpoint label.");
                Console.WriteLine("PASS microphone picker displays " + picker.Text);
            }
            dialog.Close();
        }
        Console.WriteLine("PASS Microphone dialog opens and closes without starting capture");
        CheckDialogCancellation();

        var installed = SpeechRecognitionEngine.InstalledRecognizers();
        var recognizer = Audio.FindEnglishRecognizer();
        if (recognizer == null)
        {
            Require(!installed.Any(r => r.Culture.TwoLetterISOLanguageName == "en"),
                "An installed English recognizer was overlooked.");
            Console.WriteLine("SKIP Recorded speech tests: this PC has no English desktop recognizer.");
            return;
        }
        Require(recognizer.Culture.TwoLetterISOLanguageName == "en", "An English grammar requires an English recognizer.");
        if (installed.Any(r => r.Culture.Name == "en-US"))
            Require(recognizer.Culture.Name == "en-US", "US English should be preferred when present.");
        Console.WriteLine("PASS Recognizer selection (" + recognizer.Culture.Name + ")");
        // Desktop controls above may have installed a WinForms synchronization context.
        Task.Run(() => CheckHandsFreeLifecycle(recognizer)).GetAwaiter().GetResult();
        var tone = new byte[16000 * 2];
        for (int i = 0; i < tone.Length / 2; i++)
        {
            short value = (short)(16000 * Math.Sin(2 * Math.PI * 440 * i / 16000));
            tone[i * 2] = (byte)value;
            tone[i * 2 + 1] = (byte)(value >> 8);
        }
        var noMatch = Audio.AnalyzeRecording(tone, 16000, 1, recognizer, CancellationToken.None);
        Require(noMatch.Command == null && noMatch.PeakDbFs > -7, "Tone must show input without accepting a phrase.");
        Console.WriteLine("PASS Non-speech signal is distinguished from silence");

        using (var voice = new SpeechSynthesizer())
        {
            var englishVoice = voice.GetInstalledVoices()
                .Where(item => item.Enabled && item.VoiceInfo.Culture.TwoLetterISOLanguageName == "en")
                .OrderByDescending(item => item.VoiceInfo.Culture.Name == recognizer.Culture.Name)
                .FirstOrDefault();
            if (englishVoice == null)
            {
                Console.WriteLine("SKIP Recorded speech tests: this PC has no English desktop synthesis voice.");
                return;
            }
            voice.SelectVoice(englishVoice.VoiceInfo.Name);
            LatencyTests.Smoke(voice, recognizer);
            CheckPhrase(voice, recognizer, "next song", "next", 0);
            CheckPhrase(voice, recognizer, "previous song", "previous", 1);
            CheckPhrase(voice, recognizer, null, null, 0);
            CheckPhrase(voice, recognizer, "scroll down", "scroll-down", 0,
                VoiceCommands.ForMode(TabletControlMode.Automatic, null));
            var binding = VoiceCommands.CreateBinding("custom", "go to amber sky", "Ctrl+Shift+K", null);
            var commands = VoiceCommands.ForMode(TabletControlMode.SongSwipes, new[] { binding });
            CheckPhrase(voice, recognizer, binding.Phrase, binding.Id, 1, commands);
        }
    }

    private static async Task WithinTimeout(Task task, string stage)
    {
        Require(await Task.WhenAny(task, Task.Delay(10000)) == task, "Timed out " + stage);
        await task;
    }

    private static async Task CheckHandsFreeLifecycle(RecognizerInfo recognizer)
    {
        var commands = VoiceCommands.SongCommands.ToArray();
        var silence = new byte[16000 * Audio.BlockMilliseconds / 1000 * 2];
        int dispatched = 0;
        Func<MicrophoneResult, CancellationToken, Task> dispatch = (heard, token) =>
        {
            Interlocked.Increment(ref dispatched);
            return Task.FromResult(true);
        };
        using (var stop = new CancellationTokenSource())
        using (var release = new ManualResetEventSlim())
        {
            var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var quiet = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var releasing = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            bool closed = false;
            Task session = Audio.RunHandsFreeInputAsync(recognizer, commands, (accept, opened, cancel) =>
            {
                opened(Stopwatch.GetTimestamp());
                accept(silence, 16000, 1);
                quiet.SetResult(true);
                Require(cancel.WaitHandle.WaitOne(10000), "Stop did not reach the silent input.");
                releasing.SetResult(true);
                Require(release.Wait(10000), "Source cleanup was never released.");
                closed = true;
            }, () => ready.SetResult(true), dispatch, ignored => { }, stop.Token);
            Func<Task> assertOtherWindowExcluded = async () =>
            {
                try
                {
                    await Audio.RunHandsFreeInputAsync(recognizer, commands,
                        (accept, opened, token) => { throw new Exception("A second window opened the microphone."); },
                        () => { }, dispatch, ignored => { }, CancellationToken.None);
                    throw new Exception("Overlapping listener started.");
                }
                catch (InvalidOperationException error) { Require(error.Message.Contains("other voice control"), error.Message); }
            };
            try
            {
                await WithinTimeout(Task.WhenAll(ready.Task, quiet.Task), "opening a silent talkback input");
                await assertOtherWindowExcluded();
                stop.Cancel();
                await WithinTimeout(releasing.Task, "canceling a silent talkback input");
                Require(!session.IsCompleted, "Stop reported completion while the source still owned the input.");
                await assertOtherWindowExcluded();
            }
            finally { stop.Cancel(); release.Set(); }
            try { await WithinTimeout(session, "releasing the microphone"); throw new Exception("Expected cancellation."); }
            catch (OperationCanceledException) { }
            Require(closed, "Listener ended before releasing its silent input.");
        }

        bool failedReady = false;
        try
        {
            await WithinTimeout(Audio.RunHandsFreeInputAsync(recognizer, commands,
                (accept, started, cancel) => { throw new IOException("Simulated disconnected input."); },
                () => failedReady = true, dispatch, ignored => { }, CancellationToken.None), "handling a lost input");
            throw new Exception("The capture failure was lost.");
        }
        catch (IOException error) { Require(error.Message == "Simulated disconnected input.", error.Message); }
        Require(!failedReady, "A failed input was displayed as ready.");
        using (var retry = new CancellationTokenSource())
        {
            bool retryOpened = false;
            Task fresh = Audio.RunHandsFreeInputAsync(recognizer, commands, (accept, started, token) =>
            {
                started(Stopwatch.GetTimestamp());
                Require(token.WaitHandle.WaitOne(10000), "The reconnected input stayed open after Stop.");
            }, () => { retryOpened = true; retry.Cancel(); }, dispatch, ignored => { }, retry.Token);
            try { await WithinTimeout(fresh, "stopping the reconnected input"); throw new Exception("Expected cancellation."); }
            catch (OperationCanceledException) { }
            Require(retryOpened, "A previous input failure blocked the next Start.");
        }

        using (var stop = new CancellationTokenSource())
        {
            var late = new TaskCompletionSource<MicrophoneResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            Task loop = HandsFreeListening.RunAsync(cancel => late.Task, dispatch, ignored => { }, stop.Token);
            stop.Cancel();
            late.SetResult(new MicrophoneResult(-16, "next"));
            try { await WithinTimeout(loop, "discarding a late result"); throw new Exception("Expected cancellation."); }
            catch (OperationCanceledException) { }
        }
        Require(dispatched == 0, "Silence or a recognition result delivered after Stop sent a tablet command.");
        Console.WriteLine("PASS exclusive hands-free microphone ownership, silent stop through device cleanup, restart after input loss and canceled late recognition");
    }

    private static void CheckSpeechEndpoint()
    {
        using (var stream = new Audio.LiveSpeechStream(1000))
        {
            var block = new byte[16000 * Audio.BlockMilliseconds / 1000 * 2];
            Action<int> append = peak =>
            {
                block[0] = (byte)peak;
                block[1] = (byte)(peak >> 8);
                stream.Append(block, 16000, 1);
            };
            // Five startup blocks, including a click and an empty buffer, followed by loud and soft syllables.
            foreach (int peak in new[] { 2100, 0, 220, 230, 225, 5000, 750 }) append(peak);
            Require(stream.QuietMilliseconds == 0, "A startup transient masked the quieter end of a phrase.");
            for (int i = 0; i < 8; i++) append(225);
            Require(stream.QuietMilliseconds == 160, "Steady room noise delayed the end of the phrase.");
        }
        Console.WriteLine("PASS Quiet syllable and 160 ms of trailing room noise after a startup click");
    }

    private static void CheckCommandLibrary()
    {
        Require(OnSongActions.All.Select(a => a.Id).Distinct().Count() == OnSongActions.All.Length, "Duplicate action IDs.");
        Require(OnSongActions.All.Count(a => a.Category == "Keyboard shortcuts") > 1, "Keyboard reference missing.");
        Require(OnSongActions.All.Any(a => a.Id == "navigation-song-switch") &&
            OnSongActions.All.Any(a => a.Id == "navigation-section-switch"), "Missing parameterized song or section.");
        var bind = VoiceCommands.CreateBinding("navigation-section-switch", "  play   the chorus ", "Shift+Ctrl+k", null);
        Require(bind.Phrase == "play the chorus" && bind.Shortcut == "Ctrl+Shift+K", "Binding normalization failed.");
        foreach (var invalid in new[] { "NEXT SONG", "scroll up", "play the chorus", "go --" })
        {
            try { VoiceCommands.CreateBinding("custom", invalid, "F8", new[] { bind }); throw new InvalidOperationException("Invalid phrase accepted: " + invalid); }
            catch (ArgumentException) { }
        }
        string settingsFile = Path.Combine(Path.GetTempPath(), "OnSong-mapping-" + Guid.NewGuid().ToString("N") + ".xml");
        try
        {
            File.WriteAllText(settingsFile, "<Preferences><BufferMs>110</BufferMs></Preferences>");
            var preferences = Preferences.Load(settingsFile);
            Require(preferences.VoiceBindings.Count == 0 && preferences.BufferMs == 110, "Legacy preferences failed to load.");
            preferences.VoiceBindings.Add(bind);
            preferences.Save();
            var restored = Preferences.Load(settingsFile).VoiceBindings.Single();
            Require(restored.Id == bind.Id && restored.ActionId == bind.ActionId && restored.Phrase == bind.Phrase &&
                restored.Shortcut == bind.Shortcut, "Custom binding changed across restart.");
        }
        finally { File.Delete(settingsFile); }
        Require(VoiceShortcut.Parse(bind.Shortcut).AndroidKeys.SequenceEqual(new[] { "KEYCODE_CTRL_LEFT", "KEYCODE_SHIFT_LEFT", "KEYCODE_K" }),
            "Incorrect chord token order.");
        foreach (var text in new[] { "Alt+Tab", "Ctrl+Ctrl+F8", "KEYCODE_VOLUME_UP", "Enter;foo", "", "Home+Page Up" })
        {
            try { VoiceShortcut.Parse(text); throw new InvalidOperationException("Invalid shortcut accepted: " + text); }
            catch (ArgumentException) { }
        }
        foreach (var mode in new[] { TabletControlMode.SongSwipes, TabletControlMode.Automatic, TabletControlMode.ArrowShortcuts })
        {
            var configured = VoiceCommands.ForMode(mode, new[] { bind });
            Require(configured.Count == 5 && configured.Single(c => c.Id == bind.Id).Phrase == bind.Phrase,
                "Executable commands include unmapped or missing actions.");
            Require(!VoiceCommands.ForMode(mode, new[] { bind, bind }).Any(c => c.Id == bind.Id),
                "Ambiguous saved bindings must stay out of the live grammar.");
        }
        Require(Audio.CaptureMilliseconds(VoiceCommands.SongCommands) == 2500 &&
            Audio.CaptureMilliseconds(VoiceCommands.ForMode(TabletControlMode.Automatic, new[] { bind })) > 2500,
            "The recording window should accommodate longer mapped phrases.");
        Console.WriteLine("PASS " + OnSongActions.All.Length + " source-backed catalog rows, explicit phrase mappings and validated shortcuts");
    }

    private static void CheckLevel(byte[] samples, double expected)
    {
        var measured = Audio.AnalyzeRecording(samples, 48000, 2, null, CancellationToken.None);
        Require(measured.Command == null && (measured.PeakDbFs == expected || Math.Abs(measured.PeakDbFs - expected) < 0.001),
            "Unexpected peak for synthesized PCM.");
    }

    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (Control nested in Descendants(child)) yield return nested;
        }
    }

    private static void CheckDialogCancellation()
    {
        var pending = new TaskCompletionSource<MicrophoneResult>();
        CancellationToken requested = CancellationToken.None;
        int calls = 0;
        using (var dialog = new MicrophoneTest(null, (device, recognizer, token) =>
        {
            calls++;
            requested = token;
            return pending.Task;
        }))
        {
            dialog.ShowInTaskbar = false;
            dialog.Opacity = 0;
            dialog.Show();
            var controls = Descendants(dialog).ToArray();
            var devices = controls.OfType<ComboBox>().Single();
            devices.SelectedIndex = devices.Items.Add(new AudioDevice(55, "Synthetic input"));
            var test = controls.OfType<Button>().Single(b => b.Text == "Test input");
            Require(calls == 0 && test.Enabled, "Merely selecting an input should leave capture idle.");
            test.PerformClick();
            Require(calls == 1 && !test.Enabled && !devices.Enabled, "Repeated or ambiguous tests must be blocked.");
            dialog.Close();
            Require(requested.IsCancellationRequested && !dialog.IsDisposed, "Closing must cancel and wait for capture cleanup.");
            pending.SetCanceled();
            Application.DoEvents();
            Require(dialog.IsDisposed, "The dialog did not close after capture released its microphone.");
        }
        Console.WriteLine("PASS Closing an active microphone test cancels and awaits cleanup");
    }

    private static void CheckPhrase(SpeechSynthesizer voice, RecognizerInfo recognizer, string phrase, string expected, int channel,
        IEnumerable<VoiceCommand> commands = null)
    {
        using (var pcm = new MemoryStream())
        {
            voice.SetOutputToAudioStream(pcm, new SpeechAudioFormatInfo(48000, AudioBitsPerSample.Sixteen, AudioChannel.Mono));
            var prompt = new PromptBuilder(voice.Voice.Culture);
            if (phrase != null) prompt.AppendText(phrase);
            prompt.AppendBreak(TimeSpan.FromMilliseconds(600));
            voice.Speak(prompt);
            voice.SetOutputToNull();
            // Simulate one isolated microphone on a 48 kHz stereo mixer return.
            byte[] mono = pcm.ToArray();
            const int leadingFrames = 48000 / 2;
            byte[] stereo = new byte[(leadingFrames * 4) + mono.Length * 2];
            for (int offset = 0; offset < mono.Length; offset += 2)
                Buffer.BlockCopy(mono, offset, stereo, leadingFrames * 4 + offset * 2 + channel * 2, 2);
            long captureStart = Stopwatch.GetTimestamp();
            var result = Audio.AnalyzeRecording(stereo, 48000, 2, recognizer, commands ?? VoiceCommands.SongCommands,
                CancellationToken.None, captureStart);
            Require(result.Command == expected, "Unexpected recognition for " + (phrase ?? "silence") + ": " + (result.Command ?? "no command"));
            if (phrase != null)
            {
                Require(!Double.IsInfinity(result.PeakDbFs), "Speech did not register an input peak.");
                double start = (result.SpeechStartTimestamp.Value - captureStart) / (double)Stopwatch.Frequency;
                double end = (result.SpeechEndTimestamp.Value - captureStart) / (double)Stopwatch.Frequency;
                Require(start > 0.1 && end > start && end <= stereo.Length / (48000.0 * 4) + 0.002,
                    "Acoustic offsets must identify speech inside the captured audio.");
                Console.WriteLine("  Recognizer acoustic start/end at " + start.ToString("0.000") + " / " + end.ToString("0.000") + " s");
            }
            Console.WriteLine("PASS " + (phrase ?? "Silence produces no command"));
        }
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Speech.AudioFormat;
using System.Speech.Recognition;
using System.Speech.Synthesis;
using System.Threading.Tasks;
using System.Windows.Forms;
using PcOnSongBridge;

internal static class VoiceNavigationIntegration
{
    private static void Check(bool condition, string reason)
    {
        if (!condition) throw new InvalidOperationException(reason);
    }

    private static IEnumerable<Control> Descendants(Control parent)
    {
        foreach (Control child in parent.Controls)
        {
            yield return child;
            foreach (Control nested in Descendants(child)) yield return nested;
        }
    }

    private static byte[] Synthesize(SpeechSynthesizer voice, string phrase)
    {
        using (var pcm = new MemoryStream())
        {
            voice.SetOutputToAudioStream(pcm, new SpeechAudioFormatInfo(16000, AudioBitsPerSample.Sixteen, AudioChannel.Mono));
            var prompt = new PromptBuilder(voice.Voice.Culture);
            prompt.AppendText(phrase);
            prompt.AppendBreak(TimeSpan.FromMilliseconds(600));
            voice.Speak(prompt);
            voice.SetOutputToNull();
            return pcm.ToArray();
        }
    }

    private static byte[] NonSpeechSignal()
    {
        byte[] pcm = new byte[32000];
        for (int frame = 0; frame < pcm.Length / 2; frame++)
        {
            short sample = (short)(8192 * Math.Sin(frame * 2 * Math.PI * 440 / 16000));
            pcm[2 * frame] = unchecked((byte)sample);
            pcm[2 * frame + 1] = (byte)(sample >> 8);
        }
        return pcm;
    }

    private static async Task TestButton(SongCommandTest form, string command, string outcome)
    {
        Button button = Descendants(form).OfType<Button>().Single(item => item.AccessibleName == "Speak and send " + command + " song");
        button.PerformClick();
        var clock = Stopwatch.StartNew();
        while (!button.Enabled && clock.ElapsedMilliseconds < 12000) await Task.Delay(25);
        Check(button.Enabled && (form.ResultSummary ?? "").Contains(command + " song — " + outcome),
            "Unexpected song command result. " + form.ResultSummary);
    }

    [STAThread]
    private static int Main()
    {
        var recognizer = Audio.FindEnglishRecognizer();
        if (recognizer == null)
        {
            Console.WriteLine("SKIP speech-to-navigation integration: no English desktop recognizer installed.");
            return 0;
        }
        using (var voice = new SpeechSynthesizer())
        {
            var installed = voice.GetInstalledVoices().Where(v => v.Enabled && v.VoiceInfo.Culture.TwoLetterISOLanguageName == "en")
                .OrderByDescending(v => v.VoiceInfo.Culture.Equals(recognizer.Culture)).FirstOrDefault();
            if (installed == null)
            {
                Console.WriteLine("SKIP speech-to-navigation integration: no English desktop synthesis voice installed.");
                return 0;
            }
            voice.SelectVoice(installed.VoiceInfo.Name);
            byte[] next = Synthesize(voice, "next song");
            byte[] previous = Synthesize(voice, "previous song");
            byte[] samples = next;

            string stage = AppDomain.CurrentDomain.BaseDirectory;
            string tablet = Path.Combine(stage, "spoken-commands-tablet");
            Directory.CreateDirectory(tablet);
            foreach (string tool in new[] { "adb.exe", "scrcpy.exe" })
                File.Copy(Path.Combine(stage, tool), Path.Combine(tablet, tool));
            string index = Path.Combine(tablet, "song-index");
            string events = Path.Combine(tablet, "dispatched-navigation");
            File.WriteAllText(index, "53");
            File.WriteAllText(Path.Combine(tablet, "track-navigation"), "");
            File.WriteAllText(Path.Combine(tablet, "no-link"), "");
            string background = Path.Combine(tablet, "other-foreground");
            bool useSwipes = false;

            Application.EnableVisualStyles();
            using (var bridge = new AndroidBridge(Path.Combine(tablet, "scrcpy.exe")))
            using (var dialog = new SongCommandTest(
                token => Task.Run(() => Audio.AnalyzeRecording(samples, 16000, 1, recognizer, token).Command, token),
                (command, token) => useSwipes ? bridge.SendSwipeActionAsync("READY123", command, token)
                    : bridge.SendKeyboardActionAsync("READY123", command, token), "Synthetic PCM", "Isolated tablet"))
            {
                Exception failure = null;
                dialog.Opacity = 0;
                dialog.ShowInTaskbar = false;
                dialog.Shown += async delegate
                {
                    try
                    {
                        Check(!await bridge.HasSongLinksAsync("READY123"), "Fixture must exercise foreground mode.");
                        foreach (bool swipe in new[] { false, true })
                        {
                            useSwipes = swipe;
                            if (swipe) await bridge.CheckSwipeAsync("READY123");
                            else await bridge.CheckKeyboardAsync("READY123");
                            samples = next;
                            await TestButton(dialog, "next", "sent, movement unconfirmed");
                            Check(File.ReadAllText(index) == "54", "Next failed to advance the simulated tablet.");
                            samples = previous;
                            await TestButton(dialog, "previous", "sent, movement unconfirmed");
                            Check(File.ReadAllText(index) == "53", "Previous failed to restore the starting song.");
                            samples = NonSpeechSignal();
                            await TestButton(dialog, "next", "no command recognized or sent");
                            samples = next;
                            File.WriteAllText(background, "");
                            await TestButton(dialog, "next", "recognized, delivery unconfirmed");
                            File.Delete(background);
                            if (swipe)
                            {
                                string missing = Path.Combine(tablet, "no-viewer");
                                File.WriteAllText(missing, "");
                                await TestButton(dialog, "next", "recognized, delivery unconfirmed");
                                File.Delete(missing);
                            }
                            Check(File.ReadAllText(index) == "53", "Device moved on an unrecognized command or unverified screen.");
                        }
                        Check(File.ReadAllLines(events).SequenceEqual(new[] {
                            "-s READY123 shell input keyevent KEYCODE_DPAD_RIGHT",
                            "-s READY123 shell input keyevent KEYCODE_DPAD_LEFT",
                            "-s READY123 shell input swipe 1152 1200 448 1200 300",
                            "-s READY123 shell input swipe 448 1200 1152 1200 300"
                        }), "Expected exactly one pair of arrow events and one pair of 300 ms center swipes.");
                    }
                    catch (Exception ex) { failure = ex; }
                    finally { dialog.Close(); }
                };
                Application.Run(dialog);
                if (failure != null) { Console.Error.WriteLine(failure); return 1; }
            }
            Console.WriteLine("PASS " + recognizer.Culture.Name + " synthesized speech through the song-command dialog, Windows recognition and Android dispatch.");
            Console.WriteLine("PASS spoken arrows and spoken swipes each moved simulated chart index 53 -> 54 -> 53.");
            Console.WriteLine("PASS unrecognized audio, background OnSong and a missing swipe viewer sent no additional navigation.");
        }
        return 0;
    }
}

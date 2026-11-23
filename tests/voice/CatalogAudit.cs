using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Speech.AudioFormat;
using System.Speech.Recognition;
using System.Speech.Synthesis;
using System.Threading;
using System.Threading.Tasks;
using PcOnSongBridge;

internal static class CatalogAudit
{
    private static readonly string[] Modifiers = { "", "Ctrl+", "Shift+", "Alt+", "Ctrl+Shift+", "Ctrl+Alt+", "Alt+Shift+" };
    private static readonly string[] Keys = "ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789".Select(c => c.ToString())
        .Concat(Enumerable.Range(1, 12).Where(i => i != 4).Select(i => "F" + i)).ToArray();

    private static void Check(bool ok, string message) { if (!ok) throw new InvalidOperationException(message); }

    internal static List<VoiceCommand> BuildGrammarFixture()
    {
        var bindings = new List<VoiceBinding>();
        var native = VoiceCommands.ForMode(TabletControlMode.SongSwipes, bindings).ToDictionary(c => c.Id);
        var phrases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var action in OnSongActions.All)
        {
            string phrase = VoiceCommands.SuggestedPhrase(action);
            Check(phrases.Add(phrase), "Suggested phrase collision: " + phrase + " (" + action.Id + ")");
            if (native.ContainsKey(action.Id))
            {
                Check(native[action.Id].Phrase == phrase, "Built-in phrase differs from its suggestion.");
                continue;
            }
            int i = bindings.Count;
            string shortcut = Modifiers[i / Keys.Length] + Keys[i % Keys.Length];
            bindings.Add(VoiceCommands.CreateBinding(action.Id, phrase, shortcut, bindings));
        }
        List<VoiceCommand> all = VoiceCommands.ForMode(TabletControlMode.SongSwipes, bindings);
        Check(all.Count == OnSongActions.All.Length, "A catalog mapping is missing.");
        foreach (var mode in Enum.GetValues(typeof(TabletControlMode)).Cast<TabletControlMode>())
            Check(VoiceCommands.ForMode(mode, bindings).Select(c => c.Id).SequenceEqual(all.Select(c => c.Id)), "Missing commands in " + mode);
        var wireKeys = bindings.Select(b => String.Join("+", VoiceShortcut.Parse(b.Shortcut).AndroidKeys)).ToArray();
        Check(wireKeys.Distinct().Count() == bindings.Count, "Two distinct fixture shortcuts share a wire encoding.");
        Console.WriteLine("PASS all {0} catalog suggestions are unique and valid: {1} built-in, {2} distinct fixture shortcuts in {3} modes. Covered {4} categories.",
            all.Count, native.Count, bindings.Count, Enum.GetValues(typeof(TabletControlMode)).Length, OnSongActions.All.Select(a => a.Category).Distinct().Count());
        return all;
    }

    internal static void Run(string fakeToolPath, List<VoiceCommand> all, RecognizerInfo recognizer)
    {
        if (recognizer == null) throw new InvalidOperationException("The speech audit requires an English Windows recognizer.");
        CheckDelivery(fakeToolPath, all);
        using (var engine = new SpeechRecognitionEngine(recognizer))
        using (var voice = new SpeechSynthesizer())
        {
            var speakers = voice.GetInstalledVoices().Where(v => v.Enabled && v.VoiceInfo.Culture.Name == recognizer.Culture.Name)
                .OrderBy(v => v.VoiceInfo.Name, StringComparer.Ordinal).Take(2).ToArray();
            if (speakers.Length == 0) throw new InvalidOperationException("No synthesis voice matches " + recognizer.Culture.Name);
            engine.LoadGrammar(Audio.BuildCommandGrammar(recognizer, all.ToArray()));
            engine.InitialSilenceTimeout = TimeSpan.FromMilliseconds(Audio.CaptureMilliseconds(all));
            foreach (var command in all)
            {
                var emulated = engine.EmulateRecognize(command.Phrase);
                Check(emulated != null && (string)emulated.Semantics.Value == command.Id, "Invalid grammar semantics for " + command.Phrase);
            }
            Console.WriteLine("PASS {0}/{0} exact text inputs resolved to the expected command ID in one combined SAPI grammar.", all.Count);
            bool allSafe = true;
            foreach (var speaker in speakers)
            {
                voice.SelectVoice(speaker.VoiceInfo.Name);
                if (!CheckSpeech(engine, voice, all)) allSafe = false;
            }
            Check(allSafe, "The catalog sweep accepted a different command or recognized none of this voice's samples.");
        }
    }

    private static bool CheckSpeech(SpeechRecognitionEngine engine, SpeechSynthesizer voice, List<VoiceCommand> all)
    {
        var format = new SpeechAudioFormatInfo(16000, AudioBitsPerSample.Sixteen, AudioChannel.Mono);
        var byId = all.ToDictionary(c => c.Id);
        int correct = 0, low = 0, missing = 0, wrong = 0;
        foreach (var command in all)
        {
            using (var pcm = new MemoryStream())
            {
                voice.SetOutputToAudioStream(pcm, format);
                var prompt = new PromptBuilder(voice.Voice.Culture);
                prompt.AppendBreak(TimeSpan.FromMilliseconds(250));
                prompt.AppendText(command.Phrase);
                prompt.AppendBreak(TimeSpan.FromMilliseconds(550));
                voice.Speak(prompt);
                voice.SetOutputToNull();
                // Use the recognizer's production 16 kHz format and the same maximum listen window.
                long limit = Audio.CaptureMilliseconds(all) * 32L;
                if (pcm.Length > limit) pcm.SetLength(limit);
                pcm.Position = 0;
                engine.SetInputToAudioStream(pcm, format);
                RecognitionResult heard = engine.Recognize();
                if (heard == null) { missing++; Console.WriteLine("NO RESULT: " + command.Phrase); }
                else if (heard.Confidence < Audio.CommandConfidence)
                {
                    low++;
                    Console.WriteLine("LOW {0:0.000}: {1} -> {2}", heard.Confidence, command.Phrase, heard.Text);
                }
                else if ((string)heard.Semantics.Value != command.Id)
                {
                    wrong++;
                    Console.WriteLine("WRONG {0:0.000}: {1} -> {2}", heard.Confidence, command.Phrase, byId[(string)heard.Semantics.Value].Phrase);
                }
                else correct++;
                engine.SetInputToNull();
            }
        }
        Console.WriteLine("Acoustic audit with {0}, {1}, confidence >= {2:0.00}: {3} trials; {4} correct, {5} low-confidence rejections, {6} no result, {7} wrong accepted.",
            voice.Voice.Name, voice.Voice.Culture.Name, Audio.CommandConfidence, all.Count, correct, low, missing, wrong);
        Check(correct + low + missing + wrong == all.Count, "Incomplete recognition trial count.");
        return wrong == 0 && correct > 0;
    }

    private static void CheckDelivery(string fakeToolPath, List<VoiceCommand> all)
    {
        string receipts = Path.Combine(Path.GetDirectoryName(fakeToolPath), "receipts");
        Directory.CreateDirectory(receipts);
        var mapped = all.Where(c => c.Shortcut != null).ToArray();
        using (var bridge = new AndroidBridge(fakeToolPath))
        {
            Parallel.ForEach(mapped, new ParallelOptions { MaxDegreeOfParallelism = 3 }, command =>
            {
                CommandDispatch.SendAsync(bridge, "CATALOG_FIXTURE", TabletControlMode.SongSwipes, command, false,
                    CancellationToken.None).GetAwaiter().GetResult();
                string[] keys = VoiceShortcut.Parse(command.Shortcut).AndroidKeys;
                string path = Path.Combine(receipts, String.Join("+", keys) + ".txt");
                string expected = (keys.Length == 1 ? "input keyevent " : "input keycombination ") + String.Join(" ", keys);
                Check(File.ReadAllText(path) == expected, "Incorrect fixture input for " + command.Phrase);
            });
        }
        Check(Directory.GetFiles(receipts).Length == mapped.Length, "Unexpected number of simulated dispatches.");
        Console.WriteLine("PASS all {0} distinct mapped shortcuts through production dispatch to local ADB fixtures.", mapped.Length);
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Speech.AudioFormat;
using System.Speech.Recognition;
using System.Speech.Synthesis;
using System.Threading;
using System.Threading.Tasks;
using PcOnSongBridge;

internal static class LatencyTests
{
    private const int Rate = 16000;
    private static readonly int BytesPerBlock = Rate * Audio.BlockMilliseconds / 1000 * 2;

    internal static void Run()
    {
        var recognizer = Audio.FindEnglishRecognizer();
        if (recognizer == null) throw new InvalidOperationException("An English desktop recognizer is required for latency measurements.");
        var ids = VoiceCommands.SongCommands;
        using (var synth = new SpeechSynthesizer())
        {
            var voice = synth.GetInstalledVoices().First(v => v.Enabled && v.VoiceInfo.Culture.Name == recognizer.Culture.Name);
            synth.SelectVoice(voice.VoiceInfo.Name);
            var next = Say(synth, "next song");
            var previous = Say(synth, "previous song");
            var later = Say(synth, "please");
            var binding = VoiceCommands.CreateBinding("custom", "next song please", "F4", null);
            var longBinding = VoiceCommands.CreateBinding("custom", "go to the last chorus of the song", "F5", new[] { binding });
            var expanded = VoiceCommands.ForMode(TabletControlMode.Automatic, new[] { binding, longBinding }).ToArray();
            var catalog = CatalogAudit.BuildGrammarFixture().ToArray();
            var timings = new List<double>();
            for (int i = 0; i < 3; i++)
            {
                timings.Add(Measure("next song", next, "next", ids, recognizer));
                timings.Add(Measure("previous song", previous, "previous", ids, recognizer));
            }
            timings.Add(Measure("long command", Say(synth, longBinding.Phrase), longBinding.Id, expanded, recognizer));
            var example = catalog.Single(c => c.Phrase == "go to section number");
            timings.Add(Measure("entire catalog enabled", Say(synth, example.Phrase), example.Id, catalog, recognizer));
            timings.Add(Measure("44.1 kHz left mixer channel", previous, "previous", ids, recognizer, 44100, 2, 0));
            timings.Add(Measure("48 kHz right mixer channel", next, "next", ids, recognizer, 48000, 2, 1));
            var paused = next.Concat(new byte[200 * 32]).Concat(later).ToArray();
            Measure("longer command after a 200 ms pause", paused, binding.Id, expanded, recognizer);
            Measure("ambiguous standalone prefix", next, "next", expanded, recognizer);
            Measure("silence", new byte[0], null, ids, recognizer);
            var tone = new byte[Rate * 2];
            for (int i = 0; i < Rate; i++)
            {
                short sample = (short)(9000 * Math.Sin(2 * Math.PI * 440 * i / Rate));
                tone[i * 2] = (byte)sample;
                tone[i * 2 + 1] = (byte)(sample >> 8);
            }
            Measure("tone", tone, null, ids, recognizer);
            timings.Add(Measure("continuous low-level noise under speech", next, "next", ids, recognizer, backgroundNoise: true));
            CheckSingleCommand(next, previous, recognizer, ids);
            CheckFailedSource(recognizer, ids);
            CompareHandsFreeEndpoints(synth, recognizer);
            CompareStandaloneTiming(synth, recognizer);
            using (var stop = new CancellationTokenSource(150))
            {
                var elapsed = Stopwatch.StartNew();
                try
                {
                    Audio.RecognizeLive(recognizer, ids, 2500, (receive, token) => Pace(next, 2500, receive, token), stop.Token);
                    throw new InvalidOperationException("Cancelled audio returned a command.");
                }
                catch (OperationCanceledException) { }
                if (elapsed.ElapsedMilliseconds > 800) throw new InvalidOperationException("Cancellation exceeded 800 ms.");
                Console.WriteLine("PASS cancellation completed after {0} ms", elapsed.ElapsedMilliseconds);
            }
            timings.Sort();
            double median = timings[timings.Count / 2];
            Console.WriteLine("Ordinary command acoustic end to returned result: median {0:0} ms, maximum {1:0} ms ({2} paced trials).",
                median, timings.Last(), timings.Count);
            if (timings.Last() >= 500) throw new InvalidOperationException("An ordinary command exceeded 500 ms.");
        }
    }

    internal static void Smoke(SpeechSynthesizer synth, RecognizerInfo recognizer)
    {
        double delay = Measure("streaming on the selected stereo channel", Say(synth, "next song"), "next",
            VoiceCommands.SongCommands, recognizer, 48000, 2, 1);
        if (delay >= 800) throw new InvalidOperationException("Live recognition waited for the fixed capture window.");
        CheckHandsFree(synth, recognizer);
        CheckHandsFreePrefixesAndRepeats(synth, recognizer);
        CheckStandaloneTalkback(synth, recognizer);
        CheckHandsFreeInputFailure(Say(synth, "next song"), recognizer);
    }

    private static void CheckHandsFreeInputFailure(byte[] spoken, RecognizerInfo recognizer)
    {
        var audio = new byte[300 * 32].Concat(spoken).ToArray();
        int blocks = (audio.Length + BytesPerBlock - 1) / BytesPerBlock;
        var unplugged = new IOException("Simulated talkback microphone disconnect.");
        int lost = 0, lateDispatches = 0;
        try
        {
            Task.Run(() => Audio.RunHandsFreeInputAsync(recognizer, VoiceCommands.SongCommands, (accept, start, token) =>
            {
                // End the device stream at the last syllable, while SAPI still holds the utterance.
                Pace(audio, blocks * Audio.BlockMilliseconds, accept, token, start: start);
                Interlocked.Exchange(ref lost, 1);
                throw unplugged;
            }, () => { }, (result, token) =>
            {
                if (Volatile.Read(ref lost) != 0) Interlocked.Increment(ref lateDispatches);
                return Task.FromResult(0);
            }, result => { }, CancellationToken.None)).GetAwaiter().GetResult();
            throw new Exception("Microphone failure was swallowed.");
        }
        catch (IOException error) { if (error != unplugged) throw; }
        if (lateDispatches != 0) throw new InvalidOperationException("A final recognizer result was dispatched after the mic failed.");

        int ready = 0;
        try
        {
            Task.Run(() => Audio.RunHandsFreeInputAsync(recognizer, VoiceCommands.SongCommands, (accept, start, token) => { },
                () => ready++, (result, token) => { throw new Exception("Empty input dispatched a command."); },
                result => { }, CancellationToken.None)).GetAwaiter().GetResult();
            throw new Exception("A microphone that never opened was accepted.");
        }
        catch (InvalidOperationException) { }
        if (ready != 0) throw new InvalidOperationException("The listener reported ready before the microphone opened.");
        Console.WriteLine("PASS a microphone disconnect discards the final utterance and an unopened input never reports ready.");
    }

    private static void CheckHandsFree(SpeechSynthesizer synth, RecognizerInfo recognizer)
    {
        var audio = new List<byte>(new byte[2250 * 32]);
        var ends = new List<double>();
        foreach (string phrase in new[] { "next song", "bring the bass guitar up a little", "previous song", "next song" })
        {
            audio.AddRange(Say(synth, phrase));
            if (phrase.EndsWith("song", StringComparison.Ordinal)) ends.Add(audio.Count / 32.0);
            audio.AddRange(new byte[(phrase.StartsWith("bring") ? 4800 : 700) * 32]);
        }
        var delays = CheckContinuousSequence(recognizer, VoiceCommands.SongCommands, audio.ToArray(), ends,
            new[] { "next", "previous", "next" }, false);
        if (delays.Any(d => d < 0 || d > 1000) || delays.OrderBy(d => d).ElementAt(1) > 400)
            throw new InvalidOperationException("Unexpected continuous recognition latency: " + String.Join(", ", delays));
        Console.WriteLine("PASS continuous stereo talkback: one mic open, three commands, ordinary speech, extended silence and stop. Waveform end to dispatch: {0} ms.",
            String.Join(", ", delays.Select(d => d.ToString("0"))));
    }

    private static void CheckHandsFreePrefixesAndRepeats(SpeechSynthesizer synth, RecognizerInfo recognizer)
    {
        var longer = VoiceCommands.CreateBinding("custom", "next song please", "F4", null);
        var grammar = VoiceCommands.ForMode(TabletControlMode.Automatic, new[] { longer }).ToArray();
        var audio = new List<byte>(new byte[600 * 32]);
        var ends = new List<double>();
        audio.AddRange(Say(synth, "the guitar comes in after the next song"));
        audio.AddRange(new byte[800 * 32]);
        audio.AddRange(Say(synth, "next song"));
        audio.AddRange(new byte[200 * 32]);
        audio.AddRange(Say(synth, "please"));
        ends.Add(audio.Count / 32.0);
        audio.AddRange(new byte[750 * 32]);
        for (int i = 0; i < 2; i++)
        {
            audio.AddRange(Say(synth, "previous song"));
            ends.Add(audio.Count / 32.0);
            audio.AddRange(new byte[420 * 32]);
        }
        var repeats = audio.ToArray();
        var followedByTalkback = repeats.Concat(Say(synth, "the guitar comes in after the next song")).ToArray();
        Action<byte[], bool, bool> checkRepeats = (pcm, fast, allowMissingFinalRepeat) =>
        {
            var delays = CheckContinuousSequence(recognizer, grammar, pcm, ends,
                new[] { longer.Id, "previous", "previous" }, true,
                verifySongEndpoints: fast, standaloneCommandsOnly: true, allowMissingFinalRepeat: allowMissingFinalRepeat);
            if (delays.Any(d => d < 0 || d > 1300))
                throw new InvalidOperationException("Early, duplicate, or late hands-free command around a prefix or repeat.");
        };
        checkRepeats(repeats, false, false);
        checkRepeats(followedByTalkback, true, false);
        // With early verification forced off, Windows can merge the last repeat and the
        // following sentence. Reject that uncertain repeat and never send the embedded "next".
        checkRepeats(followedByTalkback, false, true);
        Console.WriteLine("PASS standalone mode ignores an embedded command in room noise, accepts a paused mapped phrase and two consecutive previous commands.");
    }

    private static List<double> CheckContinuousSequence(RecognizerInfo recognizer, VoiceCommand[] grammar,
        byte[] pcm, List<double> ends, string[] expected, bool noise, bool verifySongEndpoints = true,
        bool standaloneCommandsOnly = false, bool allowMissingFinalRepeat = false)
    {
        var received = new List<string>();
        var delays = new List<double>();
        int opens = 0, ready = 0;
        long origin = 0;
        using (var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(pcm.Length / 32 + 10000)))
        {
            bool playedToEnd = false;
            try
            {
                Task.Run(() => Audio.RunHandsFreeInputAsync(recognizer, grammar, (accept, start, token) =>
                {
                    Interlocked.Increment(ref opens);
                    Pace(pcm, pcm.Length / 32 + 1200, accept, token, 48000, 2, 1, noise, t =>
                    {
                        origin = t;
                        start(t);
                    });
                    // Keep listening after the last expected event to catch delayed finals and false commands.
                    playedToEnd = true;
                    stop.Cancel();
                }, () => ready++, (result, token) =>
                {
                    int i = received.Count;
                    received.Add(result.Command);
                    if (i < ends.Count) delays.Add((Stopwatch.GetTimestamp() - origin) * 1000.0 / Stopwatch.Frequency - ends[i]);
                    return Task.FromResult(0);
                }, unmatched => { }, stop.Token, verifySongEndpoints, standaloneCommandsOnly)).GetAwaiter().GetResult();
            }
            catch (OperationCanceledException) { if (!stop.IsCancellationRequested) throw; }
            if (!playedToEnd) throw new InvalidOperationException("Continuous replay timed out before the last audio block.");
        }
        bool missingLastOnly = allowMissingFinalRepeat && received.SequenceEqual(expected.Take(expected.Length - 1));
        if (opens != 1 || ready != 1 || (!received.SequenceEqual(expected) && !missingLastOnly))
            throw new InvalidOperationException("Continuous recognition returned unexpected commands: " + String.Join(", ", received));
        return delays;
    }

    private static void CheckStandaloneTalkback(SpeechSynthesizer synth, RecognizerInfo recognizer)
    {
        var commands = VoiceCommands.ForMode(TabletControlMode.FastSongShortcuts, null).ToArray();
        var audio = new List<byte>(new byte[600 * 32]);
        var ends = new List<double>();
        var expected = new[] { "next", "previous", "scroll-down" };
        foreach (var phrase in new[] { "the guitar comes in after the next song", "next song", "previous song",
            "next song starts with the bass", "scroll down", "the guitar comes in after the next song" })
        {
            audio.AddRange(Say(synth, phrase));
            if (commands.Any(c => c.Phrase == phrase)) ends.Add(audio.Count / 32.0);
            audio.AddRange(new byte[750 * 32]);
        }
        foreach (bool fast in new[] { true, false })
        {
            var delays = CheckContinuousSequence(recognizer, commands, audio.ToArray(), ends, expected, false,
                verifySongEndpoints: fast, standaloneCommandsOnly: true);
            if (delays.Any(d => d < 0 || d > 1000))
                throw new InvalidOperationException("Standalone mode dispatched outside an intentional command's timing window.");
        }
        Console.WriteLine("PASS standalone commands before and after incidental talkback using both early verification and Windows final recognition.");
    }

    private static void CompareStandaloneTiming(SpeechSynthesizer synth, RecognizerInfo recognizer)
    {
        var grammar = VoiceCommands.ForMode(TabletControlMode.FastSongShortcuts, null).ToArray();
        var audio = new List<byte>(new byte[600 * 32]);
        var ends = new List<double>();
        var ids = new[] { "next", "previous", "next", "previous", "next", "previous" };
        foreach (string id in ids)
        {
            audio.AddRange(Say(synth, grammar.Single(c => c.Id == id).Phrase));
            ends.Add(audio.Count / 32.0);
            audio.AddRange(new byte[700 * 32]);
        }
        var pcm = audio.ToArray();
        var off = CheckContinuousSequence(recognizer, grammar, pcm, ends, ids, true);
        var on = CheckContinuousSequence(recognizer, grammar, pcm, ends, ids, true, standaloneCommandsOnly: true);
        if (off.Any(d => d < 0) || on.Any(d => d < 0))
            throw new InvalidOperationException("The listener dispatched before the command ended.");
        foreach (string id in ids.Distinct())
        {
            double before = off.Where((d, i) => ids[i] == id).OrderBy(d => d).ElementAt(1);
            double after = on.Where((d, i) => ids[i] == id).OrderBy(d => d).ElementAt(1);
            Console.WriteLine("Standalone mode acoustic end to dispatch for {0}: off {1:0} ms, on {2:0} ms (3 matched trials per setting).", id, before, after);
            if (after - before > 60)
                throw new InvalidOperationException("Standalone mode added more than three 20 ms capture blocks.");
        }
    }

    private static void CompareHandsFreeEndpoints(SpeechSynthesizer synth, RecognizerInfo recognizer)
    {
        var audio = new List<byte>(new byte[500 * 32]);
        var ends = new List<double>();
        foreach (string phrase in new[] { "next song", "previous song", "next song" })
        {
            audio.AddRange(Say(synth, phrase));
            ends.Add(audio.Count / 32.0);
            audio.AddRange(new byte[700 * 32]);
        }
        var pcm = audio.ToArray();
        var expected = new[] { "next", "previous", "next" };
        var normal = CheckContinuousSequence(recognizer, VoiceCommands.SongCommands, pcm, ends, expected, false, false);
        var verified = CheckContinuousSequence(recognizer, VoiceCommands.SongCommands, pcm, ends, expected, false);
        double normalMedian = normal.OrderBy(x => x).ElementAt(1), fastMedian = verified.OrderBy(x => x).ElementAt(1);
        Console.WriteLine("Hands-free acoustic end to dispatch for identical paced input: normal {0:0} ms, verified {1:0} ms (medians).",
            normalMedian, fastMedian);
        if (verified.Any(d => d < 0) || fastMedian >= normalMedian - 80)
            throw new InvalidOperationException("Early song recognition did not improve median dispatch by 80 ms.");
    }

    private static void CheckSingleCommand(byte[] first, byte[] second, RecognizerInfo recognizer, VoiceCommand[] commands)
    {
        var audio = new byte[300 * 32].Concat(first).Concat(new byte[700 * 32]).Concat(second).ToArray();
        int consumed = 0;
        var result = Audio.RecognizeLive(recognizer, commands, 2500, (receive, token) =>
            Pace(audio, 2500, (block, rate, channels) => { consumed += block.Length; return receive(block, rate, channels); }, token),
            CancellationToken.None);
        if (result.Command != "next" || consumed >= (1000 * 32 + first.Length))
            throw new InvalidOperationException("A single listen continued into a second valid phrase.");
        Console.WriteLine("PASS one result returned before the next spoken command");
    }

    private static void CheckFailedSource(RecognizerInfo recognizer, VoiceCommand[] commands)
    {
        var empty = Audio.RecognizeLive(recognizer, commands, 2500,
            (receive, token) => Stopwatch.GetTimestamp(), CancellationToken.None);
        if (empty.Command != null || !Double.IsNegativeInfinity(empty.PeakDbFs))
            throw new InvalidOperationException("An empty device stream returned a command or input level.");
        bool released = false;
        bool receivedError = false;
        var unplugged = new IOException("Simulated microphone unplugged.");
        try
        {
            Audio.RecognizeLive(recognizer, commands, 2500, (receive, token) =>
            {
                try
                {
                    receive(new byte[BytesPerBlock], Rate, 1);
                    throw unplugged;
                }
                finally { released = true; }
            }, CancellationToken.None);
        }
        catch (IOException error) { if (error != unplugged) throw; receivedError = true; }
        if (!released || !receivedError) throw new InvalidOperationException("Input failure or cleanup was lost.");
        Console.WriteLine("PASS empty device stream and input failure both release the decoder");
    }

    // Drop synthesis leading/trailing silence; the last sample above -50 dBFS anchors the independent acoustic-end clock.
    private static byte[] Say(SpeechSynthesizer synth, string phrase)
    {
        using (var stream = new MemoryStream())
        {
            synth.SetOutputToAudioStream(stream, new SpeechAudioFormatInfo(Rate, AudioBitsPerSample.Sixteen, AudioChannel.Mono));
            synth.Speak(phrase);
            synth.SetOutputToNull();
            byte[] samples = stream.ToArray();
            int first = 0;
            int last = samples.Length - 2;
            while (first < last && Math.Abs((int)BitConverter.ToInt16(samples, first)) < 104) first += 2;
            while (last > first && Math.Abs((int)BitConverter.ToInt16(samples, last)) < 104) last -= 2;
            return samples.Skip(first).Take(last - first + 2).ToArray();
        }
    }

    private static double Measure(string name, byte[] spoken, string expected, VoiceCommand[] commands, RecognizerInfo recognizer,
        int sampleRate = Rate, ushort channels = 1, int activeChannel = 0, bool backgroundNoise = false)
    {
        const int leadingMs = 300;
        long started = 0;
        int window = Audio.CaptureMilliseconds(commands);
        byte[] audio = new byte[leadingMs * 32].Concat(spoken).ToArray();
        var result = Audio.RecognizeLive(recognizer, commands, window, (receive, token) =>
        {
            started = Pace(audio, window, receive, token, sampleRate, channels, activeChannel, backgroundNoise);
            return started;
        }, CancellationToken.None);
        long returned = Stopwatch.GetTimestamp();
        if (result.Command != expected)
            throw new InvalidOperationException(name + ": " + (result.Command ?? "no match") + " (expected " + (expected ?? "no match") + ").");
        double afterEnd = (returned - started) * 1000.0 / Stopwatch.Frequency - audio.Length / 32.0;
        if (expected == null) Console.WriteLine("PASS {0}: no command; peak {1:0.0} dBFS", name, result.PeakDbFs);
        else
        {
            if (!result.SpeechStartTimestamp.HasValue || !result.SpeechEndTimestamp.HasValue ||
                result.SpeechStartTimestamp < started || result.SpeechEndTimestamp <= result.SpeechStartTimestamp || result.SpeechEndTimestamp > returned)
                throw new InvalidOperationException("Invalid monotonic acoustic timestamps.");
            Console.WriteLine("PASS {0}: {1:0} ms after waveform end; {2:0} ms after recognizer's speech end",
                name, afterEnd, (returned - result.SpeechEndTimestamp.Value) * 1000.0 / Stopwatch.Frequency);
        }
        return afterEnd;
    }

    private static long Pace(byte[] audio, int window, Func<byte[], uint, ushort, bool> receive, CancellationToken token,
        int sampleRate = Rate, ushort channels = 1, int activeChannel = 0, bool backgroundNoise = false, Action<long> start = null)
    {
        var block = new byte[BytesPerBlock];
        var frames = sampleRate * Audio.BlockMilliseconds / 1000;
        var mixer = new byte[frames * channels * 2];
        var noise = new Random(98341);
        long started = Stopwatch.GetTimestamp();
        if (start != null) start(started);
        for (int elapsed = Audio.BlockMilliseconds; elapsed <= window; elapsed += Audio.BlockMilliseconds)
        {
            int offset = (elapsed / Audio.BlockMilliseconds - 1) * block.Length;
            Array.Clear(block, 0, block.Length);
            if (offset < audio.Length) Buffer.BlockCopy(audio, offset, block, 0, Math.Min(block.Length, audio.Length - offset));
            for (int frame = 0; frame < frames; frame++)
            {
                int value = BitConverter.ToInt16(block, (frame * Rate / sampleRate) * 2);
                if (backgroundNoise) value = Math.Max(-32768, Math.Min(32767, value + noise.Next(-230, 231)));
                int index = (frame * channels + activeChannel) * 2;
                mixer[index] = (byte)value;
                mixer[index + 1] = (byte)(value >> 8);
            }
            double remaining = elapsed - (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
            if (remaining > 0) token.WaitHandle.WaitOne((int)Math.Ceiling(remaining));
            token.ThrowIfCancellationRequested();
            if (!receive(mixer, (uint)sampleRate, channels)) break;
        }
        return started;
    }
}

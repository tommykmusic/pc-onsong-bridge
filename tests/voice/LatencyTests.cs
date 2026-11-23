using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Speech.AudioFormat;
using System.Speech.Recognition;
using System.Speech.Synthesis;
using System.Threading;
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
            Measure("continuous low-level noise under speech", next, "next", ids, recognizer, backgroundNoise: true);
            CheckSingleCommand(next, previous, recognizer, ids);
            CheckFailedSource(recognizer, ids);
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
        int sampleRate = Rate, ushort channels = 1, int activeChannel = 0, bool backgroundNoise = false)
    {
        var block = new byte[BytesPerBlock];
        var frames = sampleRate * Audio.BlockMilliseconds / 1000;
        var mixer = new byte[frames * channels * 2];
        var noise = new Random(98341);
        long started = Stopwatch.GetTimestamp();
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

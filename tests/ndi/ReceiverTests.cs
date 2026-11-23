using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Threading;
using PcOnSongBridge;

internal static class ReceiverTests
{
    private static void Check(bool passed, string detail) { if (!passed) throw new Exception(detail); }
    private static void WaitFor(Func<bool> check, string detail)
    {
        Check(SpinWait.SpinUntil(check, TimeSpan.FromSeconds(4)), detail);
    }

    private sealed class Capture : INdiCapture
    {
        public volatile bool Online, Closed;
        public long DroppedPackets { get; set; }
        public readonly BlockingCollection<object> Events = new BlockingCollection<object>();
        public bool Connected { get { return Online; } }
        public int Read(out NdiAudioBlock data)
        {
            object entry;
            Events.TryTake(out entry, 20);
            if (entry is Exception) throw (Exception)entry;
            data = entry as NdiAudioBlock;
            return data != null ? 2 : entry is int ? (int)entry : 0;
        }
        public void Dispose() { Closed = true; }
    }

    private sealed class Playback : INdiPlayback
    {
        public volatile bool Lost, Closed;
        public readonly ConcurrentQueue<short[]> Writes = new ConcurrentQueue<short[]>();
        public readonly ManualResetEventSlim EnteredWrite = new ManualResetEventSlim(false);
        public readonly ManualResetEventSlim DriverReady = new ManualResetEventSlim(true);
        public int Rate { get { return 48000; } }
        public long Played { get; private set; }
        public void Write(short[] samples, CancellationToken token)
        {
            EnteredWrite.Set();
            DriverReady.Wait(token);
            Writes.Enqueue(samples);
            Played += samples.Length / 2;
        }
        public void Poll() { if (Lost) throw new InvalidOperationException("Selected output unplugged."); }
        public void Dispose() { Closed = true; }
    }

    private static void Main()
    {
        CheckConversion();
        CheckSession();
        CheckSlowDriver(false);
        CheckSlowDriver(true);
        CheckDiscoveryAddress();
        CheckRuntimeIfAvailable();
        CheckEndpointIfSelected();
        Console.WriteLine("PASS NDI cue onset, stereo, rate conversion, idle meter, reconnect, stop and device loss.");
    }

    private static void CheckSlowDriver(bool saturate)
    {
        var input = new Capture { Online = true, DroppedPackets = 3 };
        var playback = new Playback();
        playback.DriverReady.Reset();
        using (var receiver = new NdiReceiver(s => input, e => playback))
        {
            receiver.StartAsync(new NdiSource("Sleeping driver fixture", ""), "endpoint", CancellationToken.None).Wait();
            int blocks = saturate ? 13 : 5;
            const int size = 4800;
            for (int n = 0; n < blocks; n++)
            {
                var cue = new float[size];
                for (int i = 0; i < cue.Length; i++) cue[i] = (1 + n + i % 9) / 32768f;
                input.Events.Add(new NdiAudioBlock(48000, 1, cue, cue));
                if (n == 0) Check(playback.EnteredWrite.Wait(2000), "Driver failed to accept first write.");
            }
            WaitFor(() => receiver.GetStatus().AudioFrames == blocks, "A blocked driver stopped native capture.");
            long expectedDrops = saturate ? 2 * size : 0;
            Check(receiver.GetStatus().DroppedFrames == expectedDrops && receiver.GetStatus().DroppedAudioPackets == 3,
                "Overflow accounting mixed SDK packets and output sample frames.");
            playback.DriverReady.Set();
            WaitFor(() => receiver.GetStatus().PlayedFrames == blocks * size - expectedDrops, "Queued audio failed to drain.");
            int block = 0;
            foreach (short[] samples in playback.Writes)
            {
                for (int i = 0; i < samples.Length; i++) Check(samples[i] == 1 + block + (i / 2) % 9, "Blocked playback changed cue samples or order.");
                block++;
            }
            Check(block == blocks - expectedDrops / size, "Incorrect output buffer count.");
            playback.EnteredWrite.Reset();
            playback.DriverReady.Reset();
            input.Events.Add(new NdiAudioBlock(48000, 1, new[] { .1f }, new[] { .1f }));
            Check(playback.EnteredWrite.Wait(2000), "Driver did not receive final packet.");
            if (saturate)
            {
                input.Events.Add(new InvalidOperationException("Test capture unavailable."));
                WaitFor(() => !receiver.GetStatus().Running && receiver.GetStatus().Error == "Test capture unavailable.",
                    "Capture failure failed to stop a blocked renderer or lost the error.");
            }
            Check(receiver.StopAsync().Wait(2000) && input.Closed && playback.Closed, "Stopping a blocked render worker leaked a handle.");
        }
    }

    private static void CheckDiscoveryAddress()
    {
        var tablet = IPAddress.Parse("192.0.2.5");
        Check(NdiAudioNative.AddressMatches("192.0.2.5:5961", tablet) &&
            NdiAudioNative.AddressMatches("ndi://192.0.2.5:5961/", tablet) &&
            !NdiAudioNative.AddressMatches("192.0.2.50:5961", tablet) &&
            !NdiAudioNative.AddressMatches("ndi://192.0.2.5@192.0.2.50:5961", tablet) &&
            !NdiAudioNative.AddressMatches("localhost:5961", tablet) &&
            !NdiAudioNative.AddressMatches("", tablet), "Targeted discovery leaked an unrelated source or failed to match an address.");
        using (var receiver = new NdiReceiver((string)null))
        {
            bool rejected = false;
            try { receiver.DiscoverAtAddressAsync("192.0.2.5,192.0.2.6", CancellationToken.None); }
            catch (ArgumentException) { rejected = true; }
            Check(rejected, "Targeted discovery accepted multiple hosts.");
            bool canceled = false;
            try { receiver.DiscoverAtAddressAsync("192.0.2.5", new CancellationToken(true)).GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { canceled = true; }
            Check(canceled, "Canceled targeted discovery opened the runtime.");
        }
    }

    private static void CheckConversion()
    {
        var converter = new NdiAudioConverter(48000);
        double peak;
        var audio = converter.Convert(new NdiAudioBlock(48000, 2, new[] { 0.5f, -0.1f, -2f, float.NaN },
            new[] { -0.5f, 0.1f, 2f, 0f }), out peak);
        Check(audio[0] == 16384 && audio[1] == -16384 && audio[4] == -32768 && audio[5] == 32767 && audio[6] == 0 && peak == 1,
            "First sample, channel separation, clipping or nonfinite handling failed.");
        converter = new NdiAudioConverter(48000);
        var input = new float[441];
        for (int i = 0; i < input.Length; i++) input[i] = .25f;
        int total = 0;
        for (int i = 0; i < 100; i++)
        {
            audio = converter.Convert(new NdiAudioBlock(44100, 1, input, input), out peak);
            total += audio.Length / 2;
            foreach (short v in audio) Check(v == 8192, "Resampling introduced a boundary click or swapped mono.");
        }
        Check(Math.Abs(48000 - total) <= 1, "Resampling drifted across packet boundaries.");
        converter = new NdiAudioConverter(44100);
        input = new float[1];
        total = 0;
        for (int i = 0; i < 48000; i++)
            total += converter.Convert(new NdiAudioBlock(48000, 1, input, input), out peak).Length / 2;
        Check(total == 44100, "Downsampling single-sample packets drifted.");
    }

    private static void CheckSession()
    {
        var source = new NdiSource("Practice (OnSong)", "192.0.2.1:5961");
        var input = new Capture();
        var output = new Playback();
        using (var receiver = new NdiReceiver(s => { Check(s == source, "Source changed."); return input; },
            id => { Check(id == "chosen-endpoint", "Output selection changed."); return output; }))
        {
            receiver.StartAsync(source, "chosen-endpoint", CancellationToken.None).GetAwaiter().GetResult();
            Check(receiver.GetStatus().Running && receiver.GetStatus().AudioFrames == 0, "Receiver failed to wait for first cue.");
            input.Online = true;
            input.Events.Add(1);
            WaitFor(() => receiver.GetStatus().VideoFrames == 1, "Video status missing.");
            var mono = new[] { 0.5f, 0.1f, 0f };
            var cue = new NdiAudioBlock(48000, 1, mono, mono);
            input.Events.Add(cue);
            WaitFor(() => receiver.GetStatus().PlayedFrames == 3, "First cue after silence lost.");
            var status = receiver.GetStatus();
            short[] written;
            Check(output.Writes.TryPeek(out written) && written[0] == 16384 && written[1] == 16384 &&
                status.Connected && status.Peak == 0.5 && status.LastSignalUtc != DateTime.MinValue, "Incorrect first audio packet or meter.");
            input.Online = false;
            input.Events.Add(4);
            WaitFor(() => !receiver.GetStatus().Connected && receiver.GetStatus().Peak == 0, "Meter hung after silence or source loss.");
            Check(receiver.GetStatus().Running, "An idle network receiver stopped.");
            input.Online = true;
            input.Events.Add(cue);
            WaitFor(() => receiver.GetStatus().PlayedFrames == 6, "First cue after reconnect lost.");
            output.Lost = true;
            WaitFor(() => !receiver.GetStatus().Running && output.Closed && input.Closed, "Device loss leaked or redirected audio.");
            Check(receiver.GetStatus().Error == "Selected output unplugged.", "Device failure was not visible.");
            receiver.StopAsync().GetAwaiter().GetResult();
        }

        input = new Capture();
        output = new Playback();
        using (var receiver = new NdiReceiver(s => input, id => output))
        using (var cancellation = new CancellationTokenSource())
        {
            receiver.StartAsync(source, "chosen-endpoint", cancellation.Token).GetAwaiter().GetResult();
            cancellation.Cancel();
            receiver.StopAsync().GetAwaiter().GetResult();
            Check(input.Closed && output.Closed && !receiver.GetStatus().Running, "Cancel did not clean up both handles.");
        }
    }

    private static void CheckRuntimeIfAvailable()
    {
        string file = Environment.GetEnvironmentVariable("ONSONG_TEST_NDI_RUNTIME");
        if (String.IsNullOrEmpty(file)) return;
        Check(File.Exists(file), "Selected test runtime unavailable.");
        if (Environment.Is64BitProcess)
            Check(Marshal.SizeOf(typeof(NdiAudioNative.FindOptions)) == 24 && Marshal.SizeOf(typeof(NdiAudioNative.ReceiverOptions)) == 40 &&
                Marshal.SizeOf(typeof(NdiAudioNative.AudioFrame)) == 56 && Marshal.SizeOf(typeof(NdiAudioNative.VideoFrame)) == 72,
                "Unexpected native ABI layout.");
        using (NdiAudioNative.Acquire(file))
        using (var input = new NdiAudioInput(file, new NdiSource("Offline-" + Guid.NewGuid(), "127.0.0.1:1")))
        {
            NdiAudioBlock block;
            input.Read(out block);
            Check(block == null, "An unselected source produced audio.");
        }
        Console.WriteLine("PASS official NDI runtime load, public exports, local receiver creation and teardown.");
        string target = Environment.GetEnvironmentVariable("ONSONG_TEST_NDI_FIND_IP");
        if (!String.IsNullOrEmpty(target))
        {
            using (var receiver = new NdiReceiver(file))
            {
                var found = receiver.DiscoverAtAddressAsync(target, CancellationToken.None).GetAwaiter().GetResult();
                Check(found.Count > 0, "The NDI finder received no source attributable to the selected tablet.");
                foreach (var item in found) Console.WriteLine("Targeted NDI finder: " + item.Name + " at " + item.Address);
            }
        }
    }

    private static void CheckEndpointIfSelected()
    {
        string endpoint = Environment.GetEnvironmentVariable("ONSONG_TEST_NDI_ENDPOINT");
        if (String.IsNullOrEmpty(endpoint)) return;
        var input = new Capture { Online = true };
        using (var receiver = new NdiReceiver(source => input, id => new Audio.NdiWaveOutput(id)))
        {
            receiver.StartAsync(new NdiSource("Simulated silent input", ""), endpoint, CancellationToken.None).GetAwaiter().GetResult();
            // Exercise the actual render driver using digital silence only.
            var zeros = new float[4800];
            var silent = new NdiAudioBlock(48000, 2, zeros, zeros);
            bool matched = false;
            for (int i = 0; i < 5; i++)
            {
                input.Events.Add(silent);
                Thread.Sleep(85);
                matched |= PlaybackRoute.Inspect(Process.GetCurrentProcess().Id, endpoint).State == PlaybackRouteState.Matched;
            }
            WaitFor(() => receiver.GetStatus().PlayedFrames > 0, "Windows did not complete any selected endpoint buffers.");
            Check(matched && receiver.GetStatus().DroppedFrames == 0, "Playback did not remain on the selected endpoint.");
        }
        Console.WriteLine("PASS silent PCM through the selected physical Windows endpoint and active render session.");
    }
}

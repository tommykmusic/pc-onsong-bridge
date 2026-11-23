using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace PcOnSongBridge
{
    internal sealed class NdiSource
    {
        public readonly string Name, Address;
        public NdiSource(string name, string address) { Name = name ?? ""; Address = address ?? ""; }
        public override string ToString() { return Name.Length == 0 ? Address : Name; }
    }

    internal sealed class NdiReceiverStatus
    {
        public bool Running { get; internal set; }
        public bool Connected { get; internal set; }
        public long VideoFrames { get; internal set; }
        public long AudioFrames { get; internal set; }
        public long PlayedFrames { get; internal set; }
        public long DroppedFrames { get; internal set; }
        public long DroppedAudioPackets { get; internal set; }
        public int SampleRate { get; internal set; }
        public int Channels { get; internal set; }
        public double Peak { get; internal set; }
        public string Error { get; internal set; }
        public DateTime LastAudioUtc { get; internal set; }
        public DateTime LastSignalUtc { get; internal set; }
        internal NdiReceiverStatus Copy() { return (NdiReceiverStatus)MemberwiseClone(); }
    }

    internal interface INdiCapture : IDisposable
    {
        // Read returns within 100 ms, including when the sender goes offline or stops sending audio.
        int Read(out NdiAudioBlock audio);
        bool Connected { get; }
        long DroppedPackets { get; }
    }

    internal interface INdiPlayback : IDisposable
    {
        int Rate { get; }
        long Played { get; }
        void Write(short[] stereo, CancellationToken token);
        void Poll();
    }

    internal sealed class NdiReceiver : IDisposable
    {
        private readonly object gate = new object();
        private readonly string runtime;
        private readonly Func<NdiSource, INdiCapture> openCapture;
        private readonly Func<string, INdiPlayback> openOutput;
        private NdiReceiverStatus status = new NdiReceiverStatus();
        private CancellationTokenSource stop;
        private Task worker = Task.FromResult(true);
        private bool disposed;
        private long peakAt;

        public NdiReceiver(string runtimeDllPath)
            : this(source => new NdiAudioInput(runtimeDllPath, source), endpoint => new Audio.NdiWaveOutput(endpoint))
        {
            runtime = runtimeDllPath;
        }

        internal NdiReceiver(Func<NdiSource, INdiCapture> capture, Func<string, INdiPlayback> output)
        { openCapture = capture; openOutput = output; }

        public Task<List<NdiSource>> DiscoverAsync(CancellationToken token)
        {
            lock (gate) if (disposed) throw new ObjectDisposedException("NdiReceiver");
            return Task.Run(() => NdiAudioNative.Discover(runtime, token), token);
        }

        public Task<List<NdiSource>> DiscoverAtAddressAsync(string tabletIp, CancellationToken token)
        {
            IPAddress address;
            if (!IPAddress.TryParse(tabletIp, out address) || address.AddressFamily != AddressFamily.InterNetwork ||
                address.ToString() != tabletIp)
                throw new ArgumentException("Enter the tablet's Wi-Fi IPv4 address.");
            lock (gate) if (disposed) throw new ObjectDisposedException("NdiReceiver");
            return Task.Run(() => NdiAudioNative.Discover(runtime, token, address), token);
        }

        public Task StartAsync(NdiSource source, string endpointId, CancellationToken token)
        {
            if (source == null || (String.IsNullOrWhiteSpace(source.Name) && String.IsNullOrWhiteSpace(source.Address)))
                throw new ArgumentException("Choose an OnSong NDI source.");
            if (String.IsNullOrWhiteSpace(endpointId)) throw new ArgumentException("Choose a Windows playback device.");
            token.ThrowIfCancellationRequested();
            lock (gate)
            {
                if (disposed) throw new ObjectDisposedException("NdiReceiver");
                if (!worker.IsCompleted) throw new InvalidOperationException("Stop the current NDI receiver first.");
                var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
                stop = lifetime;
                status = new NdiReceiverStatus();
                peakAt = 0;
                worker = Task.Factory.StartNew(() => Run(source, endpointId, lifetime, started),
                    CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                return started.Task;
            }
        }

        private void Run(NdiSource source, string endpointId, CancellationTokenSource lifetime, TaskCompletionSource<bool> started)
        {
            try
            {
                lifetime.Token.ThrowIfCancellationRequested();
                using (INdiPlayback output = openOutput(endpointId))
                using (INdiCapture input = openCapture(source))
                {
                    var converter = new NdiAudioConverter(output.Rate);
                    // A sleeping render device can take hundreds of milliseconds to accept the first cue.
                    // Drain the small native receive queue independently while the driver wakes.
                    var pending = new PcmQueue(output.Rate);
                    lifetime.Token.ThrowIfCancellationRequested();
                    Task render = Task.Factory.StartNew(() => Render(output, pending, lifetime),
                        CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
                    try
                    {
                        lock (gate) status.Running = true;
                        started.TrySetResult(true);
                        while (!lifetime.IsCancellationRequested)
                        {
                            NdiAudioBlock block;
                            int kind = input.Read(out block);
                            lifetime.Token.ThrowIfCancellationRequested();
                            bool connected = input.Connected;
                            long nativeDrops = input.DroppedPackets;
                            double peak = 0;
                            int overflow = block == null ? 0 : pending.Add(converter.Convert(block, out peak));
                            lock (gate)
                            {
                                status.Connected = connected;
                                status.DroppedAudioPackets = nativeDrops;
                                status.DroppedFrames += overflow;
                                if (kind == 1) status.VideoFrames++;
                                if (block != null)
                                {
                                    status.AudioFrames++;
                                    status.LastAudioUtc = DateTime.UtcNow;
                                    status.SampleRate = block.Rate;
                                    status.Channels = block.Channels;
                                    if (peak > 0.001) status.LastSignalUtc = status.LastAudioUtc;
                                    long now = Stopwatch.GetTimestamp();
                                    if (peak >= status.Peak || now - peakAt > Stopwatch.Frequency / 2)
                                    { status.Peak = peak; peakAt = now; }
                                }
                            }
                            // The NDI receiver reconnects to the selected source when it returns.
                            if (kind == 4) lifetime.Token.WaitHandle.WaitOne(100);
                        }
                    }
                    finally { lifetime.Cancel(); render.GetAwaiter().GetResult(); }
                }
            }
            catch (OperationCanceledException) { started.TrySetCanceled(); }
            catch (Exception ex)
            {
                lock (gate) status.Error = ex.Message;
                started.TrySetException(ex);
            }
            finally
            {
                lock (gate) { status.Running = false; status.Connected = false; status.Peak = 0; stop = null; }
                lifetime.Dispose();
            }
        }

        private void Render(INdiPlayback output, PcmQueue pending, CancellationTokenSource lifetime)
        {
            CancellationToken token = lifetime.Token;
            try
            {
                while (!token.IsCancellationRequested)
                {
                    output.Poll();
                    lock (gate) status.PlayedFrames = output.Played;
                    short[] samples = pending.Take();
                    if (samples != null) output.Write(samples, token);
                }
            }
            catch (OperationCanceledException) { if (!token.IsCancellationRequested) throw; }
            finally { lifetime.Cancel(); }
        }

        private sealed class PcmQueue
        {
            private readonly Queue<short[]> blocks = new Queue<short[]>();
            private readonly int limit;
            private int frames;
            internal PcmQueue(int rate) { limit = rate; } // One second maximum, with no prefill delay.

            internal int Add(short[] samples)
            {
                lock (blocks)
                {
                    int count = samples.Length / 2;
                    int keep = Math.Min(count, limit - frames);
                    if (keep > 0)
                    {
                        if (keep < count) Array.Resize(ref samples, keep * 2);
                        blocks.Enqueue(samples);
                        frames += keep;
                        Monitor.Pulse(blocks);
                    }
                    return count - keep;
                }
            }

            internal short[] Take()
            {
                lock (blocks)
                {
                    if (blocks.Count == 0) Monitor.Wait(blocks, 20); // Check the output even during silence.
                    if (blocks.Count == 0) return null;
                    short[] samples = blocks.Dequeue();
                    frames -= samples.Length / 2;
                    return samples;
                }
            }
        }

        public NdiReceiverStatus GetStatus()
        {
            lock (gate)
            {
                var snapshot = status.Copy();
                if (Stopwatch.GetTimestamp() - peakAt > Stopwatch.Frequency / 2) snapshot.Peak = 0;
                return snapshot;
            }
        }

        public Task StopAsync()
        {
            lock (gate) { if (stop != null) stop.Cancel(); return worker; }
        }

        public void Dispose()
        {
            lock (gate) disposed = true;
            StopAsync().GetAwaiter().GetResult();
        }
    }
}

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Speech.AudioFormat;
using System.Speech.Recognition;
using System.Threading;
using System.Threading.Tasks;

namespace PcOnSongBridge
{
    internal static partial class Audio
    {
        internal const int BlockMilliseconds = 20;
        private const int PrefixPauseMilliseconds = 450;
        // Return the Stopwatch timestamp corresponding to the first sample.
        internal delegate long CaptureSource(Func<byte[], uint, ushort, bool> receive, CancellationToken token);

        internal static MicrophoneResult RecognizeLive(RecognizerInfo recognizer, VoiceCommand[] enabled,
            int milliseconds, CaptureSource capture, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using (var input = new LiveSpeechStream(milliseconds))
            using (var engine = new SpeechRecognitionEngine(recognizer))
            {
                engine.LoadGrammar(BuildCommandGrammar(recognizer, enabled));
                engine.InitialSilenceTimeout = TimeSpan.FromMilliseconds(milliseconds);
                engine.EndSilenceTimeout = TimeSpan.FromMilliseconds(120);
                engine.EndSilenceTimeoutAmbiguous = TimeSpan.FromMilliseconds(PrefixPauseMilliseconds);
                engine.SetInputToAudioStream(input, new SpeechAudioFormatInfo(SpeechSampleRate, AudioBitsPerSample.Sixteen, AudioChannel.Mono));
                // A completed grammar phrase can be confirmed as soon as the input goes quiet.
                // SAPI's streaming decoder continues through pauses, noise, or a disputed endpoint.
                var ambiguous = enabled.Where(c => enabled.Any(other =>
                    other.Phrase.StartsWith(c.Phrase + " ", StringComparison.OrdinalIgnoreCase))).Select(c => c.Id).ToArray();
                var recognition = Task.Run(() => engine.Recognize());
                Task<MicrophoneResult> candidate = null;
                int candidateActivity = 0;
                long candidateDuration = 0;
                MicrophoneResult early = null;
                MicrophoneResult proposed = null;
                long started;
                RecognitionResult result;
                try
                {
                    started = capture((pcm, rate, channels) =>
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        input.Append(pcm, rate, channels);
                        if (candidate != null && candidate.IsCompleted)
                        {
                            proposed = candidate.GetAwaiter().GetResult();
                            candidate = null;
                        }
                        if (proposed != null && proposed.Command != null && candidateActivity == input.LastActivity &&
                            (!ambiguous.Contains(proposed.Command) || input.QuietMilliseconds >= PrefixPauseMilliseconds))
                            early = proposed;
                        if (early != null || recognition.IsCompleted) return false;
                        if (candidate == null && input.QuietMilliseconds >= 160 && candidateActivity != input.LastActivity)
                        {
                            candidateActivity = input.LastActivity;
                            candidateDuration = input.RecordedTicks;
                            proposed = null;
                            var samples = input.EndpointSample();
                            candidate = Task.Run(() => ConfirmEndpoint(samples, recognizer, enabled, cancellationToken));
                        }
                        return true;
                    }, cancellationToken);
                }
                finally
                {
                    // Wake a pending SAPI read on stop, cancellation, unplug, or end of window.
                    input.Complete();
                    try { result = recognition.GetAwaiter().GetResult(); }
                    finally { if (candidate != null) candidate.GetAwaiter().GetResult(); }
                }
                cancellationToken.ThrowIfCancellationRequested();
                var match = early ?? AcceptedResult(result, enabled, input.PeakDbFs, 0);
                return ToCaptureClock(match, started, early == null ? input.RecordedTicks : candidateDuration, input.PeakDbFs);
            }
        }

        private static MicrophoneResult ConfirmEndpoint(byte[] samples, RecognizerInfo recognizer, VoiceCommand[] commands,
            CancellationToken token)
        {
            // An engine which cannot open a second recognizer can still finish the live stream normally.
            try { return AnalyzeRecording(samples, SpeechSampleRate, 1, recognizer, commands, token, 0); }
            catch (InvalidOperationException) { return null; }
            catch (COMException) { return null; }
        }

        private static MicrophoneResult ToCaptureClock(MicrophoneResult match, long started, long recordedTicks, double peakDbFs)
        {
            // The verifier pads with virtual silence. Only actual captured samples are timestamps.
            if (!match.SpeechStartTimestamp.HasValue || !match.SpeechEndTimestamp.HasValue)
                return new MicrophoneResult(peakDbFs, match.Command);
            long start = Math.Max(0, match.SpeechStartTimestamp.Value);
            long end = Math.Min(recordedTicks, match.SpeechEndTimestamp.Value);
            return end > start ? new MicrophoneResult(peakDbFs, match.Command, started + start, started + end) :
                new MicrophoneResult(peakDbFs, match.Command);
        }

        private static long CaptureBlocks(AudioDevice device, int milliseconds, Func<byte[], uint, ushort, bool> receive,
            CancellationToken token, Action onListening)
        {
            token.ThrowIfCancellationRequested();
            WaveFormat format;
            IntPtr handle = OpenDevice(device, true, out format);
            var buffers = new WaveBuffer[8];
            var prepared = new bool[buffers.Length];
            try
            {
                int size = (int)format.SampleRate * BlockMilliseconds / 1000 * format.BlockAlign;
                var samples = new byte[size];
                for (int index = 0; index < buffers.Length; index++)
                {
                    WaveBuffer buffer = buffers[index] = new WaveBuffer(size);
                    Check(Native.waveInPrepareHeader(handle, buffer.Header, HeaderSize), true, "Prepare microphone input");
                    prepared[index] = true;
                    Check(Native.waveInAddBuffer(handle, buffer.Header, HeaderSize), true, "Queue microphone input");
                }
                token.ThrowIfCancellationRequested();
                long started = Stopwatch.GetTimestamp();
                Check(Native.waveInStart(handle), true, "Start recording");
                if (onListening != null) onListening();
                for (int block = 0; block < milliseconds / BlockMilliseconds; block++)
                {
                    var buffer = buffers[block % buffers.Length];
                    var header = WaitForBuffer(buffer, 2000, token);
                    if (header.BytesRecorded != size)
                        throw new InvalidOperationException("The selected microphone stopped delivering audio. Refresh the audio device list and select it again.");
                    Marshal.Copy(buffer.Data, samples, 0, samples.Length);
                    if (!receive(samples, format.SampleRate, format.Channels)) break;
                    Check(Native.waveInAddBuffer(handle, buffer.Header, HeaderSize), true, "Queue microphone input");
                }
                return started;
            }
            finally
            {
                Native.waveInReset(handle);
                for (int i = 0; i < buffers.Length; i++)
                    if (prepared[i] && Native.waveInUnprepareHeader(handle, buffers[i].Header, HeaderSize) == 0)
                        prepared[i] = false;
                bool closed = Native.waveInClose(handle) == 0;
                for (int i = 0; i < buffers.Length; i++)
                    if (buffers[i] != null && (closed || !prepared[i])) buffers[i].Dispose();
            }
        }

        // SAPI's IStream adapter requires Length and Position before it starts recording.
        // The bounded window also permits SAPI to seek back over audio it has already read.
        internal sealed class LiveSpeechStream : Stream
        {
            private readonly object sync = new object();
            private readonly byte[] data;
            private int written;
            private int position;
            private bool complete;
            private int peak;
            private readonly int[] initialPeaks = new int[5];
            private int initialPeakCount;
            private int ambientPeak;
            internal int LastActivity { get; private set; }
            internal int QuietMilliseconds { get { return (written - LastActivity) * 1000 / (SpeechSampleRate * 2); } }
            internal long RecordedTicks { get { return written * (long)Stopwatch.Frequency / (SpeechSampleRate * 2); } }

            internal LiveSpeechStream(int milliseconds) { data = new byte[milliseconds * (SpeechSampleRate / 1000) * 2]; }
            internal double PeakDbFs { get { return peak == 0 ? Double.NegativeInfinity : 20 * Math.Log10(peak / 32768.0); } }

            internal void Append(byte[] pcm, uint sampleRate, ushort channels)
            {
                // WinMM delivers 20 ms at 16, 44.1 or 48 kHz, all exact integral blocks of 320 output frames.
                int stride = channels * 2;
                int frames = pcm.Length / stride;
                int channel = FindLouderChannel(pcm, channels, frames);
                int count = (int)(frames * (long)SpeechSampleRate / sampleRate);
                int blockPeak = 0;
                for (int offset = 0; offset + 1 < pcm.Length; offset += 2)
                    blockPeak = Math.Max(blockPeak, Math.Abs((int)ReadSample(pcm, offset)));
                peak = Math.Max(blockPeak, peak);
                if (initialPeakCount < initialPeaks.Length)
                {
                    initialPeaks[initialPeakCount++] = blockPeak;
                    if (initialPeakCount == initialPeaks.Length)
                    {
                        // One startup click or empty device block should not set the room level.
                        Array.Sort(initialPeaks);
                        int median = initialPeaks[initialPeaks.Length / 2];
                        if (median <= 600) ambientPeak = median;
                    }
                }
                lock (sync)
                {
                    int previous = 0;
                    for (int i = 0; i < count && written + 1 < data.Length; i++)
                    {
                        int next = (int)((i + 1) * (long)sampleRate / SpeechSampleRate);
                        int sum = 0;
                        for (int frame = previous; frame < next; frame++)
                            sum += ReadSample(pcm, frame * stride + channel * 2);
                        short sample = (short)(sum / (next - previous));
                        data[written++] = (byte)sample;
                        data[written++] = (byte)(sample >> 8);
                        previous = next;
                    }
                    // Accelerate when trailing blocks drop near silence or back to the initial room floor.
                    int floor = peak > ambientPeak * 3 ? ambientPeak * 2 : 0;
                    if (blockPeak > Math.Max(24, Math.Max(floor, Math.Min(180, peak / 100)))) LastActivity = written;
                    Monitor.PulseAll(sync);
                }
            }

            internal byte[] EndpointSample()
            {
                // A finite utterance needs trailing silence for the desktop engine to finish scoring it.
                var sample = new byte[written + SpeechSampleRate * 2 / 2];
                Buffer.BlockCopy(data, 0, sample, 0, written);
                return sample;
            }

            internal void Complete() { lock (sync) { complete = true; Monitor.PulseAll(sync); } }

            public override int Read(byte[] buffer, int offset, int count)
            {
                lock (sync)
                {
                    count = Math.Min(count, data.Length - position);
                    while (!complete && written - position < count) Monitor.Wait(sync);
                    int size = Math.Max(0, Math.Min(count, written - position));
                    Buffer.BlockCopy(data, position, buffer, offset, size);
                    position += size;
                    return size;
                }
            }

            public override long Seek(long offset, SeekOrigin origin)
            {
                lock (sync)
                {
                    long next = origin == SeekOrigin.End ? Length + offset : origin == SeekOrigin.Current ? position + offset : offset;
                    if (next < 0 || next > Length) throw new IOException("Seek outside the active speech window.");
                    return position = (int)next;
                }
            }

            public override bool CanRead { get { return true; } }
            public override bool CanWrite { get { return false; } }
            public override bool CanSeek { get { return true; } }
            public override long Length { get { return data.Length; } }
            public override long Position { get { lock (sync) return position; } set { Seek(value, SeekOrigin.Begin); } }
            public override void Flush() { }
            public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
            public override void SetLength(long value) { throw new NotSupportedException(); }
        }
    }
}

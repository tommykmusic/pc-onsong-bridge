using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;

namespace PcOnSongBridge
{
    internal sealed class NdiAudioBlock
    {
        internal readonly int Rate, Channels;
        internal readonly float[] Left, Right;
        internal NdiAudioBlock(int rate, int channels, float[] left, float[] right)
        { Rate = rate; Channels = channels; Left = left; Right = right; }
    }

    // Convert OnSong's planar float main mix to the selected device's stereo PCM format.
    // Mono is duplicated. For a multi-output mix, channels one and two are the stereo pair.
    internal sealed class NdiAudioConverter
    {
        private readonly int targetRate;
        private int previousRate;
        private float lastLeft, lastRight;
        private long next;
        private double currentGain = 1, gainStep, targetGain = 1;
        private int? lastGainDb;
        private int rampFrames;
        internal NdiAudioConverter(int rate) { targetRate = rate; }

        internal short[] Convert(NdiAudioBlock frame, out double peak)
        {
            double outputPeak;
            return Convert(frame, 0, out peak, out outputPeak);
        }

        internal short[] Convert(NdiAudioBlock frame, int gainDb, out double peak, out double outputPeak)
        {
            int count = frame.Left.Length;
            if (previousRate != frame.Rate)
            {
                previousRate = frame.Rate;
                next = 0;
                lastLeft = lastRight = 0;
            }
            long last = (long)(count - 1) * targetRate;
            int length = next > last ? 0 : (int)((last - next) / frame.Rate + 1);
            var pcm = new short[length * 2];
            if (lastGainDb != gainDb)
            {
                targetGain = Math.Pow(10, gainDb / 20.0);
                // Start at the selected level. Smooth live adjustments over 5 ms to avoid a click.
                if (lastGainDb == null) currentGain = targetGain;
                else
                {
                    rampFrames = Math.Max(1, targetRate / 200);
                    gainStep = (targetGain - currentGain) / rampFrames;
                }
                lastGainDb = gainDb;
            }
            peak = outputPeak = 0;
            for (int i = 0; i < pcm.Length; i += 2, next += frame.Rate)
            {
                if (rampFrames > 0)
                    currentGain = --rampFrames == 0 ? targetGain : currentGain + gainStep;
                int right = (int)((next + targetRate - 1) / targetRate);
                double weight = 1.0 - (right * (long)targetRate - next) / (double)targetRate;
                float a = right == 0 ? lastLeft : frame.Left[right - 1];
                float b = right == 0 ? lastRight : frame.Right[right - 1];
                pcm[i] = Sample(a + (frame.Left[right] - a) * weight, ref peak, ref outputPeak);
                pcm[i + 1] = Sample(b + (frame.Right[right] - b) * weight, ref peak, ref outputPeak);
            }
            next -= (long)count * targetRate;
            lastLeft = frame.Left[count - 1];
            lastRight = frame.Right[count - 1];
            return pcm;
        }

        private short Sample(double value, ref double peak, ref double outputPeak)
        {
            if (Double.IsNaN(value)) return 0;
            peak = Math.Max(peak, Math.Min(1, Math.Abs(value)));
            double scaled = value * currentGain;
            outputPeak = Math.Max(outputPeak, Math.Abs(scaled));
            return (short)Math.Max(-32768, Math.Min(32767, Math.Round(scaled * 32768)));
        }
    }

    internal static partial class Audio
    {
        internal sealed class NdiWaveOutput : INdiPlayback
        {
            private sealed class Pending
            {
                internal WaveBuffer Buffer;
                internal int Frames;
                internal long Submitted;
            }
            private readonly Queue<Pending> buffers = new Queue<Pending>();
            private readonly AudioDevice device;
            private IntPtr handle;
            private int queued;
            private long nextDeviceCheck;
            public int Rate { get; private set; }
            public long Played { get; private set; }

            public NdiWaveOutput(string endpointId)
            {
                device = GetOutputs().FirstOrDefault(d => String.Equals(d.EndpointId, endpointId, StringComparison.OrdinalIgnoreCase));
                if (device == null) throw new InvalidOperationException("The selected Windows playback device is no longer available.");
                VerifyDevice(device, false);
                WaveFormat format;
                handle = OpenDevice(device, false, out format);
                try { VerifyDevice(device, false); }
                catch { Native.waveOutClose(handle); handle = IntPtr.Zero; throw; }
                Rate = (int)format.SampleRate;
            }

            public void Write(short[] stereo, CancellationToken token)
            {
                // Submit immediately. Only the render worker waits if the driver's quarter-second buffer is full.
                int offset = 0;
                while (offset < stereo.Length)
                {
                    token.ThrowIfCancellationRequested();
                    int frames = Math.Min((stereo.Length - offset) / 2, Rate / 4 - queued);
                    if (frames == 0)
                    {
                        Poll();
                        if (queued >= Rate / 4) token.WaitHandle.WaitOne(5);
                        continue;
                    }
                    Submit(stereo, offset, frames);
                    offset += frames * 2;
                }
            }

            private void Submit(short[] stereo, int offset, int frames)
            {
                var buffer = new WaveBuffer(frames * 4);
                bool prepared = false;
                try
                {
                    Marshal.Copy(stereo, offset, buffer.Data, frames * 2);
                    Check(Native.waveOutPrepareHeader(handle, buffer.Header, HeaderSize), false, "Prepare NDI cue");
                    prepared = true;
                    Check(Native.waveOutWrite(handle, buffer.Header, HeaderSize), false, "Play NDI cue");
                    buffers.Enqueue(new Pending { Buffer = buffer, Frames = frames, Submitted = Stopwatch.GetTimestamp() });
                    queued += frames;
                }
                catch
                {
                    if (!prepared || Native.waveOutUnprepareHeader(handle, buffer.Header, HeaderSize) == 0) buffer.Dispose();
                    throw;
                }
            }

            public void Poll()
            {
                long now = Stopwatch.GetTimestamp();
                if (now >= nextDeviceCheck)
                {
                    VerifyDevice(device, false);
                    nextDeviceCheck = now + Stopwatch.Frequency;
                }
                while (buffers.Count != 0)
                {
                    Pending next = buffers.Peek();
                    var header = (WaveHeader)Marshal.PtrToStructure(next.Buffer.Header, typeof(WaveHeader));
                    if ((header.Flags & WaveHeaderDone) == 0)
                    {
                        if (now - next.Submitted > 3 * Stopwatch.Frequency)
                            throw new InvalidOperationException("The selected Windows device stopped playing cue audio. Reconnect it and restart Wi-Fi cues.");
                        break;
                    }
                    Check(Native.waveOutUnprepareHeader(handle, next.Buffer.Header, HeaderSize), false, "Finish NDI playback");
                    next.Buffer.Dispose();
                    buffers.Dequeue();
                    queued -= next.Frames;
                    Played += next.Frames;
                }
            }

            public void Dispose()
            {
                if (handle == IntPtr.Zero) return;
                Native.waveOutReset(handle);
                var held = new List<WaveBuffer>();
                while (buffers.Count != 0)
                {
                    WaveBuffer buffer = buffers.Dequeue().Buffer;
                    if (Native.waveOutUnprepareHeader(handle, buffer.Header, HeaderSize) == 0) buffer.Dispose();
                    else held.Add(buffer);
                }
                if (Native.waveOutClose(handle) == 0)
                    foreach (WaveBuffer buffer in held) buffer.Dispose();
                handle = IntPtr.Zero;
            }
        }
    }
}

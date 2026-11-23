using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Speech.Recognition;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PcOnSongBridge
{
    internal sealed class AudioDevice
    {
        public uint Id { get; private set; }
        // Keep the WinMM name for device validation and for preferences from earlier bridge versions.
        public string Name { get; private set; }
        public string DisplayName { get; private set; }
        public string EndpointId { get; private set; }

        internal AudioDevice(uint id, string name, string endpointId = null, string friendlyName = null)
        {
            Id = id;
            Name = name;
            DisplayName = String.IsNullOrWhiteSpace(friendlyName) ? name : friendlyName;
            EndpointId = endpointId;
        }

        public override string ToString() { return DisplayName; }
    }

    internal sealed class MicrophoneResult
    {
        internal readonly double PeakDbFs;
        internal readonly string Command;
        internal readonly long? SpeechStartTimestamp;
        internal readonly long? SpeechEndTimestamp;

        internal MicrophoneResult(double peakDbFs, string command, long? speechStartTimestamp = null, long? speechEndTimestamp = null)
        {
            PeakDbFs = peakDbFs;
            Command = command;
            SpeechStartTimestamp = speechStartTimestamp;
            SpeechEndTimestamp = speechEndTimestamp;
        }
    }

    internal static partial class Audio
    {
        private const int RecordingMilliseconds = 2500;
        private const int SpeechSampleRate = 16000;
        internal const float CommandConfidence = 0.80f;
        private const uint WaveHeaderDone = 1;
        private static readonly uint HeaderSize = (uint)Marshal.SizeOf(typeof(WaveHeader));

        public static List<AudioDevice> GetOutputs() { return GetDevices(false); }
        public static List<AudioDevice> GetInputs() { return GetDevices(true); }

        public static Task PlayTestToneAsync(AudioDevice device)
        {
            return Task.Run(() => PlayTestTone(device));
        }

        // Live feedback when the microphone heard sound but no ready phrase. The level tells a musician
        // whether to check the input gain or simply repeat the phrase.
        internal static string NoCommandMessage(double peakDbFs)
        {
            if (Double.IsNegativeInfinity(peakDbFs)) return "No microphone signal. Check the selected input.";
            string level = "peak " + peakDbFs.ToString("0.#", System.Globalization.CultureInfo.CurrentCulture) + " dBFS";
            if (peakDbFs < -60)
                return "No usable microphone signal (" + level + "). Check that the mic is connected, unmuted, and selected.";
            if (peakDbFs < -40)
                return "No command recognized. The microphone was very quiet (" + level + "). Move closer or raise the input level.";
            if (peakDbFs >= -1)
                return "No command recognized. The microphone peaked near full scale (" + level + "). Lower the input gain slightly and try again.";
            return "No command recognized (" + level + "). Say the phrase clearly after Listening appears.";
        }

        public static RecognizerInfo FindEnglishRecognizer()
        {
            return SpeechRecognitionEngine.InstalledRecognizers()
                .Where(recognizer => recognizer.Culture.TwoLetterISOLanguageName == "en")
                .OrderByDescending(recognizer => recognizer.Culture.Name == "en-US")
                .ThenBy(recognizer => recognizer.Culture.Name, StringComparer.Ordinal)
                .ThenBy(recognizer => recognizer.Id, StringComparer.Ordinal)
                .FirstOrDefault();
        }

        internal static Task<MicrophoneResult> TestMicrophoneAsync(AudioDevice device, RecognizerInfo recognizer, CancellationToken cancellationToken)
        {
            return TestMicrophoneAsync(device, recognizer, VoiceCommands.SongCommands, cancellationToken);
        }

        internal static int CaptureMilliseconds(IEnumerable<VoiceCommand> commands)
        {
            int words = commands.Select(c => c.Phrase.Split(' ').Length).DefaultIfEmpty(0).Max();
            return RecordingMilliseconds + Math.Min(6, Math.Max(0, words - 2)) * 500;
        }

        internal static Task<MicrophoneResult> TestMicrophoneAsync(AudioDevice device, RecognizerInfo recognizer,
            IEnumerable<VoiceCommand> commands, CancellationToken cancellationToken, Action onListening = null)
        {
            VoiceCommand[] enabled = commands.ToArray();
            return Task.Run(() =>
            {
                VerifyDevice(device, true);
                int milliseconds = CaptureMilliseconds(enabled);
                if (recognizer != null && enabled.Length > 0)
                    return RecognizeLive(recognizer, enabled, milliseconds,
                        (receive, token) => CaptureBlocks(device, milliseconds, receive, token, onListening), cancellationToken);
                WaveFormat format;
                long started;
                byte[] recording = Capture(device, milliseconds, out format, out started, cancellationToken, onListening);
                return AnalyzeRecording(recording, format.SampleRate, format.Channels, recognizer, enabled, cancellationToken, started);
            }, cancellationToken);
        }

        private static List<AudioDevice> GetDevices(bool input)
        {
            var devices = new List<AudioDevice>();
            uint count = input ? Native.waveInGetNumDevs() : Native.waveOutGetNumDevs();
            for (uint id = 0; id < count; id++)
            {
                string name = GetDeviceName(id, input);
                if (String.IsNullOrWhiteSpace(name)) continue;
                string endpointId = PlaybackRoute.GetWaveEndpointId((int)id, input);
                devices.Add(new AudioDevice(id, name, endpointId, PlaybackRoute.GetEndpointFriendlyName(endpointId)));
            }
            return devices;
        }

        private static string GetDeviceName(uint id, bool input)
        {
            if (input)
            {
                WaveInCaps caps;
                uint error = Native.waveInGetDevCaps(new UIntPtr(id), out caps, (uint)Marshal.SizeOf(typeof(WaveInCaps)));
                return error == 0 ? caps.Name : null;
            }

            WaveOutCaps output;
            uint outputError = Native.waveOutGetDevCaps(new UIntPtr(id), out output, (uint)Marshal.SizeOf(typeof(WaveOutCaps)));
            return outputError == 0 ? output.Name : null;
        }

        private static void VerifyDevice(AudioDevice device, bool input)
        {
            if (device == null)
                throw new InvalidOperationException(input ? "Select a Windows recording device." : "Select a Windows playback device.");
            if (device.Id == UInt32.MaxValue || !String.Equals(device.Name, GetDeviceName(device.Id, input), StringComparison.Ordinal))
                throw new InvalidOperationException("The selected audio device changed. Refresh the audio device list and select it again.");
            if (device.EndpointId != null &&
                !String.Equals(device.EndpointId, PlaybackRoute.GetWaveEndpointId((int)device.Id, input), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The selected audio device has moved. Refresh the audio device list and select it again.");
        }

        private static void PlayTestTone(AudioDevice device)
        {
            VerifyDevice(device, false);
            WaveFormat format;
            IntPtr handle = OpenDevice(device, false, out format);
            WaveBuffer buffer = null;
            bool prepared = false;
            try
            {
                byte[] samples = CreateTestTone(format);
                buffer = new WaveBuffer(samples.Length);
                Marshal.Copy(samples, 0, buffer.Data, samples.Length);
                Check(Native.waveOutPrepareHeader(handle, buffer.Header, HeaderSize), false, "Prepare playback");
                prepared = true;
                Check(Native.waveOutWrite(handle, buffer.Header, HeaderSize), false, "Play the test tone");
                WaitForBuffer(buffer, 3000, CancellationToken.None);
            }
            finally
            {
                Native.waveOutReset(handle);
                bool released = !prepared || Native.waveOutUnprepareHeader(handle, buffer.Header, HeaderSize) == 0;
                if (Native.waveOutClose(handle) == 0)
                    released = true;
                if (buffer != null && released)
                    buffer.Dispose();
            }
        }

        private static byte[] CreateTestTone(WaveFormat format)
        {
            int pulse = (int)format.SampleRate * 180 / 1000;
            int gap = (int)format.SampleRate * 120 / 1000;
            int fade = (int)format.SampleRate * 10 / 1000;
            var samples = new byte[(2 * pulse + gap) * format.BlockAlign];
            for (int frame = 0; frame < 2 * pulse + gap; frame++)
            {
                int position = frame % (pulse + gap);
                if (position >= pulse)
                    continue;
                double envelope = Math.Min(1.0, Math.Min(position, pulse - 1 - position) / (double)fade);
                short sample = (short)(520 * envelope * Math.Sin(2 * Math.PI * 700 * position / format.SampleRate));
                for (int channel = 0; channel < format.Channels; channel++)
                {
                    int index = frame * format.BlockAlign + channel * 2;
                    samples[index] = unchecked((byte)sample);
                    samples[index + 1] = unchecked((byte)(sample >> 8));
                }
            }
            return samples;
        }

        internal static MicrophoneResult AnalyzeRecording(byte[] samples, uint sampleRate, ushort channels, RecognizerInfo recognizer,
            CancellationToken cancellationToken)
        {
            return AnalyzeRecording(samples, sampleRate, channels, recognizer, VoiceCommands.SongCommands, cancellationToken);
        }

        internal static MicrophoneResult AnalyzeRecording(byte[] samples, uint sampleRate, ushort channels, RecognizerInfo recognizer,
            IEnumerable<VoiceCommand> commands, CancellationToken cancellationToken, long? captureStartTimestamp = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int peak = 0;
            for (int offset = 0; offset + 1 < samples.Length; offset += 2)
                peak = Math.Max(peak, Math.Abs((int)ReadSample(samples, offset)));
            if (peak == 0) return new MicrophoneResult(Double.NegativeInfinity, null);

            double peakDbFs = 20 * Math.Log10(peak / 32768.0);
            if (recognizer == null) return new MicrophoneResult(peakDbFs, null);
            var enabled = commands.ToArray();
            if (enabled.Length == 0) return new MicrophoneResult(peakDbFs, null);
            using (MemoryStream wave = ConvertToSpeechWave(samples, sampleRate, channels))
            {
                if (wave == null) return new MicrophoneResult(peakDbFs, null);
                return RecognizeWave(wave, peakDbFs, recognizer, enabled, captureStartTimestamp, cancellationToken);
            }
        }

        internal static Grammar BuildCommandGrammar(RecognizerInfo recognizer, IEnumerable<VoiceCommand> enabled)
        {
            var phrases = new Choices();
            foreach (var command in enabled)
            {
                var phrase = new GrammarBuilder { Culture = recognizer.Culture };
                phrase.Append(command.Phrase);
                phrases.Add(new SemanticResultValue(phrase, command.Id));
            }
            var builder = new GrammarBuilder { Culture = recognizer.Culture };
            builder.Append(phrases);
            return new Grammar(builder);
        }

        private static MicrophoneResult RecognizeWave(Stream wave, double peakDbFs, RecognizerInfo recognizer,
            VoiceCommand[] enabled, long? captureStart, CancellationToken cancellationToken)
        {
            using (var engine = new SpeechRecognitionEngine(recognizer))
            {
                engine.LoadGrammar(BuildCommandGrammar(recognizer, enabled));
                engine.InitialSilenceTimeout = TimeSpan.FromMilliseconds(CaptureMilliseconds(enabled));
                engine.SetInputToWaveStream(wave);
                cancellationToken.ThrowIfCancellationRequested();
                RecognitionResult result = engine.Recognize();
                cancellationToken.ThrowIfCancellationRequested();
                return AcceptedResult(result, enabled, peakDbFs, captureStart);
            }
        }

        private static MicrophoneResult AcceptedResult(RecognitionResult result, VoiceCommand[] enabled,
            double peakDbFs, long? captureStart)
        {
            if (result == null || result.Confidence < CommandConfidence)
                return new MicrophoneResult(peakDbFs, null);
            string id = result.Semantics.Value as string;
            if (!enabled.Any(c => c.Id == id)) return new MicrophoneResult(peakDbFs, null);
            var audio = result.Audio;
            if (audio == null || !captureStart.HasValue || audio.Duration <= TimeSpan.Zero)
                return new MicrophoneResult(peakDbFs, id);
            long start = captureStart.Value + (long)Math.Round(audio.AudioPosition.TotalSeconds * Stopwatch.Frequency);
            long end = start + (long)Math.Round(audio.Duration.TotalSeconds * Stopwatch.Frequency);
            return new MicrophoneResult(peakDbFs, id, start, end);
        }

        private static byte[] Capture(AudioDevice device, int milliseconds, out WaveFormat format, out long captureStart,
            CancellationToken cancellationToken, Action onListening = null)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IntPtr handle = OpenDevice(device, true, out format);
            WaveBuffer buffer = null;
            bool prepared = false;
            try
            {
                int byteCount = (int)format.SampleRate * milliseconds / 1000 * format.BlockAlign;
                buffer = new WaveBuffer(byteCount);
                Check(Native.waveInPrepareHeader(handle, buffer.Header, HeaderSize), true, "Prepare recording");
                prepared = true;
                Check(Native.waveInAddBuffer(handle, buffer.Header, HeaderSize), true, "Queue the microphone buffer");
                captureStart = Stopwatch.GetTimestamp();
                Check(Native.waveInStart(handle), true, "Start recording");
                if (onListening != null) onListening();
                WaveHeader header = WaitForBuffer(buffer, milliseconds + 2000, cancellationToken);
                int validBytes = (int)Math.Min(header.BytesRecorded, (uint)byteCount);
                var samples = new byte[validBytes - validBytes % format.BlockAlign];
                Marshal.Copy(buffer.Data, samples, 0, samples.Length);
                return samples;
            }
            finally
            {
                Native.waveInReset(handle);
                bool released = !prepared || Native.waveInUnprepareHeader(handle, buffer.Header, HeaderSize) == 0;
                if (Native.waveInClose(handle) == 0)
                    released = true;
                if (buffer != null && released)
                    buffer.Dispose();
            }
        }

        private static MemoryStream ConvertToSpeechWave(byte[] samples, uint sampleRate, ushort channels)
        {
            int blockAlign = channels * 2;
            int frames = samples.Length / blockAlign;
            if (frames == 0)
                return null;

            int channel = FindLouderChannel(samples, channels, frames);
            int speechFrames = checked((int)(frames * (long)SpeechSampleRate / sampleRate));
            var wave = new MemoryStream(44 + speechFrames * 2);
            using (var writer = new BinaryWriter(wave, Encoding.ASCII, true))
            {
                writer.Write(Encoding.ASCII.GetBytes("RIFF"));
                writer.Write(36 + speechFrames * 2);
                writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
                writer.Write(16);
                writer.Write((ushort)1);
                writer.Write((ushort)1);
                writer.Write(SpeechSampleRate);
                writer.Write(SpeechSampleRate * 2);
                writer.Write((ushort)2);
                writer.Write((ushort)16);
                writer.Write(Encoding.ASCII.GetBytes("data"));
                writer.Write(speechFrames * 2);

                int previous = 0;
                for (int index = 0; index < speechFrames; index++)
                {
                    int next = (int)((index + 1) * (long)sampleRate / SpeechSampleRate);
                    int sum = 0;
                    for (int frame = previous; frame < next; frame++)
                        sum += ReadSample(samples, frame * blockAlign + channel * 2);
                    writer.Write((short)(sum / (next - previous)));
                    previous = next;
                }
            }
            wave.Position = 0;
            return wave;
        }

        private static int FindLouderChannel(byte[] samples, ushort channels, int frames)
        {
            if (channels == 1)
                return 0;

            // The selected USB recording pair must contain only the speech feed and silence.
            long leftEnergy = 0;
            long rightEnergy = 0;
            for (int frame = 0; frame < frames; frame++)
            {
                int index = frame * channels * 2;
                int left = ReadSample(samples, index);
                int right = ReadSample(samples, index + 2);
                leftEnergy += left * left;
                rightEnergy += right * right;
            }
            return rightEnergy > leftEnergy ? 1 : 0;
        }

        private static short ReadSample(byte[] samples, int offset)
        {
            return unchecked((short)(samples[offset] | samples[offset + 1] << 8));
        }

        private static WaveHeader WaitForBuffer(WaveBuffer buffer, int timeout, CancellationToken cancellationToken)
        {
            var timer = Stopwatch.StartNew();
            while (timer.ElapsedMilliseconds < timeout)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var header = (WaveHeader)Marshal.PtrToStructure(buffer.Header, typeof(WaveHeader));
                if ((header.Flags & WaveHeaderDone) != 0)
                    return header;
                Thread.Sleep(10);
            }
            throw new TimeoutException("The selected audio device stopped responding.");
        }

        private static IntPtr OpenDevice(AudioDevice device, bool input, out WaveFormat format)
        {
            var choices = input
                ? new[] { Format(48000, 2), Format(44100, 2), Format(48000, 1), Format(44100, 1), Format(16000, 1) }
                : new[] { Format(48000, 2), Format(44100, 2) };
            uint error = 0;
            foreach (WaveFormat choice in choices)
            {
                IntPtr handle;
                format = choice;
                error = input
                    ? Native.waveInOpen(out handle, new UIntPtr(device.Id), ref format, IntPtr.Zero, IntPtr.Zero, 0)
                    : Native.waveOutOpen(out handle, new UIntPtr(device.Id), ref format, IntPtr.Zero, IntPtr.Zero, 0);
                if (error == 0)
                    return handle;
                const uint badFormat = 32;
                if (error != badFormat)
                    break;
            }
            Check(error, input, "Open " + device.DisplayName);
            throw new InvalidOperationException("No compatible audio format was found.");
        }

        private static WaveFormat Format(uint sampleRate, ushort channels)
        {
            ushort blockAlign = (ushort)(channels * 2);
            return new WaveFormat
            {
                FormatTag = 1,
                SampleRate = sampleRate,
                Channels = channels,
                BlockAlign = blockAlign,
                BytesPerSecond = sampleRate * blockAlign,
                BitsPerSample = 16
            };
        }

        private static void Check(uint error, bool input, string operation)
        {
            if (error == 0)
                return;
            var text = new StringBuilder(256);
            uint lookupError = input
                ? Native.waveInGetErrorText(error, text, (uint)text.Capacity)
                : Native.waveOutGetErrorText(error, text, (uint)text.Capacity);
            string message = lookupError == 0 ? text.ToString() : "Windows audio error " + error;
            throw new InvalidOperationException(operation + ": " + message);
        }

        private sealed class WaveBuffer : IDisposable
        {
            public readonly IntPtr Data;
            public readonly IntPtr Header;

            public WaveBuffer(int byteCount)
            {
                Data = Marshal.AllocHGlobal(byteCount);
                try
                {
                    Header = Marshal.AllocHGlobal((int)HeaderSize);
                    Marshal.StructureToPtr(new WaveHeader { Data = Data, BufferLength = (uint)byteCount }, Header, false);
                }
                catch
                {
                    if (Header != IntPtr.Zero)
                        Marshal.FreeHGlobal(Header);
                    Marshal.FreeHGlobal(Data);
                    throw;
                }
            }

            public void Dispose()
            {
                Marshal.FreeHGlobal(Header);
                Marshal.FreeHGlobal(Data);
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WaveInCaps
        {
            public ushort ManufacturerId;
            public ushort ProductId;
            public uint DriverVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name;
            public uint Formats;
            public ushort Channels;
            public ushort Reserved;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct WaveOutCaps
        {
            public ushort ManufacturerId;
            public ushort ProductId;
            public uint DriverVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name;
            public uint Formats;
            public ushort Channels;
            public ushort Reserved;
            public uint Support;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 2)]
        private struct WaveFormat
        {
            public ushort FormatTag;
            public ushort Channels;
            public uint SampleRate;
            public uint BytesPerSecond;
            public ushort BlockAlign;
            public ushort BitsPerSample;
            public ushort ExtraSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WaveHeader
        {
            public IntPtr Data;
            public uint BufferLength;
            public uint BytesRecorded;
            public UIntPtr User;
            public uint Flags;
            public uint Loops;
            public IntPtr Next;
            public UIntPtr Reserved;
        }

        private static class Native
        {
            [DllImport("winmm.dll")] public static extern uint waveInGetNumDevs();
            [DllImport("winmm.dll")] public static extern uint waveOutGetNumDevs();
            [DllImport("winmm.dll", CharSet = CharSet.Unicode)] public static extern uint waveInGetDevCaps(UIntPtr id, out WaveInCaps caps, uint size);
            [DllImport("winmm.dll", CharSet = CharSet.Unicode)] public static extern uint waveOutGetDevCaps(UIntPtr id, out WaveOutCaps caps, uint size);
            [DllImport("winmm.dll", CharSet = CharSet.Unicode)] public static extern uint waveInGetErrorText(uint error, StringBuilder text, uint length);
            [DllImport("winmm.dll", CharSet = CharSet.Unicode)] public static extern uint waveOutGetErrorText(uint error, StringBuilder text, uint length);
            [DllImport("winmm.dll")] public static extern uint waveInOpen(out IntPtr handle, UIntPtr id, ref WaveFormat format, IntPtr callback, IntPtr instance, uint flags);
            [DllImport("winmm.dll")] public static extern uint waveOutOpen(out IntPtr handle, UIntPtr id, ref WaveFormat format, IntPtr callback, IntPtr instance, uint flags);
            [DllImport("winmm.dll")] public static extern uint waveInPrepareHeader(IntPtr handle, IntPtr header, uint size);
            [DllImport("winmm.dll")] public static extern uint waveOutPrepareHeader(IntPtr handle, IntPtr header, uint size);
            [DllImport("winmm.dll")] public static extern uint waveInUnprepareHeader(IntPtr handle, IntPtr header, uint size);
            [DllImport("winmm.dll")] public static extern uint waveOutUnprepareHeader(IntPtr handle, IntPtr header, uint size);
            [DllImport("winmm.dll")] public static extern uint waveInAddBuffer(IntPtr handle, IntPtr header, uint size);
            [DllImport("winmm.dll")] public static extern uint waveOutWrite(IntPtr handle, IntPtr header, uint size);
            [DllImport("winmm.dll")] public static extern uint waveInStart(IntPtr handle);
            [DllImport("winmm.dll")] public static extern uint waveInReset(IntPtr handle);
            [DllImport("winmm.dll")] public static extern uint waveOutReset(IntPtr handle);
            [DllImport("winmm.dll")] public static extern uint waveInClose(IntPtr handle);
            [DllImport("winmm.dll")] public static extern uint waveOutClose(IntPtr handle);
        }
    }
}

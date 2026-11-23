using System;
using System.Collections.Concurrent;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Speech.AudioFormat;
using System.Speech.Recognition;
using System.Threading;
using System.Threading.Tasks;

namespace PcOnSongBridge
{
    internal delegate Task HandsFreeSession(AudioDevice device, RecognizerInfo recognizer, VoiceCommand[] commands,
        Action ready, Func<MicrophoneResult, CancellationToken, Task> dispatch, Action<MicrophoneResult> noMatch,
        CancellationToken token);

    internal static class HandsFreeListening
    {
        // The UI owns navigation. Only one command may be dispatched at a time.
        internal static async Task RunAsync(Func<CancellationToken, Task<MicrophoneResult>> read,
            Func<MicrophoneResult, CancellationToken, Task> dispatch, Action<MicrophoneResult> noMatch, CancellationToken token)
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var heard = await read(token);
                token.ThrowIfCancellationRequested();
                if (String.IsNullOrEmpty(heard.Command)) noMatch(heard);
                else await dispatch(heard, token);
            }
        }
    }

    internal static partial class Audio
    {
        internal delegate void OpenSpeechInput(Func<byte[], uint, ushort, bool> accept, Action<long> started, CancellationToken token);
        private static int handsFreeActive;

        internal static Task ListenHandsFreeAsync(AudioDevice device, RecognizerInfo recognizer, VoiceCommand[] commands,
            Action ready, Func<MicrophoneResult, CancellationToken, Task> dispatch, Action<MicrophoneResult> noMatch, CancellationToken token)
        {
            return RunHandsFreeInputAsync(recognizer, commands, (accept, started, cancel) =>
            {
                VerifyDevice(device, true);
                CaptureBlocks(device, Timeout.Infinite, accept, cancel, null, started);
            }, ready, dispatch, noMatch, token);
        }

        internal static async Task RunHandsFreeInputAsync(RecognizerInfo recognizer, VoiceCommand[] commands, OpenSpeechInput open,
            Action ready, Func<MicrophoneResult, CancellationToken, Task> dispatch, Action<MicrophoneResult> noMatch, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (Interlocked.CompareExchange(ref handsFreeActive, 1, 0) != 0)
                throw new InvalidOperationException("Stop listening in the other voice control window first.");
            try
            {
                using (var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token))
                using (var results = new BlockingCollection<MicrophoneResult>(4))
                {
                    var opened = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    var worker = Task.Run(() =>
                    {
                        try { RecognizeHandsFree(recognizer, commands, open, opened, results, lifetime); }
                        finally { results.CompleteAdding(); }
                    });
                    Exception failure = null;
                    try
                    {
                        await Task.WhenAny(opened.Task, worker);
                        if (!opened.Task.IsCompleted || worker.IsFaulted)
                        {
                            await worker;
                            throw new InvalidOperationException("The microphone closed before listening started.");
                        }
                        lifetime.Token.ThrowIfCancellationRequested();
                        ready();
                        await HandsFreeListening.RunAsync(async cancel =>
                        {
                            var result = await Task.Run(() =>
                            {
                                MicrophoneResult item;
                                return results.TryTake(out item, Timeout.Infinite, cancel) ? item : null;
                            }, cancel);
                            if (result == null)
                            {
                                await worker;
                                throw new InvalidOperationException("The microphone stream ended. Check the input and start listening again.");
                            }
                            if (worker.IsFaulted) await worker;
                            return result;
                        }, dispatch, noMatch, lifetime.Token);
                    }
                    catch (Exception error) { failure = error; }
                    lifetime.Cancel();
                    try { await worker; }
                    catch (OperationCanceledException) { }
                    catch (Exception error) { if (failure == null || failure is OperationCanceledException) failure = error; }
                    if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
                }
            }
            finally { Interlocked.Exchange(ref handsFreeActive, 0); }
        }

        private static void RecognizeHandsFree(RecognizerInfo recognizer, VoiceCommand[] commands, OpenSpeechInput open,
            TaskCompletionSource<bool> opened, BlockingCollection<MicrophoneResult> results, CancellationTokenSource lifetime)
        {
            var token = lifetime.Token;
            using (var engine = new SpeechRecognitionEngine(recognizer))
            using (var input = new TalkbackStream())
            using (var stopInput = CancellationTokenSource.CreateLinkedTokenSource(token))
            using (var completed = new ManualResetEventSlim())
            {
                long firstSample = 0;
                Exception failure = null;
                engine.LoadGrammar(BuildCommandGrammar(recognizer, commands));
                engine.InitialSilenceTimeout = TimeSpan.Zero;
                engine.EndSilenceTimeout = TimeSpan.FromMilliseconds(120);
                engine.EndSilenceTimeoutAmbiguous = TimeSpan.FromMilliseconds(PrefixPauseMilliseconds);
                engine.SetInputToAudioStream(input, new SpeechAudioFormatInfo(SpeechSampleRate, AudioBitsPerSample.Sixteen, AudioChannel.Mono));
                Action<RecognitionResult, bool> heard = (speech, accepted) =>
                {
                    if (stopInput.IsCancellationRequested) return;
                    try
                    {
                        double peak = SpeechPeak(speech);
                        var result = accepted ? AcceptedResult(speech, commands, peak, Interlocked.Read(ref firstSample)) :
                            new MicrophoneResult(peak, null);
                        // A slow tablet needs the pending commands, not a backlog of microphone level hints.
                        if (result.Command == null && results.Count > 0) return;
                        if (!results.TryAdd(result) && result.Command != null)
                            throw new InvalidOperationException("Tablet control fell behind the microphone. Check the connection and start listening again.");
                    }
                    catch (Exception error)
                    {
                        Interlocked.CompareExchange(ref failure, error, null);
                        lifetime.Cancel();
                    }
                };
                engine.SpeechRecognized += (sender, e) => heard(e.Result, true);
                engine.SpeechRecognitionRejected += (sender, e) => heard(e.Result, false);
                engine.RecognizeCompleted += (sender, e) =>
                {
                    if (e.Error != null)
                    {
                        Interlocked.CompareExchange(ref failure, e.Error, null);
                        lifetime.Cancel();
                    }
                    stopInput.Cancel();
                    completed.Set();
                };
                token.ThrowIfCancellationRequested();
                engine.RecognizeAsync(RecognizeMode.Multiple);
                try
                {
                    var block = new byte[SpeechSampleRate * BlockMilliseconds / 1000 * 2];
                    open((pcm, rate, channels) =>
                    {
                        stopInput.Token.ThrowIfCancellationRequested();
                        input.Append(block, WriteSpeechBlock(pcm, rate, channels, block, 0));
                        return true;
                    }, timestamp =>
                    {
                        Interlocked.Exchange(ref firstSample, timestamp);
                        opened.TrySetResult(true);
                    }, stopInput.Token);
                }
                catch (OperationCanceledException) { if (!stopInput.IsCancellationRequested) throw; }
                catch
                {
                    // Device loss can make SAPI finalize a partial phrase on EOF. Disarm dispatch first.
                    lifetime.Cancel();
                    throw;
                }
                finally
                {
                    input.Complete();
                    if (stopInput.IsCancellationRequested && !completed.IsSet) engine.RecognizeAsyncCancel();
                    completed.Wait();
                }
                if (failure != null) throw new InvalidOperationException("Voice listening stopped. " + failure.Message, failure);
                token.ThrowIfCancellationRequested();
            }
        }

        private static double SpeechPeak(RecognitionResult speech)
        {
            if (speech == null || speech.Audio == null) return Double.NegativeInfinity;
            using (var pcm = new MemoryStream())
            {
                speech.Audio.WriteToAudioStream(pcm);
                byte[] bytes = pcm.GetBuffer();
                int peak = 0;
                for (int i = 0; i + 1 < pcm.Length; i += 2)
                    peak = Math.Max(peak, Math.Abs((int)ReadSample(bytes, i)));
                return peak == 0 ? Double.NegativeInfinity : 20 * Math.Log10(peak / 32768.0);
            }
        }

        // SAPI seeks while attaching the stream. Keep a bounded history and 64-bit sample offsets throughout a set.
        private sealed class TalkbackStream : Stream
        {
            private readonly object sync = new object();
            private readonly byte[] ring = new byte[SpeechSampleRate * 2 * 8];
            private long written, position;
            private bool complete;

            internal void Append(byte[] block, int count)
            {
                lock (sync)
                {
                    if (written + count - position > ring.Length)
                        throw new IOException("The Windows recognizer stopped reading microphone audio.");
                    int slot = (int)(written % ring.Length);
                    int first = Math.Min(count, ring.Length - slot);
                    Buffer.BlockCopy(block, 0, ring, slot, first);
                    Buffer.BlockCopy(block, first, ring, 0, count - first);
                    written += count;
                    Monitor.PulseAll(sync);
                }
            }
            internal void Complete() { lock (sync) { complete = true; Monitor.PulseAll(sync); } }
            public override int Read(byte[] buffer, int offset, int count)
            {
                lock (sync)
                {
                    count = Math.Min(count, ring.Length);
                    while (!complete && written - position < count) Monitor.Wait(sync);
                    count = (int)Math.Max(0, Math.Min(count, written - position));
                    int slot = (int)(position % ring.Length);
                    int first = Math.Min(count, ring.Length - slot);
                    Buffer.BlockCopy(ring, slot, buffer, offset, first);
                    Buffer.BlockCopy(ring, 0, buffer, offset + first, count - first);
                    position += count;
                    return count;
                }
            }
            public override long Seek(long offset, SeekOrigin origin)
            {
                lock (sync)
                {
                    long next = checked((origin == SeekOrigin.End ? Length : origin == SeekOrigin.Current ? position : 0L) + offset);
                    if (next < Math.Max(0, written - ring.Length) || next > Length)
                        throw new IOException("The recognizer requested unavailable microphone audio.");
                    return position = next;
                }
            }
            public override bool CanRead { get { return true; } }
            public override bool CanWrite { get { return false; } }
            public override bool CanSeek { get { return true; } }
            public override long Length { get { return Int64.MaxValue / TimeSpan.TicksPerSecond; } }
            public override long Position { get { lock (sync) return position; } set { Seek(value, SeekOrigin.Begin); } }
            public override void Flush() { }
            public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
            public override void SetLength(long value) { throw new NotSupportedException(); }
        }
    }
}

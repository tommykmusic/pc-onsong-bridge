using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace PcOnSongBridge
{
    // ABI of the standard NDI redistributable. The runtime is loaded only for NDI use.
    internal sealed class NdiAudioNative : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct Source { internal IntPtr Name, Address; }
        [StructLayout(LayoutKind.Sequential)]
        internal struct Performance { internal long Video, Audio, Metadata; }
        [StructLayout(LayoutKind.Sequential)]
        internal struct FindOptions
        {
            internal byte ShowLocal;
            internal IntPtr Groups, ExtraIps;
        }
        [StructLayout(LayoutKind.Sequential)]
        internal struct ReceiverOptions
        {
            internal Source Source;
            internal int ColorFormat, Bandwidth;
            internal byte AllowFields;
            internal IntPtr ReceiverName;
        }
        [StructLayout(LayoutKind.Sequential)]
        internal struct AudioFrame
        {
            internal int Rate, Channels, Samples;
            internal long Timecode;
            internal IntPtr Data;
            internal int Stride;
            internal IntPtr Metadata;
            internal long Timestamp;
        }
        [StructLayout(LayoutKind.Sequential)]
        internal struct VideoFrame
        {
            internal int Width, Height, Format, FpsN, FpsD;
            internal float Aspect;
            internal int FrameFormat;
            internal long Timecode;
            internal IntPtr Data;
            internal int Stride;
            internal IntPtr Metadata;
            internal long Timestamp;
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate byte Initialize();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void Shutdown();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate IntPtr FindCreate(ref FindOptions options);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate byte FindWait(IntPtr finder, uint ms);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate IntPtr FindSources(IntPtr finder, out uint count);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void Destroy(IntPtr instance);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate IntPtr RecvCreate(ref ReceiverOptions options);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int Capture(IntPtr receiver, ref VideoFrame video, ref AudioFrame audio, IntPtr metadata, uint ms);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void FreeVideo(IntPtr receiver, ref VideoFrame frame);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void FreeAudio(IntPtr receiver, ref AudioFrame frame);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int Connections(IntPtr receiver);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void GetPerformance(IntPtr receiver, out Performance total, out Performance dropped);
        private readonly FindCreate createFinder;
        private readonly FindWait waitFinder;
        private readonly FindSources currentSources;
        private readonly Destroy destroyFinder;
        internal readonly RecvCreate CreateReceiver;
        internal readonly Capture ReadFrame;
        internal readonly FreeVideo ReleaseVideo;
        internal readonly FreeAudio ReleaseAudio;
        internal readonly Connections ConnectionCount;
        internal readonly GetPerformance ReadPerformance;
        internal readonly Destroy DestroyReceiver;
        private readonly Shutdown shutdown;
        private readonly IntPtr module;
        private readonly string path;
        private static readonly object gate = new object();
        private static NdiAudioNative shared;
        private int users;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr LoadLibraryEx(string file, IntPtr reserved, uint flags);
        [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Ansi)]
        private static extern IntPtr GetProcAddress(IntPtr library, string name);
        [DllImport("kernel32.dll")] private static extern bool FreeLibrary(IntPtr library);

        private NdiAudioNative(string file)
        {
            path = file;
            using (var verified = WindowsToolTrust.OpenTrustedNdiLibrary(file))
            {
                if (verified == null) throw new InvalidOperationException("Install the official signed NDI 6 Runtime to receive Wi-Fi audio.");
                // The core DLL imports Windows system libraries. Ignore unsigned siblings in custom folders.
                module = LoadLibraryEx(file, IntPtr.Zero, 0x800); // LOAD_LIBRARY_SEARCH_SYSTEM32
            }
            if (module == IntPtr.Zero)
                throw new InvalidOperationException("Could not open the NDI runtime. " + new Win32Exception(Marshal.GetLastWin32Error()).Message);
            try
            {
                shutdown = Export<Shutdown>("destroy");
                createFinder = Export<FindCreate>("find_create_v2");
                waitFinder = Export<FindWait>("find_wait_for_sources");
                currentSources = Export<FindSources>("find_get_current_sources");
                destroyFinder = Export<Destroy>("find_destroy");
                CreateReceiver = Export<RecvCreate>("recv_create_v3");
                ReadFrame = Export<Capture>("recv_capture_v2");
                ReleaseVideo = Export<FreeVideo>("recv_free_video_v2");
                ReleaseAudio = Export<FreeAudio>("recv_free_audio_v2");
                ConnectionCount = Export<Connections>("recv_get_no_connections");
                ReadPerformance = Export<GetPerformance>("recv_get_performance");
                DestroyReceiver = Export<Destroy>("recv_destroy");
                if (Export<Initialize>("initialize")() == 0) throw new InvalidOperationException("The NDI runtime could not initialize on this PC.");
            }
            catch { FreeLibrary(module); throw; }
        }

        private T Export<T>(string name) where T : class
        {
            IntPtr ptr = GetProcAddress(module, "NDIlib_" + name);
            if (ptr == IntPtr.Zero) throw new InvalidOperationException("Install the current NDI runtime. A required receiver function is missing.");
            return Marshal.GetDelegateForFunctionPointer(ptr, typeof(T)) as T;
        }

        internal static NdiAudioNative Acquire(string file)
        {
            string expected = Environment.Is64BitProcess ? "Processing.NDI.Lib.x64.dll" : "Processing.NDI.Lib.x86.dll";
            if (!WindowsToolTrust.IsLocalDrivePath(file) ||
                !String.Equals(Path.GetFileName(file), expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Install the NDI runtime for this Windows app and try again.");
            file = Path.GetFullPath(file);
            lock (gate)
            {
                if (shared == null) shared = new NdiAudioNative(file);
                if (!String.Equals(shared.path, file, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Stop the current NDI connection before switching runtimes.");
                shared.users++;
                return shared;
            }
        }

        public void Dispose()
        {
            lock (gate)
            {
                if (--users != 0) return;
                shutdown();
                FreeLibrary(module);
                shared = null;
            }
        }

        internal static List<NdiSource> Discover(string file, CancellationToken token, IPAddress tablet = null)
        {
            token.ThrowIfCancellationRequested();
            using (var api = Acquire(file))
            {
                var options = new FindOptions { ShowLocal = 1, ExtraIps = NewUtf8(tablet == null ? null : tablet.ToString()) };
                IntPtr finder = IntPtr.Zero;
                try
                {
                    finder = api.createFinder(ref options);
                    if (finder == IntPtr.Zero) throw new InvalidOperationException("NDI source discovery could not start.");
                    var time = Stopwatch.StartNew();
                    var found = new List<NdiSource>();
                    do
                    {
                        token.ThrowIfCancellationRequested();
                        api.waitFinder(finder, 100);
                        token.ThrowIfCancellationRequested();
                        uint count;
                        IntPtr sources = api.currentSources(finder, out count);
                        found.Clear();
                        int size = Marshal.SizeOf(typeof(Source));
                        for (int i = 0; sources != IntPtr.Zero && i < Math.Min(4096U, count); i++)
                        {
                            var source = (Source)Marshal.PtrToStructure(IntPtr.Add(sources, i * size), typeof(Source));
                            string name = ReadUtf8(source.Name), address = ReadUtf8(source.Address);
                            if ((name.Length != 0 || address.Length != 0) && (tablet == null || AddressMatches(address, tablet)))
                                found.Add(new NdiSource(name, address));
                        }
                        if (tablet != null && found.Count > 0) break;
                    } while (time.ElapsedMilliseconds < (tablet == null ? 2400 : 4500));
                    return found;
                }
                finally
                {
                    if (finder != IntPtr.Zero) api.destroyFinder(finder);
                    Marshal.FreeHGlobal(options.ExtraIps);
                }
            }
        }

        // The finder can include multicast results along with the explicitly requested machine.
        // Only return a source whose endpoint is attributable to this address.
        internal static bool AddressMatches(string address, IPAddress requested)
        {
            if (String.IsNullOrEmpty(address)) return false;
            Uri uri;
            IPAddress actual;
            string url = address.Contains("://") ? address : "ndi://" + address;
            return Uri.TryCreate(url, UriKind.Absolute, out uri) && uri.UserInfo.Length == 0 &&
                IPAddress.TryParse(uri.DnsSafeHost, out actual) && actual.Equals(requested);
        }

        private static string ReadUtf8(IntPtr value)
        {
            if (value == IntPtr.Zero) return "";
            int n = 0;
            while (n < 4096 && Marshal.ReadByte(value, n) != 0) n++;
            if (n == 4096) return "";
            var bytes = new byte[n];
            Marshal.Copy(value, bytes, 0, n);
            return Encoding.UTF8.GetString(bytes);
        }

        internal static IntPtr NewUtf8(string value)
        {
            if (String.IsNullOrEmpty(value)) return IntPtr.Zero;
            if (value.IndexOf('\0') >= 0 || value.Length > 4096) throw new ArgumentException("Invalid NDI source.");
            byte[] bytes = Encoding.UTF8.GetBytes(value + "\0");
            IntPtr data = Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, data, bytes.Length);
            return data;
        }
    }

    internal sealed class NdiAudioInput : INdiCapture
    {
        private readonly NdiAudioNative api;
        private IntPtr recv, name, address, receiverName;
        private bool disposed;
        internal NdiAudioInput(string runtime, NdiSource source)
        {
            api = NdiAudioNative.Acquire(runtime);
            try
            {
                name = NdiAudioNative.NewUtf8(source.Name);
                address = NdiAudioNative.NewUtf8(source.Address);
                receiverName = NdiAudioNative.NewUtf8("PC OnSong Bridge");
                var options = new NdiAudioNative.ReceiverOptions {
                    Source = new NdiAudioNative.Source { Name = name, Address = address },
                    ColorFormat = 100, Bandwidth = 100, AllowFields = 1, ReceiverName = receiverName
                };
                recv = api.CreateReceiver(ref options);
                if (recv == IntPtr.Zero) throw new InvalidOperationException("NDI could not create the cue receiver.");
            }
            catch { Dispose(); throw; }
        }

        public bool Connected { get { return api.ConnectionCount(recv) > 0; } }
        public long DroppedPackets
        {
            get
            {
                NdiAudioNative.Performance total, dropped;
                api.ReadPerformance(recv, out total, out dropped);
                return dropped.Audio;
            }
        }

        public int Read(out NdiAudioBlock block)
        {
            block = null;
            var video = new NdiAudioNative.VideoFrame();
            var frame = new NdiAudioNative.AudioFrame();
            int kind = api.ReadFrame(recv, ref video, ref frame, IntPtr.Zero, 100);
            if (kind == 1) api.ReleaseVideo(recv, ref video);
            else if (kind == 2)
            {
                try
                {
                    if (frame.Data == IntPtr.Zero || frame.Samples <= 0) return kind;
                    if (frame.Rate < 8000 || frame.Rate > 192000 || frame.Channels < 1 || frame.Channels > 256 ||
                        frame.Samples > frame.Rate || (frame.Channels > 1 && frame.Stride < frame.Samples * sizeof(float)))
                        throw new InvalidOperationException("The NDI source sent an unsupported audio format.");
                    var left = new float[frame.Samples];
                    Marshal.Copy(frame.Data, left, 0, left.Length);
                    float[] right = left;
                    if (frame.Channels > 1)
                    {
                        right = new float[frame.Samples];
                        Marshal.Copy(IntPtr.Add(frame.Data, frame.Stride), right, 0, right.Length);
                    }
                    block = new NdiAudioBlock(frame.Rate, frame.Channels, left, right);
                }
                finally { api.ReleaseAudio(recv, ref frame); }
            }
            return kind;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (recv != IntPtr.Zero) api.DestroyReceiver(recv);
            Marshal.FreeHGlobal(name);
            Marshal.FreeHGlobal(address);
            Marshal.FreeHGlobal(receiverName);
            api.Dispose();
        }
    }
}

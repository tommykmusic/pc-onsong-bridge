using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace PcOnSongBridge
{
    internal sealed class WirelessService
    {
        public readonly string Name;
        public readonly string Endpoint;
        public readonly bool Pairing;

        public WirelessService(string name, string endpoint, bool pairing)
        {
            Name = name;
            Endpoint = endpoint;
            Pairing = pairing;
        }

        public override string ToString() { return Endpoint + "  (" + Name + ")"; }
    }

    internal sealed class AndroidBridge : IDisposable
    {
        private const string OnSongPackage = "com.onsongapp.app";
        private const string SongLink = "onsong://open/songs?index=";
        private const int CommandTimeoutMs = 20000;
        private static readonly Regex SerialPattern = new Regex(
            @"\A[A-Za-z0-9\[][A-Za-z0-9_.:\[\]-]{0,127}\z", RegexOptions.CultureInvariant);
        private static readonly Regex EndpointPattern = new Regex(
            @"\A(?<ip>[0-9.]+|\[[0-9A-Fa-f:.]+\]):(?<port>[0-9]{1,5})\z", RegexOptions.CultureInvariant);
        private static readonly Regex OnSongComponent = new Regex(
            @"\bcom\.onsongapp\.app/[A-Za-z0-9_.$]+", RegexOptions.CultureInvariant);
        private static readonly Regex FocusedComponent = new Regex(
            @"(?m)^\s*mCurrentFocus=Window\{[^\r\n}]*?\bu\d+\s+([^\s/}]+)/[^\s}]+", RegexOptions.CultureInvariant);

        private readonly object sessionLock = new object();
        private readonly HashSet<string> chordDevices = new HashSet<string>(StringComparer.Ordinal);
        private readonly string scrcpyPath;
        private readonly string adbPath;
        private Process session;
        private SafeFileHandle sessionJob;
        private CancellationTokenSource startup;
        private bool disposed;

        public event Action<string> Log;
        public event Action Stopped;

        public AndroidBridge(string scrcpyPath)
        {
            if (String.IsNullOrWhiteSpace(scrcpyPath))
                throw new ArgumentException("Select scrcpy.exe from the extracted official Windows package.");

            this.scrcpyPath = Path.GetFullPath(scrcpyPath);
            if (!String.Equals(Path.GetFileName(this.scrcpyPath), "scrcpy.exe", StringComparison.OrdinalIgnoreCase)
                || !File.Exists(this.scrcpyPath))
                throw new FileNotFoundException("Select an existing scrcpy.exe from the extracted official Windows package.", this.scrcpyPath);

            adbPath = Path.Combine(Path.GetDirectoryName(this.scrcpyPath), "adb.exe");
            if (!File.Exists(adbPath))
                throw new FileNotFoundException("adb.exe must be in the same folder as scrcpy.exe. Extract the entire Windows package.", adbPath);
        }

        public bool IsRunning
        {
            get
            {
                lock (sessionLock)
                    return session != null;
            }
        }

        public int? SessionProcessId
        {
            get
            {
                lock (sessionLock)
                    return session == null ? (int?)null : session.Id;
            }
        }

        public static string FindScrcpy()
        {
            string appFolder = AppDomain.CurrentDomain.BaseDirectory;
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string[] commonFolders =
            {
                Path.Combine(appFolder, "scrcpy"),
                appFolder,
                Path.GetDirectoryName(ScrcpyPackage.InstalledExecutable),
                Path.Combine(localAppData, "scrcpy"),
                Path.Combine(programFiles, "scrcpy")
            };

            string[] wingetFolders =
            {
                Path.Combine(localAppData, "Microsoft", "WinGet", "Packages"),
                Path.Combine(programFiles, "WinGet", "Packages")
            };
            foreach (string wingetFolder in wingetFolders)
            {
                if (!WindowsToolTrust.IsLocalDrivePath(wingetFolder) || !Directory.Exists(wingetFolder))
                    continue;
                try
                {
                    string[] packages = Directory.GetDirectories(wingetFolder, "Genymobile.scrcpy_*");
                    foreach (string package in packages)
                    {
                        commonFolders = commonFolders.Concat(new[] { package })
                            .Concat(Directory.GetDirectories(package, "scrcpy-win*")).ToArray();
                    }
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }

            string searchPath = Environment.GetEnvironmentVariable("PATH") ?? String.Empty;
            foreach (string folder in commonFolders.Concat(searchPath.Split(Path.PathSeparator)))
            {
                string directory = folder.Trim().Trim('"');
                try
                {
                    if (!WindowsToolTrust.IsLocalDrivePath(directory))
                        continue;
                    string executable = Path.Combine(directory, "scrcpy.exe");
                    if (File.Exists(executable) && File.Exists(Path.Combine(directory, "adb.exe")))
                        return executable;
                }
                catch (ArgumentException) { }
            }
            return String.Empty;
        }

        public Task<string> ListDevicesAsync()
        {
            return RunAdbAsync("devices -l");
        }

        public async Task<List<WirelessService>> DiscoverWirelessAsync(CancellationToken token)
        {
            // ADB already watches mDNS. Read its snapshot without making a pairing or connection request.
            string output = await RunAdbAsync("mdns services", executionToken: token).ConfigureAwait(false);
            var services = new List<WirelessService>();
            foreach (string line in output.Split('\n'))
            {
                string[] fields = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length != 3) continue;
                string type = fields[1].TrimEnd('.');
                bool pairing = type == "_adb-tls-pairing._tcp";
                if (!pairing && type != "_adb-tls-connect._tcp") continue;
                string endpoint = NormalizeEndpoint(fields[2]);
                if (endpoint != null && !services.Any(service => service.Pairing == pairing && service.Endpoint == endpoint))
                    services.Add(new WirelessService(fields[0], endpoint, pairing));
            }
            return services.OrderBy(service => service.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        public async Task<string> ConnectAsync(string endpoint)
        {
            string address = ValidateEndpoint(endpoint);
            string output = await RunAdbAsync("connect " + address).ConfigureAwait(false);
            if (!Regex.IsMatch(output, @"(?im)^\s*(?:already )?connected to\s"))
                throw new InvalidOperationException("ADB could not connect. Use the address on the main Wireless debugging screen.");
            return "ADB connected to " + address + ".";
        }

        public async Task<string> PairAsync(string endpoint, string code)
        {
            string address = ValidateEndpoint(endpoint);
            if (!Regex.IsMatch(code ?? String.Empty, @"\A[0-9]{6}\z"))
                throw new ArgumentException("Enter the six-digit pairing code shown on the tablet.");

            string output = await RunAdbAsync("pair " + address, code).ConfigureAwait(false);
            if (output.IndexOf("Successfully paired", StringComparison.OrdinalIgnoreCase) < 0)
                throw new InvalidOperationException("ADB could not pair. Refresh the code and address on the tablet and try again.");
            return "Tablet paired. Check the connected tablets list.";
        }

        public async Task StartAsync(string serial, int audioBuffer)
        {
            string device = ValidateSerial(serial);
            if (audioBuffer < 20 || audioBuffer > 500)
                throw new ArgumentOutOfRangeException("audioBuffer", "Choose a buffer between 20 and 500 ms.");

            CancellationTokenSource attempt;
            lock (sessionLock)
            {
                ThrowIfDisposed();
                if (session != null || startup != null)
                    throw new InvalidOperationException("An Android audio session is already running or starting.");
                attempt = new CancellationTokenSource();
                startup = attempt;
            }

            try
            {
                await ConfirmDeviceReadyAsync(device, attempt.Token).ConfigureAwait(false);
                string sdk = await RunAdbAsync("-s " + device + " shell getprop ro.build.version.sdk",
                    executionToken: attempt.Token).ConfigureAwait(false);
                int api;
                if (!Int32.TryParse(sdk.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out api) || api < 30)
                    throw new InvalidOperationException("Audio forwarding requires Android 11 or later. Unlock Android 11 devices before starting.");

                string args = "--serial=" + device + " --no-window --no-control --require-audio --audio-source=output --audio-buffer="
                    + audioBuffer.ToString(CultureInfo.InvariantCulture);
                lock (sessionLock)
                {
                    attempt.Token.ThrowIfCancellationRequested();
                    ThrowIfDisposed();
                    Launch(args);
                }
            }
            finally
            {
                lock (sessionLock)
                {
                    if (ReferenceEquals(startup, attempt)) startup = null;
                    attempt.Dispose();
                }
            }
        }

        public async Task<bool> HasSongLinksAsync(string serial)
        {
            string device = ValidateSerial(serial);
            await ConfirmDeviceReadyAsync(device).ConfigureAwait(false);
            foreach (string action in new[] { "next", "previous" })
            {
                string result = await RunAdbAsync("-s " + device
                    + " shell cmd package resolve-activity --brief -a android.intent.action.VIEW -d "
                    + SongLink + action + " -p " + OnSongPackage).ConfigureAwait(false);
                if (String.Equals(result.Trim(), "No activity found", StringComparison.OrdinalIgnoreCase))
                    return false;
                if (!OnSongComponent.IsMatch(result))
                    throw new InvalidOperationException("Android could not verify the OnSong " + action + " song link. " + result);
            }
            return true;
        }

        public async Task<string> SendActionAsync(string serial, string action,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            string device = ValidateSerial(serial);
            ValidateAction(action);
            string result = await RunAdbAsync("-s " + device
                + " shell am start -W -a android.intent.action.VIEW -d "
                + SongLink + action + " -p " + OnSongPackage, dispatchToken: cancellationToken).ConfigureAwait(false);
            bool deliveredToTop = result.IndexOf("delivered to currently running top-most instance", StringComparison.OrdinalIgnoreCase) >= 0;
            bool warning = Regex.IsMatch(result, @"(?im)^Warning:");
            bool deliveryConfirmed = deliveredToTop || (OnSongComponent.IsMatch(result) && !warning);
            if (!Regex.IsMatch(result, @"(?im)^Status:\s+ok\s*$") || !deliveryConfirmed
                || Regex.IsMatch(result, @"(?im)^\s*(error|exception)\b"))
                throw new InvalidOperationException("Android did not confirm delivery of the OnSong link. " + result);
            return "OnSong link delivered: " + action + " song.";
        }

        public async Task<string> CheckKeyboardAsync(string serial)
        {
            string device = ValidateSerial(serial);
            await ConfirmDeviceReadyAsync(device).ConfigureAwait(false);
            await ConfirmOnSongFocusAsync(device).ConfigureAwait(false);
            return "OnSong has keyboard focus. Verify both arrow assignments in a test set.";
        }

        public async Task<string> CheckFastSongShortcutsAsync(string serial)
        {
            string device = ValidateSerial(serial);
            await ConfirmDeviceReadyAsync(device).ConfigureAwait(false);
            await EnsureChordSupportAsync(device, CancellationToken.None).ConfigureAwait(false);
            await ConfirmOnSongFocusAsync(device, compact: true).ConfigureAwait(false);
            return "OnSong has keyboard focus and Android supports key combinations. Test both song shortcuts in a practice set.";
        }

        public async Task<string> CheckSwipeAsync(string serial, CancellationToken cancellationToken = default(CancellationToken))
        {
            string device = ValidateSerial(serial);
            await ConfirmDeviceReadyAsync(device, cancellationToken).ConfigureAwait(false);
            await ReadSongViewerAsync(device, cancellationToken).ConfigureAwait(false);
            return "OnSong has an unobstructed song viewer. Test both directions in a practice set.";
        }

        public Task<string> SendSwipeActionAsync(string serial, string action,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            return SendObservedSwipeAsync(serial, action, null, cancellationToken);
        }

        public async Task<string> SendObservedSwipeAsync(string serial, string action, Action<string> captureBefore,
            CancellationToken cancellationToken)
        {
            string device = ValidateSerial(serial);
            bool vertical = action == "scroll-up" || action == "scroll-down";
            if (!vertical) ValidateAction(action);
            SongViewer viewer = await ReadSongViewerAsync(device, cancellationToken, vertical).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (captureBefore != null) captureBefore(viewer.Identity);
            string result = await RunAdbAsync("-s " + device + " shell input swipe " + viewer.Gesture(action),
                dispatchToken: cancellationToken).ConfigureAwait(false);
            if (Regex.IsMatch(result, @"(?im)^\s*(error|exception|usage|failed)\b"))
                throw new InvalidOperationException("Android did not accept the song-viewer swipe.");
            return vertical ? "OnSong viewer swipe sent to " + action.Replace('-', ' ') + "."
                : "OnSong viewer swipe sent for " + action + " song.";
        }

        public async Task<string> ReadChartIdentityAsync(string serial, CancellationToken cancellationToken)
        {
            SongViewer viewer = await ReadSongViewerAsync(ValidateSerial(serial), cancellationToken).ConfigureAwait(false);
            return viewer.Identity;
        }

        public async Task<bool> WaitForChartChangeAsync(string serial, string before, string expectedAction,
            CancellationToken cancellationToken)
        {
            string device = ValidateSerial(serial);
            ValidateAction(expectedAction);
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                // A UI dump can take several seconds. Its completion time bounds the observed change.
                timeout.CancelAfter(TimeSpan.FromSeconds(9));
                try
                {
                    while (true)
                    {
                        try
                        {
                            SongViewer viewer = await ReadSongViewerAsync(device, timeout.Token).ConfigureAwait(false);
                            if (viewer.IsAdjacentTo(before, expectedAction)) return true;
                            // A different, unexpected change is not proof that this command worked.
                            if (!String.Equals(viewer.Identity, before, StringComparison.Ordinal)) return false;
                        }
                        catch (SongViewer.LayoutUnavailableException)
                        {
                            // During a page turn both sheets may briefly be visible. Recheck without sending input.
                        }
                        await Task.Delay(200, timeout.Token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    return false;
                }
            }
        }

        private async Task<SongViewer> ReadSongViewerAsync(string serial, CancellationToken cancellationToken, bool vertical = false)
        {
            // Check focus before reading accessibility data and again immediately before using its geometry.
            string initialWindow = await ConfirmOnSongFocusAsync(serial, cancellationToken).ConfigureAwait(false);
            string dump = await ReadOnSongLayoutAsync(serial, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            SongViewer viewer = SongViewer.Read(dump, vertical);
            var initialSize = viewer.CheckCurrentDisplay(initialWindow);
            string windows = await ConfirmOnSongFocusAsync(serial, cancellationToken).ConfigureAwait(false);
            if (viewer.CheckCurrentDisplay(windows) != initialSize || !String.Equals(
                FocusedComponent.Match(initialWindow).Value.Trim(), FocusedComponent.Match(windows).Value.Trim(), StringComparison.Ordinal))
                throw new InvalidOperationException("The active OnSong window changed. Return to the song and try again.");
            return viewer;
        }

        private async Task<string> ReadOnSongLayoutAsync(string serial, CancellationToken cancellationToken)
        {
            try
            {
                string dump = await RunAdbAsync("-s " + serial + " exec-out uiautomator dump /dev/tty",
                    executionToken: cancellationToken).ConfigureAwait(false);
                if (dump.IndexOf("<hierarchy", StringComparison.Ordinal) < 0)
                    dump = await RunAdbAsync("-s " + serial + " exec-out uiautomator dump /dev/stdout",
                        executionToken: cancellationToken).ConfigureAwait(false);
                return dump;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception)
            {
                // Failed ADB commands can echo the hierarchy, including lyrics. Keep it out of logs.
                throw new InvalidOperationException("Could not read the OnSong screen. Check the tablet connection and try again.");
            }
        }

        public async Task<string> SendKeyboardActionAsync(string serial, string action,
            CancellationToken cancellationToken = default(CancellationToken))
        {
            string device = ValidateSerial(serial);
            ValidateAction(action);
            cancellationToken.ThrowIfCancellationRequested();
            await ConfirmOnSongFocusAsync(device, cancellationToken).ConfigureAwait(false);
            string key = action == "next" ? "KEYCODE_DPAD_RIGHT" : "KEYCODE_DPAD_LEFT";
            await RunAdbAsync("-s " + device + " shell input keyevent " + key,
                dispatchToken: cancellationToken).ConfigureAwait(false);
            return "OnSong arrow key sent: " + action + " song.";
        }

        public Task<string> SendShortcutAsync(string serial, VoiceShortcut shortcut, CancellationToken cancellationToken)
        {
            return SendShortcutInputAsync(serial, shortcut, false, cancellationToken);
        }

        public Task<string> TeachShortcutAsync(string serial, VoiceShortcut shortcut, CancellationToken cancellationToken)
        {
            return SendShortcutInputAsync(serial, shortcut, true, cancellationToken);
        }

        private async Task<string> SendShortcutInputAsync(string serial, VoiceShortcut shortcut, bool teach, CancellationToken cancellationToken)
        {
            string device = ValidateSerial(serial);
            if (shortcut == null) throw new ArgumentNullException("shortcut");
            string[] keys = shortcut.AndroidKeys;
            if (keys.Length < 1 || keys.Length > 4 || keys.Any(key => !Regex.IsMatch(key, @"\AKEYCODE_[A-Z0-9_]+\z")))
                throw new ArgumentException("Select a valid OnSong keyboard shortcut.");
            string command = keys.Length > 1 ? "keycombination" : "keyevent";
            if (teach)
            {
                string help = await RunAdbAsync("-s " + device + " shell input help", executionToken: cancellationToken).ConfigureAwait(false);
                string option = keys.Length > 1 ? "-t" : "--duration";
                if (!Regex.IsMatch(help, @"(?m)^[ \t]*" + command + @"[ \t][^\r\n]*" + option + @"\b"))
                    throw new InvalidOperationException("This Android input tool cannot hold that shortcut. Hold the keys on a keyboard connected to the tablet.");
                command += " " + option + " 1400";
            }
            else if (keys.Length > 1) await EnsureChordSupportAsync(device, cancellationToken).ConfigureAwait(false);
            string input = "input " + command + " " + String.Join(" ", keys);
            string result;
            if (teach)
            {
                string initialWindow = await ConfirmOnSongFocusAsync(device, cancellationToken).ConfigureAwait(false);
                string layout = await ReadOnSongLayoutAsync(device, cancellationToken).ConfigureAwait(false);
                if (!SongViewer.IsShortcutEditor(layout))
                    throw new InvalidOperationException("On the tablet, open OnSong's Foot Pedals button, select Shortcuts and scroll to System Defined. Then try again.");
                string finalWindow = await ConfirmOnSongFocusAsync(device, cancellationToken).ConfigureAwait(false);
                if (FocusedComponent.Match(initialWindow).Value.Trim() != FocusedComponent.Match(finalWindow).Value.Trim())
                    throw new InvalidOperationException("The OnSong window changed. Return to Shortcuts and try again.");
                result = await RunAdbAsync("-s " + device + " shell " + input, dispatchToken: cancellationToken).ConfigureAwait(false);
            }
            else
            {
                // Send the validated keys only after checking this request's foreground reply.
                result = await RunAdbAsync("-s " + device + " shell -T " + FocusQuery(true) + "; exec sh",
                    dispatchToken: cancellationToken, exchange: p => SendAfterFocusAsync(p, input, cancellationToken)).ConfigureAwait(false);
            }
            if (Regex.IsMatch(result, @"(?im)^\s*(error|exception|usage|failed|unknown)\b"))
                throw new InvalidOperationException("Android did not accept the shortcut.");
            return teach ? "Held " + shortcut.Label + ". On the tablet, choose the desired action and check the assignment under User Defined."
                         : "Sent " + shortcut.Label + " to OnSong.";
        }

        private async Task EnsureChordSupportAsync(string device, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (sessionLock) if (chordDevices.Contains(device)) return;
            string help = await RunAdbAsync("-s " + device + " shell input help", executionToken: cancellationToken).ConfigureAwait(false);
            if (!Regex.IsMatch(help, @"(?m)^[ \t]*keycombination[ \t]"))
                throw new InvalidOperationException("This Android input tool does not advertise key combinations. Assign a single key or use song swipes.");
            lock (sessionLock) chordDevices.Add(device);
        }

        public void Stop()
        {
            lock (sessionLock)
            {
                chordDevices.Clear();
                if (startup != null) startup.Cancel();
                startup = null;
                if (sessionJob != null)
                {
                    sessionJob.Dispose();
                    sessionJob = null;
                }
                else KillIfRunning(session);
            }
        }

        public void Dispose()
        {
            lock (sessionLock)
            {
                disposed = true;
                Stop();
            }
        }

        private void Launch(string arguments)
        {
            Process process = NewProcess(scrcpyPath, arguments);
            process.StartInfo.EnvironmentVariables["ADB"] = adbPath;
            process.OutputDataReceived += (sender, e) => EmitLine(e.Data);
            process.ErrorDataReceived += (sender, e) => EmitLine(e.Data);
            try
            {
                process.Start();
                // Discovery and the Android version check already started the shared ADB server
                // outside this job. Only this scrcpy process and its subsequent children are assigned.
                sessionJob = AttachSessionJob(process);
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                session = process;
                Task.Run(() => MonitorSession(process));
            }
            catch
            {
                if (sessionJob != null) sessionJob.Dispose();
                sessionJob = null;
                KillIfRunning(process);
                process.Dispose();
                throw;
            }
        }

        private void MonitorSession(Process process)
        {
            // The parameterless overload also waits for EOF, which a surviving adb shell can hold.
            while (!process.WaitForExit(60000)) { }
            int exitCode = process.ExitCode;
            lock (sessionLock)
            {
                if (sessionJob != null) sessionJob.Dispose();
                sessionJob = null;
                session = null;
                process.Dispose();
                if (disposed)
                    return;
            }

            EmitLine("scrcpy exited (code " + exitCode.ToString(CultureInfo.InvariantCulture) + ").");
            Action stopped = Stopped;
            if (stopped != null)
                stopped();
        }

        private async Task ConfirmDeviceReadyAsync(string serial, CancellationToken cancellationToken = default(CancellationToken))
        {
            string devices = await RunAdbAsync("devices -l", executionToken: cancellationToken).ConfigureAwait(false);
            foreach (string line in devices.Split('\n'))
            {
                string[] fields = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
                if (fields.Length < 2 || !String.Equals(fields[0], serial, StringComparison.Ordinal))
                    continue;
                if (fields[1] == "device")
                    return;
                throw new InvalidOperationException("The selected tablet is " + fields[1] + ". Unlock it, authorize debugging and refresh devices.");
            }
            throw new InvalidOperationException("The selected tablet is missing. Refresh devices and select its current serial.");
        }

        private async Task<string> ConfirmOnSongFocusAsync(string serial, CancellationToken cancellationToken = default(CancellationToken), bool compact = false)
        {
            string command = "-s " + serial + " shell dumpsys window";
            string result = await RunAdbAsync(command + (compact ? " displays" : ""), executionToken: cancellationToken).ConfigureAwait(false);
            // Some Android builds expose focus only in the full dump. A reported null or foreign focus still fails closed.
            if (compact && result.IndexOf("mCurrentFocus=", StringComparison.Ordinal) < 0)
                result = await RunAdbAsync(command, executionToken: cancellationToken).ConfigureAwait(false);
            RequireOnSongFocus(result);
            return result;
        }

        private static void RequireOnSongFocus(string result)
        {
            // A secondary display can have null focus. An overlay (including a window with
            // no package/activity component) or two focused windows makes input ambiguous.
            var active = result.Split('\n').Select(line => line.Trim())
                .Where(line => line.StartsWith("mCurrentFocus=", StringComparison.Ordinal) && line != "mCurrentFocus=null").ToArray();
            Match window = active.Length == 1 ? FocusedComponent.Match(active[0]) : Match.Empty;
            if (!window.Success || !String.Equals(window.Groups[1].Value, OnSongPackage, StringComparison.Ordinal))
                throw new InvalidOperationException("Open OnSong on the unlocked tablet and dismiss any overlay before using song navigation.");
        }

        private const string FocusEnd = "BRIDGE_FOCUS_END";
        private const string DumpFailed = "BRIDGE_DUMP_FAILED";
        private const string ShortcutSent = "BRIDGE_SHORTCUT_SENT";

        private static string FocusQuery(bool compact)
        {
            return "(dumpsys window" + (compact ? " displays" : "") + " || echo " + DumpFailed +
                ") | grep -E '^[[:space:]]*mCurrentFocus=|^" + DumpFailed + "$'; echo " + FocusEnd;
        }

        private static async Task<string> SendAfterFocusAsync(Process process, string input, CancellationToken cancel)
        {
            using (var writer = process.StandardInput)
            {
                writer.NewLine = "\n";
                using (cancel.Register(() => KillIfRunning(process)))
                {
                    string focus = await ReadFocusReplyAsync(process.StandardOutput, cancel).ConfigureAwait(false);
                    if (focus.IndexOf("mCurrentFocus=", StringComparison.Ordinal) < 0)
                    {
                        cancel.ThrowIfCancellationRequested();
                        writer.WriteLine(FocusQuery(false));
                        focus = await ReadFocusReplyAsync(process.StandardOutput, cancel).ConfigureAwait(false);
                    }
                    RequireOnSongFocus(focus);
                }
                cancel.ThrowIfCancellationRequested();
                // Once submitted, wait for the actual receipt even if Stop is pressed.
                writer.WriteLine(input + " && echo " + ShortcutSent);
            }
            string reply = await ReadAdbOutputAsync(process.StandardOutput, 64 * 1024).ConfigureAwait(false);
            if (reply != null) reply = reply.TrimEnd();
            if (reply == null || !(reply == ShortcutSent || reply.EndsWith("\n" + ShortcutSent, StringComparison.Ordinal)))
                throw new InvalidOperationException("Android did not confirm the shortcut. Check the chart before trying again.");
            return reply.Substring(0, reply.Length - ShortcutSent.Length);
        }

        private static async Task<string> ReadFocusReplyAsync(StreamReader reader, CancellationToken cancel)
        {
            var buffer = new char[1024];
            var reply = new StringBuilder();
            int count;
            while ((count = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) != 0)
            {
                cancel.ThrowIfCancellationRequested();
                reply.Append(buffer, 0, count);
                if (reply.Length > 16384) break;
                if (reply[reply.Length - 1] != '\n') continue;
                string text = reply.ToString().Replace("\r\n", "\n");
                if (text == FocusEnd + "\n" || text.EndsWith("\n" + FocusEnd + "\n", StringComparison.Ordinal))
                {
                    if (text.IndexOf(DumpFailed, StringComparison.Ordinal) >= 0) break;
                    return text;
                }
            }
            cancel.ThrowIfCancellationRequested();
            throw new InvalidOperationException("The tablet did not return a complete foreground check. Check its connection.");
        }

        private Task<string> RunAdbAsync(string arguments, string stdin = null,
            CancellationToken dispatchToken = default(CancellationToken), CancellationToken executionToken = default(CancellationToken),
            Func<Process, Task<string>> exchange = null)
        {
            lock (sessionLock)
                ThrowIfDisposed();

            return Task.Run(async () =>
            {
                using (Process process = NewProcess(adbPath, arguments))
                {
                    process.StartInfo.RedirectStandardInput = stdin != null || exchange != null;
                    dispatchToken.ThrowIfCancellationRequested();
                    executionToken.ThrowIfCancellationRequested();
                    process.Start();
                    using (executionToken.Register(() => KillIfRunning(process)))
                    {
                        Task<string> stdout = exchange == null ? ReadAdbOutputAsync(process.StandardOutput, 8 * 1024 * 1024) : exchange(process);
                        Task<string> stderr = ReadAdbOutputAsync(process.StandardError, 64 * 1024);
                        if (stdin != null)
                        {
                            try
                            {
                                process.StandardInput.WriteLine(stdin);
                                process.StandardInput.Close();
                            }
                            catch (IOException) { }
                        }
                        Stopwatch elapsed = Stopwatch.StartNew();
                        var drains = Task.WhenAll(stdout, stderr);
                        if (!process.WaitForExit(CommandTimeoutMs) || await Task.WhenAny(drains,
                            Task.Delay(Math.Max(0, CommandTimeoutMs - (int)elapsed.ElapsedMilliseconds), executionToken)).ConfigureAwait(false) != drains)
                        {
                            KillIfRunning(process);
                            throw new TimeoutException("ADB did not respond in 20 seconds. Check the tablet connection and retry.");
                        }
                        executionToken.ThrowIfCancellationRequested();
                        await drains.ConfigureAwait(false);
                        if (process.ExitCode != 0)
                        {
                            if (stdin != null)
                                throw new InvalidOperationException("ADB could not pair. Refresh the code and address on the tablet and try again.");
                            // A partial system or UI dump can include other apps' window titles and chart text.
                            throw new InvalidOperationException("ADB could not complete the request (exit code " +
                                process.ExitCode.ToString(CultureInfo.InvariantCulture) + "). Check the selected tablet and its debugging connection.");
                        }
                        if (stdout.Result == null || stderr.Result == null)
                            throw new InvalidOperationException("The tablet returned too much diagnostic output. Check the selected tablet.");
                        return (stdout.Result.Trim() + Environment.NewLine + stderr.Result.Trim()).Trim();
                    }
                }
            }, dispatchToken);
        }

        private static async Task<string> ReadAdbOutputAsync(StreamReader reader, int maximum)
        {
            var text = new StringBuilder();
            var chunk = new char[4096];
            int count;
            while ((count = await reader.ReadAsync(chunk, 0, chunk.Length).ConfigureAwait(false)) != 0)
            {
                // Drain both pipes even on overflow so the child can exit without blocking on Windows.
                if (text == null) continue;
                if (count > maximum - text.Length) text = null;
                else text.Append(chunk, 0, count);
            }
            return text == null ? null : text.ToString();
        }

        private static Process NewProcess(string executable, string arguments)
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = executable,
                    Arguments = arguments,
                    WorkingDirectory = Path.GetDirectoryName(executable),
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                }
            };
            // ADB may keep its server running after the bridge closes. Android helpers do not use this key.
            process.StartInfo.EnvironmentVariables.Remove(ConnectSession.KeyVariable);
            return process;
        }

        private static void KillIfRunning(Process process)
        {
            try
            {
                if (process != null && !process.HasExited)
                    process.Kill();
            }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        }

        private static SafeFileHandle AttachSessionJob(Process process)
        {
            SafeFileHandle job = CreateJobObject(IntPtr.Zero, null);
            var limits = new JobLimits();
            limits.Basic.Flags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            if (!job.IsInvalid && SetInformationJobObject(job, 9, ref limits, Marshal.SizeOf(typeof(JobLimits)))
                && AssignProcessToJobObject(job, process.Handle))
                return job;

            // A host's existing job can prohibit nesting. Stop still reaps the scrcpy PID itself.
            job.Dispose();
            return null;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BasicJobLimits
        {
            public long ProcessTime, JobTime;
            public uint Flags;
            public UIntPtr MinimumWorkingSet, MaximumWorkingSet;
            public uint ProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass, SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JobLimits
        {
            public BasicJobLimits Basic;
            public ulong ReadOperations, WriteOperations, OtherOperations, ReadBytes, WriteBytes, OtherBytes;
            public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemory, PeakJobMemory;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern SafeFileHandle CreateJobObject(IntPtr security, string name);
        [DllImport("kernel32.dll")]
        private static extern bool SetInformationJobObject(SafeFileHandle job, int infoClass, ref JobLimits limits, int bytes);
        [DllImport("kernel32.dll")]
        private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);

        private static string ValidateSerial(string serial)
        {
            string device = (serial ?? String.Empty).Trim();
            if (!SerialPattern.IsMatch(device))
                throw new ArgumentException("Select an exact tablet serial from the device list.");
            return device;
        }

        private static string ValidateEndpoint(string endpoint)
        {
            string normalized = NormalizeEndpoint(endpoint);
            if (normalized == null)
                throw new ArgumentException("Enter the IP address and port displayed on the tablet, e.g. 192.168.1.25:37129.");
            return normalized;
        }

        internal static bool IsNetworkSerial(string serial)
        {
            return serial.TrimEnd('.').EndsWith("._adb-tls-connect._tcp", StringComparison.OrdinalIgnoreCase)
                || NormalizeEndpoint(serial) != null;
        }

        private static string NormalizeEndpoint(string endpoint)
        {
            string address = (endpoint ?? String.Empty).Trim();
            Match match = EndpointPattern.Match(address);
            IPAddress ip;
            int port;
            if (!match.Success || !IPAddress.TryParse(match.Groups["ip"].Value.Trim('[', ']'), out ip)
                || !Int32.TryParse(match.Groups["port"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out port)
                || port < 1 || port > 65535)
                return null;
            string host = ip.AddressFamily == AddressFamily.InterNetworkV6 ? "[" + ip + "]" : ip.ToString();
            return host + ":" + port.ToString(CultureInfo.InvariantCulture);
        }

        private static void ValidateAction(string action)
        {
            if (action != "next" && action != "previous")
                throw new ArgumentException("Choose either next or previous.");
        }

        private void ThrowIfDisposed()
        {
            if (disposed)
                throw new ObjectDisposedException("AndroidBridge");
        }

        private void EmitLine(string line)
        {
            Action<string> log = Log;
            if (log != null && !String.IsNullOrWhiteSpace(line))
                log(line);
        }
    }
}

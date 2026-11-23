using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace PcOnSongBridge
{
    internal static class ScrcpyPackage
    {
        internal const string Folder = "scrcpy-win64-v5.0.1";
        private const string DownloadUrl = "https://github.com/Genymobile/scrcpy/releases/download/v5.0.1/" + Folder + ".zip";
        private const string ArchiveSha256 = "B12A2C4EE8BE317422451FC7DCF8EE20A71B5EA7EF9AD73DDD825A25316227A5";
        private const int ArchiveBytes = 11264634;
        private static readonly string[] Files =
        {
            "AdbWinApi.dll", "AdbWinUsbApi.dll", "adb.exe", "avcodec-63.dll", "avformat-63.dll", "avutil-61.dll",
            "disconnected.png", "libusb-1.0.dll", "LICENSE.txt", "open_a_terminal_here.bat", "scrcpy-noconsole.vbs",
            "scrcpy-server", "scrcpy.exe", "scrcpy.png", "SDL3.dll", "swresample-7.dll"
        };

        private static string ToolsDirectory
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PC OnSong Bridge", "tools"); }
        }

        public static string InstalledExecutable { get { return Path.Combine(ToolsDirectory, Folder, "scrcpy.exe"); } }

        public static Task<string> InstallAsync(CancellationToken token) { return InstallAsync(ToolsDirectory, token); }

        internal static async Task<string> InstallAsync(string toolsDirectory, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (!Environment.Is64BitOperatingSystem)
                throw new PlatformNotSupportedException("The built-in scrcpy download requires 64-bit Windows. Choose a compatible scrcpy.exe manually.");
            try
            {
                using (var handler = new HttpClientHandler { SslProtocols = SslProtocols.Tls12 })
                using (var client = new HttpClient(handler) { MaxResponseContentBufferSize = ArchiveBytes, Timeout = TimeSpan.FromMinutes(2) })
                using (HttpResponseMessage response = await client.GetAsync(DownloadUrl, token).ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();
                    using (Stream archive = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                        return await Task.Run(() => InstallArchive(archive, toolsDirectory, token), token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                token.ThrowIfCancellationRequested();
                throw new TimeoutException("The scrcpy download timed out. Check the connection and try again.");
            }
        }

        internal static string InstallArchive(Stream archive, string toolsDirectory, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            using (SHA256 sha = SHA256.Create())
            {
                if (archive.Length != ArchiveBytes || !String.Equals(
                    BitConverter.ToString(sha.ComputeHash(archive)).Replace("-", ""), ArchiveSha256, StringComparison.Ordinal))
                    throw new InvalidDataException("The scrcpy download failed SHA-256 verification. Download it again.");
            }
            archive.Position = 0;
            Directory.CreateDirectory(toolsDirectory);
            string staging = Path.Combine(toolsDirectory, ".install-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            try
            {
                // Only the exact, flat names in the pinned release may become local filesystem paths.
                var remaining = new HashSet<string>(Files.Select(name => Folder + "/" + name), StringComparer.Ordinal);
                using (var zip = new ZipArchive(archive, ZipArchiveMode.Read, true))
                {
                    foreach (ZipArchiveEntry entry in zip.Entries)
                    {
                        token.ThrowIfCancellationRequested();
                        if (entry.FullName == Folder + "/" && entry.Length == 0) continue;
                        if (!remaining.Remove(entry.FullName))
                            throw new InvalidDataException("The scrcpy archive contains an unexpected entry.");
                        using (Stream source = entry.Open())
                        using (Stream file = File.Create(Path.Combine(staging, entry.Name)))
                            source.CopyTo(file);
                    }
                    if (remaining.Count != 0) throw new InvalidDataException("The scrcpy archive is incomplete.");
                }

                string destination = Path.Combine(toolsDirectory, Folder);
                using (FileStream publishLock = AcquirePublishLock(toolsDirectory, token))
                {
                    token.ThrowIfCancellationRequested();
                    if (!MatchesPackage(destination, staging))
                    {
                        token.ThrowIfCancellationRequested();
                        Publish(staging, destination);
                    }
                }
                return Path.Combine(destination, "scrcpy.exe");
            }
            finally
            {
                // This unique staging directory only holds files created by this install attempt.
                if (Directory.Exists(staging)) Directory.Delete(staging, true);
            }
        }

        private static FileStream AcquirePublishLock(string directory, CancellationToken token)
        {
            string path = Path.Combine(directory, ".scrcpy-install.lock");
            var timeout = System.Diagnostics.Stopwatch.StartNew();
            while (true)
            {
                token.ThrowIfCancellationRequested();
                try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
                catch (IOException error)
                {
                    int code = error.HResult & 0xffff;
                    if ((code != 32 && code != 33) || timeout.Elapsed > TimeSpan.FromSeconds(30)) throw;
                    token.WaitHandle.WaitOne(100);
                }
            }
        }

        private static bool MatchesPackage(string installed, string staged)
        {
            if (!Directory.Exists(installed) || (File.GetAttributes(installed) & FileAttributes.ReparsePoint) != 0 ||
                Directory.GetFileSystemEntries(installed).Length != Files.Length) return false;
            using (SHA256 sha = SHA256.Create())
            {
                foreach (string name in Files)
                {
                    string path = Path.Combine(installed, name);
                    if (!File.Exists(path) || (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return false;
                    using (Stream existing = File.OpenRead(path))
                    using (Stream expected = File.OpenRead(Path.Combine(staged, name)))
                    {
                        if (existing.Length != expected.Length || !sha.ComputeHash(existing).SequenceEqual(sha.ComputeHash(expected)))
                            return false;
                    }
                }
            }
            return true;
        }

        private static void Publish(string staging, string destination)
        {
            // Preserve any previous incomplete or modified contents. Publish the verified tree on the same volume.
            string backup = destination + ".previous-" + Guid.NewGuid().ToString("N");
            bool oldDirectory = Directory.Exists(destination);
            bool oldFile = File.Exists(destination);
            if (oldDirectory) Directory.Move(destination, backup);
            else if (oldFile) File.Move(destination, backup);
            try { Directory.Move(staging, destination); }
            catch
            {
                if (oldDirectory) Directory.Move(backup, destination);
                else if (oldFile) File.Move(backup, destination);
                throw;
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

namespace PcOnSongBridge
{
    internal static class NdiRuntime
    {
        // Vendor-signed NDI 6 Runtime 6.4.0.2. The unversioned URL is updated by the vendor.
        private const string DownloadUrl = "https://downloads.ndi.tv/SDK/NDI_SDK/NDI%206%20Runtime.exe";
        private const string ExpectedHash = "5972CCA54649039318FF5F167FD9940270CAFC09678B01E0C35695382270A144";
        private const long MaxSize = 24 * 1024 * 1024;

        internal static string FindLibrary()
        {
            var directories = new List<string>();
            foreach (EnvironmentVariableTarget target in new[] { EnvironmentVariableTarget.Process,
                EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine })
                directories.Add(Environment.GetEnvironmentVariable("NDI_RUNTIME_DIR_V6", target));
            foreach (string root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
            {
                directories.Add(Path.Combine(root, @"NDI\NDI 6 Runtime\v6"));
                directories.Add(Path.Combine(root, @"NDI\NDI 6 Runtime"));
            }
            foreach (string directory in directories)
            {
                if (String.IsNullOrWhiteSpace(directory)) continue;
                string folder = directory.Trim().Trim('"');
                if (!WindowsToolTrust.IsLocalDrivePath(folder)) continue;
                try
                {
                    string dll = Path.Combine(folder, Environment.Is64BitProcess ?
                        "Processing.NDI.Lib.x64.dll" : "Processing.NDI.Lib.x86.dll");
                    using (var verified = WindowsToolTrust.OpenTrustedNdiLibrary(dll))
                        if (verified != null) return verified.Name;
                }
                catch (ArgumentException) { }
                catch (NotSupportedException) { }
            }
            return null;
        }

        internal static async Task OpenSetupAsync(CancellationToken token)
        {
            string dir = Path.Combine(Path.GetTempPath(), "OnSong-NDI-Setup-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "NDI 6 Runtime.exe");
            try
            {
                using (var handler = new HttpClientHandler { SslProtocols = SslProtocols.Tls12 })
                using (var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(2) })
                using (var response = await client.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false))
                {
                    response.EnsureSuccessStatusCode();
                    using (var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        byte[] buffer = new byte[81920];
                        int count;
                        while ((count = await input.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false)) > 0)
                        {
                            if (output.Position + count > MaxSize) throw new InvalidDataException("Unexpected NDI setup size.");
                            await output.WriteAsync(buffer, 0, count, token).ConfigureAwait(false);
                        }
                    }
                }
                // Keep the verified image locked against writes and replacement through the ShellExecute/UAC launch.
                using (var verified = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    using (var sha = SHA256.Create())
                        if (verified.Length > MaxSize || BitConverter.ToString(sha.ComputeHash(verified)).Replace("-", "") != ExpectedHash)
                            throw new InvalidDataException("The download differs from the NDI setup verified for this release. Install the current NDI 6 Runtime from the official NDI website, then search again.");
                    token.ThrowIfCancellationRequested();
                    using (var process = Process.Start(new ProcessStartInfo(path, "/NORESTART") { UseShellExecute = true }))
                        await Task.Run(() =>
                        {
                            while (!process.WaitForExit(200)) token.ThrowIfCancellationRequested();
                        }).ConfigureAwait(false);
                }
            }
            finally
            {
                try { File.Delete(path); Directory.Delete(dir); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}

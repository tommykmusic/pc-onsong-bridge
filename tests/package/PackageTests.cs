using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using PcOnSongBridge;

internal class PackageTests
{
    private static void Check(bool ok, string message)
    {
        if (!ok) throw new Exception(message);
    }

    private static string InstallFile(string zip, string root)
    {
        using (Stream file = File.OpenRead(zip)) return ScrcpyPackage.InstallArchive(file, root, CancellationToken.None);
    }

    private static string Version(string executable)
    {
        var start = new ProcessStartInfo(executable, "--version")
        {
            CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true,
            WorkingDirectory = Path.GetDirectoryName(executable)
        };
        using (var process = Process.Start(start))
        {
            string output = process.StandardOutput.ReadToEnd();
            Check(process.WaitForExit(10000) && process.ExitCode == 0, "Installed scrcpy version check failed.");
            return output;
        }
    }

    private static int Main(string[] args)
    {
        string scratch = Path.Combine(Path.GetTempPath(), "OnSongPackageTest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            string zip = args.Length == 0 ? null : args[0];
            string installed = zip == null
                ? ScrcpyPackage.InstallAsync(scratch, CancellationToken.None).GetAwaiter().GetResult()
                : InstallFile(zip, scratch);
            Check(Version(installed).Contains("scrcpy 5.0.1"), "The installed program returned the wrong version.");
            Console.WriteLine("PASS verified package install and real scrcpy --version");
            string folder = Path.GetDirectoryName(installed);
            string server = Path.Combine(folder, "scrcpy-server");
            string marker = Path.Combine(scratch, "user-data.txt");
            File.WriteAllText(marker, "keep");
            byte[] corrupt = zip == null ? new byte[11264634] : File.ReadAllBytes(zip);
            corrupt[corrupt.Length / 2] ^= 0xFF;
            using (var stream = new MemoryStream(corrupt))
            {
                try
                {
                    ScrcpyPackage.InstallArchive(stream, scratch, CancellationToken.None);
                    throw new Exception("Damaged archive was accepted.");
                }
                catch (InvalidDataException error) { Check(error.Message.Contains("SHA-256"), "Wrong rejection reason."); }
            }
            Check(File.Exists(server) && File.ReadAllText(marker) == "keep", "Invalid package changed installed files.");
            Console.WriteLine("PASS same-length corrupt archive rejected before installation");
            // Exercise repair, with a same-length corrupt installed binary and a real concurrent second attempt.
            using (var file = File.Open(server, FileMode.Open, FileAccess.ReadWrite))
            {
                int original = file.ReadByte();
                file.Position = 0;
                file.WriteByte((byte)(original ^ 0xFF));
            }
            Func<string> reinstall = () => zip == null
                ? ScrcpyPackage.InstallAsync(scratch, CancellationToken.None).GetAwaiter().GetResult()
                : InstallFile(zip, scratch);
            Task<string> first = Task.Run(reinstall);
            Task<string> second = Task.Run(reinstall);
            Task.WaitAll(first, second);
            string[] backups = Directory.GetDirectories(scratch, ScrcpyPackage.Folder + ".previous-*");
            Check(first.Result == installed && second.Result == installed && backups.Length == 1, "Concurrent installation failed.");
            Check(File.Exists(Path.Combine(backups[0], "scrcpy-server")) && File.Exists(server), "Recovery did not preserve old files.");
            Check(Directory.GetDirectories(scratch, ".install-*").Length == 0, "A staging directory was left behind.");
            Check(Version(installed).Contains("scrcpy 5.0.1"), "Recovered release is unusable.");
            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();
                try
                {
                    ScrcpyPackage.InstallAsync(scratch, cancellation.Token).GetAwaiter().GetResult();
                    throw new Exception("Canceled installation succeeded.");
                }
                catch (OperationCanceledException) { }
            }
            Check(File.ReadAllText(marker) == "keep", "Installation touched unrelated user data.");
            Console.WriteLine("PASS concurrent reinstall, corrupt file recovery, preserved prior folder and cancellation");
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
        finally { Directory.Delete(scratch, true); }
    }
}

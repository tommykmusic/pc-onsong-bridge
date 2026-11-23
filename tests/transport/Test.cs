using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using PcOnSongBridge;
class Test
{
    static void Require(bool ok)
    {
        if (!ok) throw new Exception("Assertion failed.");
    }
    static void Failure<T>(Action op) where T : Exception
    {
        try { op(); }
        catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }
    static void Main()
    {
        // The simulated Android programs reject any inherited development key, without inspecting its value.
        Environment.SetEnvironmentVariable(ConnectSession.KeyVariable, "fixture-connect-key");
        using (var bridge = new AndroidBridge(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "scrcpy.exe")))
        using (var stream = new ManualResetEventSlim(false))
        using (var stopped = new ManualResetEventSlim(false))
        {
            bridge.Log += line => { if (line == "fake audio stream") stream.Set(); };
            bridge.Stopped += () => stopped.Set();
            Require(bridge.ListDevicesAsync().GetAwaiter().GetResult().Contains("READY123"));
            foreach (string flag in new[] { "discovery-error", "large-diagnostic" })
            {
                string file = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, flag);
                File.WriteAllText(file, "");
                try
                {
                    bridge.ListDevicesAsync().GetAwaiter().GetResult();
                    throw new Exception("Expected a concise ADB diagnostic.");
                }
                catch (InvalidOperationException error)
                {
                    Require(error.Message.Length < 225 && !error.Message.Contains("transport unavailable") &&
                        error.Message.Contains(flag == "large-diagnostic" ? "too much diagnostic output" : "exit code"));
                }
                finally { File.Delete(file); }
            }
            Require(bridge.ConnectAsync("192.168.1.1:65535").GetAwaiter().GetResult().Contains("connected"));
            Failure<InvalidOperationException>(() => bridge.ConnectAsync("192.168.1.2:40000").GetAwaiter().GetResult());
            Failure<ArgumentException>(() => bridge.ConnectAsync("192.168.1.1:65536").GetAwaiter().GetResult());
            Require(bridge.PairAsync("[::1]:40000", "123456").GetAwaiter().GetResult().Contains("paired"));
            Failure<ArgumentException>(() => bridge.PairAsync("[::1]:40000", "12345x").GetAwaiter().GetResult());
            Failure<InvalidOperationException>(() => bridge.StartAsync("OFF123", 75).GetAwaiter().GetResult());
            Failure<ArgumentException>(() => bridge.StartAsync("READY123 & nope", 75).GetAwaiter().GetResult());
            var pending = bridge.StartAsync("READY123", 75);
            bridge.Stop();
            Failure<OperationCanceledException>(() => pending.GetAwaiter().GetResult());
            Require(!bridge.IsRunning);
            bridge.StartAsync("READY123", 75).GetAwaiter().GetResult();
            Require(bridge.IsRunning);
            Require(stream.Wait(2000));
            Require(bridge.HasSongLinksAsync("READY123").GetAwaiter().GetResult());
            Require(bridge.SendActionAsync("READY123", "next").GetAwaiter().GetResult().Contains("delivered"));
            Failure<InvalidOperationException>(() => bridge.SendActionAsync("READY123", "previous").GetAwaiter().GetResult());
            Require(bridge.CheckKeyboardAsync("READY123").GetAwaiter().GetResult().Contains("focus"));
            Require(bridge.SendKeyboardActionAsync("READY123", "previous").GetAwaiter().GetResult().Contains("previous"));
            Failure<InvalidOperationException>(() => bridge.SendKeyboardActionAsync("OTHER123", "previous").GetAwaiter().GetResult());
            bridge.Stop();
            Require(stopped.Wait(2000));
            Require(!bridge.IsRunning);
        }
        CheckSessionCleanup();
        Console.WriteLine("Android bridge fixture passed.");
    }

    static void CheckSessionCleanup()
    {
        string dir = AppDomain.CurrentDomain.BaseDirectory;
        string spawn = Path.Combine(dir, "test-descendant");
        string exit = Path.Combine(dir, "exit-parent");
        File.WriteAllText(spawn, "");
        var externalClient = new ProcessStartInfo(Path.Combine(dir, "adb.exe"), "--hold-stdio")
        {
            UseShellExecute = false, CreateNoWindow = true
        };
        using (Process unrelated = Process.Start(externalClient))
        using (var bridge = new AndroidBridge(Path.Combine(dir, "scrcpy.exe")))
        using (var childReady = new ManualResetEventSlim(false))
        using (var stopped = new ManualResetEventSlim(false))
        {
            int childId = 0;
            bridge.Log += line =>
            {
                if (line.StartsWith("shell PID ") && Int32.TryParse(line.Substring(10), out childId))
                    childReady.Set();
            };
            bridge.Stopped += () => stopped.Set();
            try
            {
                foreach (string action in new[] { "exit", "stop", "dispose" })
                {
                    childReady.Reset();
                    stopped.Reset();
                    bridge.StartAsync("READY123", 75).GetAwaiter().GetResult();
                    Require(childReady.Wait(3000));
                    using (Process shell = Process.GetProcessById(childId))
                    {
                        try
                        {
                            if (action == "exit") File.WriteAllText(exit, "");
                            else if (action == "stop") bridge.Stop();
                            else bridge.Dispose();
                            if (action != "dispose") Require(stopped.Wait(3000) && !bridge.IsRunning);
                            Require(shell.WaitForExit(3000));
                            Require(!unrelated.HasExited);
                        }
                        finally
                        {
                            if (!shell.HasExited) { shell.Kill(); shell.WaitForExit(3000); }
                            File.Delete(exit);
                        }
                    }
                }
                Console.WriteLine("PASS parent exit, stop, restart and close with inherited pipes; unrelated ADB client survived.");
            }
            finally
            {
                File.Delete(spawn);
                if (!unrelated.HasExited) { unrelated.Kill(); unrelated.WaitForExit(3000); }
            }
        }
    }
}

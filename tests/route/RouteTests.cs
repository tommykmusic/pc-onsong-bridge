using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using PcOnSongBridge;

internal static class RouteTests
{
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern bool PlaySound(IntPtr sound, IntPtr module, uint flags);
    [DllImport("winmm.dll")]
    private static extern uint waveOutGetNumDevs();

    private static void Check(bool ok, string message)
    {
        if (!ok) throw new Exception(message);
    }

    private static void CheckState(PlaybackRouteState expected, string target, string targetName, params string[] ids)
    {
        // Give all outputs the same friendly name to catch accidental name comparisons.
        var names = new List<string>();
        foreach (string id in ids) names.Add("Speakers");
        PlaybackRouteResult result = PlaybackRoute.Describe(target, targetName, new List<string>(ids), names);
        Check(result.State == expected, "Expected " + expected + ", got " + result.State);
        Check(result.ActiveIds.Length == ids.Length && result.ActiveNames.Length == ids.Length, "Missing endpoint details.");
    }

    // Use a child with its own PID. The PCM samples are all zero. SND_NODEFAULT prevents fallback sounds.
    private static int PlaySilence()
    {
        byte[] wave;
        using (var stream = new MemoryStream())
        using (var writer = new BinaryWriter(stream))
        {
            byte[] silence = new byte[48000 * 2];
            writer.Write(Encoding.ASCII.GetBytes("RIFF"));
            writer.Write(36 + silence.Length);
            writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
            writer.Write(16);
            writer.Write((short)1); writer.Write((short)1); // PCM, mono
            writer.Write(48000); writer.Write(96000); writer.Write((short)2); writer.Write((short)16);
            writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(silence.Length); writer.Write(silence);
            wave = stream.ToArray();
        }
        GCHandle pin = GCHandle.Alloc(wave, GCHandleType.Pinned);
        try
        {
            if (!PlaySound(pin.AddrOfPinnedObject(), IntPtr.Zero, 1 | 2 | 4 | 8)) return 2; // async, no default, memory, loop
            Console.ReadLine();
        }
        finally
        {
            PlaySound(IntPtr.Zero, IntPtr.Zero, 0);
            pin.Free();
        }
        return 0;
    }

    private static void TestWindowsSession()
    {
        uint count = waveOutGetNumDevs();
        if (count == 0)
        {
            Console.WriteLine("SKIP live session: Windows has no waveOut device.");
            return;
        }
        var endpointIds = new List<string>();
        for (int i = 0; i < count; i++)
        {
            string id = PlaybackRoute.GetWaveOutEndpointId(i);
            if (!String.IsNullOrEmpty(id)) endpointIds.Add(id);
        }
        Check(endpointIds.Count > 0, "No WinMM output could be resolved to a Core Audio endpoint ID.");
        var start = new ProcessStartInfo(typeof(RouteTests).Assembly.Location, "--silence")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true
        };
        using (Process child = Process.Start(start))
        {
            try
            {
                PlaybackRouteResult observed;
                var timer = Stopwatch.StartNew();
                do
                {
                    Check(!child.HasExited, "The silent child could not start playback.");
                    observed = PlaybackRoute.Inspect(child.Id, (string)null);
                    if (observed.State == PlaybackRouteState.Unselected) break;
                    Thread.Sleep(100);
                } while (timer.ElapsedMilliseconds < 10000);
                Check(observed.State == PlaybackRouteState.Unselected, "Silent stream detection failed. " + observed.Message);
                Check(observed.ActiveIds.Length == 1 && observed.ActiveNames[0].Length > 0 &&
                    observed.Message.Contains(observed.ActiveNames[0]), "Active endpoint details missing.");
                int index = endpointIds.FindIndex(id => String.Equals(id, observed.ActiveIds[0], StringComparison.OrdinalIgnoreCase));
                Check(index >= 0, "Core Audio endpoint does not correspond to a WinMM output.");
                string selectedId = endpointIds[index];
                Check(PlaybackRoute.Inspect(child.Id, selectedId).State == PlaybackRouteState.Matched, "Live endpoint mismatch.");
                int parentPid;
                using (Process parent = Process.GetCurrentProcess()) parentPid = parent.Id;
                Check(PlaybackRoute.Inspect(parentPid, selectedId).State == PlaybackRouteState.Pending, "Unrelated PID matched.");
                string other = endpointIds.Find(id => !String.Equals(id, selectedId, StringComparison.OrdinalIgnoreCase));
                if (other != null)
                    Check(PlaybackRoute.Inspect(child.Id, other).State == PlaybackRouteState.Mismatch, "Wrong Windows output matched.");
                int childPid = child.Id;
                child.StandardInput.Close();
                Check(child.WaitForExit(3000) && child.ExitCode == 0, "Silent child failed to stop.");
                timer.Restart();
                PlaybackRouteResult stopped;
                do
                {
                    stopped = PlaybackRoute.Inspect(childPid, selectedId);
                    if (stopped.State == PlaybackRouteState.Pending) break;
                    Thread.Sleep(100);
                } while (timer.ElapsedMilliseconds < 5000);
                Check(stopped.State == PlaybackRouteState.Pending, "Stopped session stayed active. " + stopped.Message);
                Console.WriteLine("PASS live silent stream on " + observed.ActiveNames[0] + ", PID isolation and stop.");
            }
            finally
            {
                if (!child.HasExited)
                {
                    child.StandardInput.Close();
                    if (!child.WaitForExit(3000)) { child.Kill(); child.WaitForExit(); }
                }
            }
        }
    }

    [MTAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--silence") return PlaySilence();
        try
        {
            CheckState(PlaybackRouteState.Pending, "a", "Speakers");
            CheckState(PlaybackRouteState.Matched, "a", "Speakers", "A");
            CheckState(PlaybackRouteState.Mismatch, "a", "Speakers", "b");
            CheckState(PlaybackRouteState.Multiple, "a", "Speakers", "a", "b");
            CheckState(PlaybackRouteState.Unselected, null, null, "b");
            CheckState(PlaybackRouteState.Unavailable, "missing", null, "b");
            Check(PlaybackRoute.GetWaveOutEndpointId(-1) == null, "Invalid waveOut index should not resolve.");
            Check(PlaybackRoute.GetEndpointFriendlyName("unavailable endpoint") == null,
                "A missing endpoint must allow the WinMM name fallback.");
            Console.WriteLine("PASS route comparisons, duplicate names and unavailable devices.");
            TestWindowsSession();
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
}

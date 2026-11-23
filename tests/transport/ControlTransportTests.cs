using System;
using System.IO;
using System.Linq;
using System.Threading;
using PcOnSongBridge;

internal static class ControlTransportTests
{
    private static void Check(bool ok, string message)
    {
        if (!ok) throw new Exception(message);
    }

    private static void RequireFailure<T>(Action test) where T : Exception
    {
        try { test(); }
        catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }

    private static void Main()
    {
        // Keep this fixture's flags separate from the other transport suites in the same runner.
        string source = AppDomain.CurrentDomain.BaseDirectory;
        string dir = Path.Combine(source, "command-fixture");
        Directory.CreateDirectory(dir);
        File.Copy(Path.Combine(source, "scrcpy.exe"), Path.Combine(dir, "scrcpy.exe"));
        File.Copy(Path.Combine(source, "adb.exe"), Path.Combine(dir, "adb.exe"));
        string index = Path.Combine(dir, "song-index");
        string trace = Path.Combine(dir, "dispatched-navigation");
        Func<int> dispatchCount = () => File.Exists(trace) ? File.ReadAllLines(trace).Length : 0;
        const string tablet = "READY123";
        var token = CancellationToken.None;
        File.WriteAllText(Path.Combine(dir, "track-navigation"), "");
        File.WriteAllText(index, "53");
        using (var bridge = new AndroidBridge(Path.Combine(dir, "scrcpy.exe")))
        {
            string baseline = null;
            bridge.SendObservedSwipeAsync(tablet, "next", identity => baseline = identity, token).GetAwaiter().GetResult();
            string transition = Path.Combine(dir, "page-turn-frame");
            File.WriteAllText(transition, "");
            Check(baseline != null && bridge.WaitForChartChangeAsync(tablet, baseline, "next", token).GetAwaiter().GetResult(),
                "Expected a second observation after two sheets were briefly visible.");
            Check(!File.Exists(transition), "The transitional snapshot was not read.");
            Check(!bridge.WaitForChartChangeAsync(tablet, baseline, "previous", token).GetAwaiter().GetResult(), "Wrong direction counted.");
            Check(dispatchCount() == 1, "Observation retriggered a command.");
            string second = bridge.ReadChartIdentityAsync(tablet, token).GetAwaiter().GetResult();
            bridge.SendSwipeActionAsync(tablet, "previous", token).GetAwaiter().GetResult();
            Check(bridge.WaitForChartChangeAsync(tablet, second, "previous", token).GetAwaiter().GetResult(), "Previous was not observed.");
            bridge.SendSwipeActionAsync(tablet, "scroll-down", token).GetAwaiter().GetResult();
            bridge.SendSwipeActionAsync(tablet, "scroll-up", token).GetAwaiter().GetResult();
            Check(File.ReadAllText(index) == "53", "Scroll changed the simulated set position.");
            bridge.SendShortcutAsync(tablet, VoiceShortcut.Parse("C"), token).GetAwaiter().GetResult();
            bridge.SendShortcutAsync(tablet, VoiceShortcut.Parse("Ctrl+C"), token).GetAwaiter().GetResult();
            Check(dispatchCount() == 6, "Unexpected input count.");

            bridge.Stop(); // New connection must recheck the tablet's available input commands.
            File.WriteAllText(Path.Combine(dir, "no-key-chords"), "");
            RequireFailure<InvalidOperationException>(() => bridge.SendShortcutAsync(tablet, VoiceShortcut.Parse("Ctrl+C"), token).GetAwaiter().GetResult());
            File.WriteAllText(Path.Combine(dir, "other-foreground"), "");
            RequireFailure<InvalidOperationException>(() => bridge.SendShortcutAsync(tablet, VoiceShortcut.Parse("C"), token).GetAwaiter().GetResult());
            File.Delete(Path.Combine(dir, "other-foreground"));
            File.WriteAllText(Path.Combine(dir, "vertical-overlay"), "");
            RequireFailure<InvalidOperationException>(() => bridge.SendSwipeActionAsync(tablet, "scroll-down", token).GetAwaiter().GetResult());
            File.Delete(Path.Combine(dir, "vertical-overlay"));
            using (var canceled = new CancellationTokenSource())
                RequireFailure<OperationCanceledException>(() => bridge.SendObservedSwipeAsync(tablet, "next",
                    identity => canceled.Cancel(), canceled.Token).GetAwaiter().GetResult());
            using (var canceled = new CancellationTokenSource(100))
                RequireFailure<OperationCanceledException>(() => bridge.WaitForChartChangeAsync(tablet, baseline, "next", canceled.Token).GetAwaiter().GetResult());
            Check(dispatchCount() == 6, "A denied, canceled or read-only operation sent input.");
            File.WriteAllText(Path.Combine(dir, "input-rejected"), "");
            RequireFailure<InvalidOperationException>(() => bridge.SendShortcutAsync(tablet, VoiceShortcut.Parse("C"), token).GetAwaiter().GetResult());
            Check(dispatchCount() == 7, "An Android input error retried the command.");
            File.Delete(Path.Combine(dir, "input-rejected"));
            File.Delete(Path.Combine(dir, "no-key-chords"));
            var chord = VoiceShortcut.Parse("Ctrl+Shift+J");
            RequireFailure<InvalidOperationException>(() => bridge.TeachShortcutAsync(tablet, chord, token).GetAwaiter().GetResult());
            Check(dispatchCount() == 7, "The bridge held a shortcut in the song viewer.");
            File.WriteAllText(Path.Combine(dir, "shortcut-editor"), "");
            File.WriteAllText(Path.Combine(dir, "action-picker"), "");
            RequireFailure<InvalidOperationException>(() => bridge.TeachShortcutAsync(tablet, chord, token).GetAwaiter().GetResult());
            Check(dispatchCount() == 7, "The bridge held a shortcut in the action picker.");
            File.Delete(Path.Combine(dir, "action-picker"));
            File.WriteAllText(Path.Combine(dir, "switch-after-layout"), "");
            RequireFailure<InvalidOperationException>(() => bridge.TeachShortcutAsync(tablet, chord, token).GetAwaiter().GetResult());
            Check(dispatchCount() == 7, "The bridge held a shortcut after a foreground switch.");
            File.Delete(Path.Combine(dir, "switch-after-layout"));
            File.Delete(Path.Combine(dir, "other-foreground"));
            bridge.TeachShortcutAsync(tablet, chord, token).GetAwaiter().GetResult();
            bridge.TeachShortcutAsync(tablet, VoiceShortcut.Parse("F9"), token).GetAwaiter().GetResult();
            Check(dispatchCount() == 9, "Shortcut setup must send exactly one hold for each request.");
            File.WriteAllText(Path.Combine(dir, "no-key-chords"), "");
            RequireFailure<InvalidOperationException>(() => bridge.TeachShortcutAsync(tablet, VoiceShortcut.Parse("F9"), token).GetAwaiter().GetResult());
            Check(dispatchCount() == 9, "An unsupported hold sent a short keypress.");
            File.Delete(Path.Combine(dir, "no-key-chords"));
            File.Delete(Path.Combine(dir, "shortcut-editor"));
            CheckFastDispatch(bridge, tablet, dir);
        }
        Console.WriteLine("PASS single dispatch, fast song chords, chart observation, guarded scrolling, editor-only teaching and cancellation.");
    }

    private static void CheckFastDispatch(AndroidBridge bridge, string tablet, string dir)
    {
        const TabletControlMode mode = TabletControlMode.FastSongShortcuts;
        var token = CancellationToken.None;
        var native = VoiceCommands.ForMode(mode, new VoiceBinding[0]);
        var next = native.Single(c => c.Id == "next");
        var previous = native.Single(c => c.Id == "previous");
        Check(next.Shortcut == "Ctrl+Shift+N" && previous.Shortcut == "Ctrl+Shift+P", "Canonical song shortcuts changed.");

        File.WriteAllText(Path.Combine(dir, "track-fast-input"), "");
        string trace = Path.Combine(dir, "fast-input-trace");
        string index = Path.Combine(dir, "song-index");
        bridge.CheckFastSongShortcutsAsync(tablet).GetAwaiter().GetResult();
        Check(File.ReadAllLines(trace).Count(line => line.EndsWith("input help")) == 1, "Preflight did not warm the chord capability.");
        File.WriteAllText(trace, "");
        CommandDispatch.SendAsync(bridge, tablet, mode, next, false, token).GetAwaiter().GetResult();
        var lines = File.ReadAllLines(trace);
        Check(lines.Length == 2 && lines[0].StartsWith("-s " + tablet + " shell -T (dumpsys window displays ") &&
            lines[1] == "remote input keycombination KEYCODE_CTRL_LEFT KEYCODE_SHIFT_LEFT KEYCODE_N",
            "Live next must use a single ADB shell with a fresh focus check and one chord.");
        Check(File.ReadAllText(index) == "54", "Fast next did not move to the adjacent song.");

        File.WriteAllText(trace, "");
        var missing = CommandDispatch.SendAsync(bridge, tablet, mode, previous, true, token).GetAwaiter().GetResult();
        lines = File.ReadAllLines(trace);
        Check(lines.Length == 2 && lines[1].EndsWith("keycombination KEYCODE_CTRL_LEFT KEYCODE_SHIFT_LEFT KEYCODE_P") && !missing.ChartObserved.HasValue,
            "Previous with no prepared chart must dispatch immediately without layout reads.");
        Check(File.ReadAllText(index) == "53", "Fast previous did not restore the adjacent song.");

        string before = bridge.ReadChartIdentityAsync(tablet, token).GetAwaiter().GetResult();
        bool acknowledged = false;
        File.WriteAllText(trace, "");
        var delivered = CommandDispatch.SendAsync(bridge, tablet, mode, next, true, token, before, () =>
        {
            var atAck = File.ReadAllLines(trace);
            Check(atAck.Length == 2 && atAck[1].EndsWith("KEYCODE_N"), "Chart read delayed the input acknowledgment.");
            acknowledged = true;
        }).GetAwaiter().GetResult();
        Check(acknowledged && delivered.ChartObserved.HasValue && delivered.ChartObserved.Value >= delivered.Acknowledged &&
            File.ReadAllLines(trace).Any(line => line.Contains("uiautomator")), "Expected the adjacent chart to be verified after acknowledgment.");

        File.WriteAllText(Path.Combine(dir, "legacy-focus-output"), "");
        File.WriteAllText(trace, "");
        CommandDispatch.SendAsync(bridge, tablet, mode, previous, false, token).GetAwaiter().GetResult();
        lines = File.ReadAllLines(trace);
        Check(lines.Length == 3 && lines[0].Contains("dumpsys window displays") && lines[1] == "remote dumpsys window" &&
            lines[2].EndsWith("KEYCODE_P"), "Android builds without a compact focus field must use the full focus check.");
        File.Delete(Path.Combine(dir, "legacy-focus-output"));

        foreach (string denied in new[] { "secondary-overlay", "focus-dump-failed" })
        {
            File.WriteAllText(Path.Combine(dir, denied), "");
            File.WriteAllText(trace, "");
            RequireFailure<InvalidOperationException>(() => CommandDispatch.SendAsync(bridge, tablet, mode, next, false, token).GetAwaiter().GetResult());
            Check(File.ReadAllLines(trace).Length == 1, "An ambiguous or interrupted foreground check sent input.");
            File.Delete(Path.Combine(dir, denied));
        }
        string hold = Path.Combine(dir, "hold-shortcut-focus");
        File.WriteAllText(hold, "");
        File.WriteAllText(trace, "");
        using (var cancel = new CancellationTokenSource())
        {
            var pending = CommandDispatch.SendAsync(bridge, tablet, mode, next, false, cancel.Token);
            Check(SpinWait.SpinUntil(() => File.Exists(Path.Combine(dir, "shortcut-focus-pending")), 5000), "Missing shortcut focus check.");
            cancel.Cancel();
            RequireFailure<OperationCanceledException>(() => pending.GetAwaiter().GetResult());
        }
        File.Delete(hold);
        Check(File.ReadAllLines(trace).Length == 1, "Stop during focus verification still sent a shortcut.");
        File.WriteAllText(Path.Combine(dir, "shortcut-receipt-lost"), "");
        File.WriteAllText(trace, "");
        RequireFailure<InvalidOperationException>(() => CommandDispatch.SendAsync(bridge, tablet, mode, next, false, token).GetAwaiter().GetResult());
        Check(File.ReadAllLines(trace).Length == 2 && File.ReadAllText(index) == "54", "A lost acknowledgment must not repeat a song change.");
    }
}

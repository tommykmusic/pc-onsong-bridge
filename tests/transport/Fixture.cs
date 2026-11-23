using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
class Fixture
{
    private const string WirelessSerial = "192.168.30.2:40501";
    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern bool PlaySound(string path, IntPtr module, uint flags);

    static int Main(string[] args)
    {
        string cmd = String.Join(" ", args);
        if (File.Exists("track-fast-input")) File.AppendAllText("fast-input-trace", cmd + Environment.NewLine);
        if (File.Exists("track-navigation") && (cmd.Contains("shell am start") || cmd.Contains("shell input ") && !cmd.EndsWith("input help")))
        {
            File.AppendAllText("dispatched-navigation", cmd + Environment.NewLine);
            for (int i = 0; i < 200 && File.Exists("hold-navigation-reply"); i++) Thread.Sleep(25);
        }
        if (cmd == "--hold-stdio")
        {
            Thread.Sleep(15000);
            return 0;
        }
        if (Environment.GetEnvironmentVariable("ONSONG_CONNECT_API_KEY") != null)
        {
            Console.Error.WriteLine("Unexpected Connect credential in Android process.");
            return 9;
        }
        if (Assembly.GetExecutingAssembly().GetName().Name == "scrcpy")
        {
            if (Environment.GetEnvironmentVariable("ADB") == null || cmd != "--serial=READY123 --no-window --no-control --require-audio --audio-source=output --audio-buffer=75")
            {
                Console.Error.WriteLine("unexpected stream command: " + cmd);
                return 3;
            }
            Console.WriteLine("fake audio stream");
            if (File.Exists("test-descendant"))
            {
                var child = new ProcessStartInfo(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "adb.exe"), "--hold-stdio")
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true
                };
                using (Process shell = Process.Start(child))
                {
                    shell.StandardInput.Close();
                    Console.WriteLine("shell PID " + shell.Id);
                }
                for (int i = 0; i < 300 && !File.Exists("exit-parent"); i++) Thread.Sleep(50);
                return 0;
            }
            if (!File.Exists("silence.wav")) Thread.Sleep(10000);
            else
            {
                bool playing = false;
                for (int i = 0; i < 600 && !File.Exists("disconnect"); i++)
                {
                    if (!playing && File.Exists("begin-playback"))
                    {
                        // SND_FILENAME | SND_ASYNC | SND_NODEFAULT | SND_LOOP, with all-zero PCM.
                        playing = PlaySound(Path.GetFullPath("silence.wav"), IntPtr.Zero, 0x2000b);
                        if (!playing) return 4;
                    }
                    Thread.Sleep(50);
                }
            }
            return 0;
        }
        if (cmd == "devices -l")
        {
            if (File.Exists("large-diagnostic")) { Console.Error.Write(new string('x', 70000)); return 0; }
            if (File.Exists("discovery-error")) { Console.Error.WriteLine("transport unavailable"); return 1; }
            Thread.Sleep(150);
            Console.WriteLine("List of devices attached\nOFF123 unauthorized");
            if (!File.Exists("disconnect"))
                Console.WriteLine((File.Exists("wireless-tablet") ? WirelessSerial : "READY123") + " device product:test model:test");
            return 0;
        }
        // Use the same simulated chart for USB and paired wireless debugging.
        // The dispatch trace above retains the actual serial sent by the bridge.
        if (cmd.StartsWith("-s " + WirelessSerial + " ", StringComparison.Ordinal) && File.Exists("wireless-tablet"))
            cmd = "-s READY123 " + cmd.Substring(WirelessSerial.Length + 4);
        if (cmd == "connect 192.168.1.1:65535") Console.WriteLine("connected to 192.168.1.1:65535");
        else if (cmd == "connect 192.168.1.2:40000") Console.WriteLine("failed to connect to 192.168.1.2:40000");
        else if (cmd == "pair [::1]:40000") Console.WriteLine(Console.ReadLine() == "123456" ? "Successfully paired to [::1]:40000" : "pairing error");
        else if (cmd == "-s READY123 shell getprop ro.build.version.sdk") Console.WriteLine("34");
        else if (cmd.Contains("shell cmd package resolve-activity") && cmd.EndsWith("-p com.onsongapp.app") && cmd.Contains("onsong://open/songs?index="))
        {
            if (File.Exists("resolver-error")) { Console.Error.WriteLine("Tablet transport interrupted"); return 1; }
            if (File.Exists("no-link") && cmd.Contains("index=previous")) Console.WriteLine("No activity found");
            else Console.WriteLine("priority=0 preferredOrder=0 match=0x108000 specificIndex=-1 isDefault=false\ncom.onsongapp.app/.MainActivity");
        }
        else if (cmd.Contains("shell am start -W") && cmd.EndsWith("-p com.onsongapp.app"))
        {
            if (cmd.Contains("index=next"))
                Console.WriteLine("Warning: Activity not started, intent has been delivered to currently running top-most instance.\nStatus: ok\nComplete");
            else
                Console.WriteLine("Warning: Activity not started, its current task has been brought to the front\nStatus: ok\nActivity: com.onsongapp.app/.MainActivity\nComplete");
        }
        else if (cmd == "-s READY123 shell dumpsys window" || cmd == "-s READY123 shell dumpsys window displays")
        {
            if (cmd.EndsWith(" displays") && File.Exists("legacy-focus-output")) { Console.WriteLine("Display #0"); return 0; }
            Console.WriteLine("  Display: mDisplayId=0 (organized)\n    init=1600x2400 320dpi cur=1600x2400 app=1600x2400");
            Console.WriteLine(File.Exists("other-foreground")
                ? "  mCurrentFocus=Window{6c0401 u0 com.android.launcher3/com.android.launcher3.MainActivity}"
                : "  mCurrentFocus=Window{2215424 u0 com.onsongapp.app/com.onsongapp.android.MainActivity}");
            Console.WriteLine("  DisplayRotation\n    mRotation=0 mDeferredRotationPauseCount=0\n  mTopFocusedDisplayId=0");
        }
        else if (cmd == "-s OTHER123 shell dumpsys window") Console.WriteLine("  mCurrentFocus=Window{6c0401 u0 com.android.launcher3/com.android.launcher3.MainActivity}");
        else if (cmd == "-s READY123 exec-out uiautomator dump /dev/tty")
        {
            if (File.Exists("shortcut-editor"))
            {
                WriteShortcutEditor();
                if (File.Exists("switch-after-layout")) File.WriteAllText("other-foreground", "");
            }
            else WriteViewer();
        }
        else if (cmd == "-s READY123 shell input help")
            Console.WriteLine(File.Exists("no-key-chords") ? "  keyevent <key code>" : "  keyevent [--longpress|--duration <duration to hold key down in ms>] <key code>\n  keycombination [-t duration(ms)] <key code 1> <key code 2> ...");
        else if (cmd == "-s READY123 shell input swipe 800 1728 800 672 300" || cmd == "-s READY123 shell input swipe 800 672 800 1728 300" ||
                 cmd == "-s READY123 shell input keyevent --duration 1400 KEYCODE_F9" ||
                 cmd == "-s READY123 shell input keycombination -t 1400 KEYCODE_CTRL_LEFT KEYCODE_SHIFT_LEFT KEYCODE_J" ||
                 cmd == "-s READY123 shell input keyevent KEYCODE_C" || cmd == "-s READY123 shell input keycombination KEYCODE_CTRL_LEFT KEYCODE_C")
        {
            if (File.Exists("input-rejected")) Console.WriteLine("Error: command rejected");
        }
        else if (cmd == "-s READY123 shell input keyevent KEYCODE_DPAD_LEFT" ||
                 cmd == "-s READY123 shell input keyevent KEYCODE_DPAD_RIGHT" ||
                 cmd == "-s READY123 shell input keycombination KEYCODE_CTRL_LEFT KEYCODE_SHIFT_LEFT KEYCODE_N" ||
                 cmd == "-s READY123 shell input keycombination KEYCODE_CTRL_LEFT KEYCODE_SHIFT_LEFT KEYCODE_P" ||
                 cmd == "-s READY123 shell input swipe 1152 1200 448 1200 300" ||
                 cmd == "-s READY123 shell input swipe 448 1200 1152 1200 300")
        {
            if (File.Exists("song-index"))
            {
                int current = Int32.Parse(File.ReadAllText("song-index"));
                bool forward = cmd.EndsWith("KEYCODE_DPAD_RIGHT") || cmd.EndsWith("KEYCODE_N") || cmd.Contains("swipe 1152");
                int next = current + (forward ? 1 : -1);
                if (next < 0 || next >= 100) return 7;
                File.WriteAllText("song-index", next.ToString());
            }
        }
        else { Console.Error.WriteLine("unexpected " + cmd); return 5; }
        return 0;
    }

    private static void WriteShortcutEditor()
    {
        var xml = new StringBuilder("<hierarchy rotation='0'><node package='com.onsongapp.app' bounds='[0,48][1600,2400]'>");
        foreach (string label in new[] { "Shortcuts", "SYSTEM DEFINED", "Press and hold keys to create new shortcuts", "Pedals", "MIDI", "Screen", "Shortcuts", "Voice" })
            xml.Append("<node package='com.onsongapp.app' class='android.widget.TextView' bounds='[120,200][390,240]' text='").Append(label).Append("'/>");
        if (File.Exists("action-picker"))
            xml.Append("<node package='com.onsongapp.app' class='android.widget.TextView' bounds='[120,200][390,240]' text='Choose Action'/>");
        Console.WriteLine(xml.Append("</node></hierarchy>UI hierchary dumped to: /dev/tty"));
    }

    private static void WriteViewer()
    {
        int current = File.Exists("song-index") ? Int32.Parse(File.ReadAllText("song-index")) : 53;
        const string full = "[0,0][1600,2400]";
        const string hidden = "[0,0][0,0]";
        Func<string, string, string> node = (id, bounds) =>
            "<node package=\"com.onsongapp.app\" class=\"android.view.View\" resource-id=\"" +
            id + "\" enabled=\"true\" bounds=\"" + bounds + "\">";
        var xml = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><hierarchy rotation=\"0\">");
        xml.Append("<node package=\"com.onsongapp.app\" class=\"android.webkit.WebView\" bounds=\"" + full + "\">");
        if (File.Exists("no-viewer")) xml.Append(node("settings", full) + "</node>");
        else
        {
            bool turning = File.Exists("page-turn-frame");
            if (turning) File.Delete("page-turn-frame");
            xml.Append(node("viewer", full)).Append(node("R2-Scroller", full));
            for (int i = current - 1; i <= current + 1; i++)
            {
                bool visible = i == current || (turning && i == current - 1);
                string bounds = visible ? full : hidden;
                string chartBounds = visible ? "[0,136][1600,2400]" : hidden;
                xml.Append(node("_sheet_" + i, bounds)).Append(node("_00000000-0000-4000-8000-" + i.ToString("D12"), chartBounds));
                xml.Append(node("page1", bounds)).Append("</node></node></node>");
            }
            xml.Append("</node></node>");
            if (File.Exists("vertical-overlay")) xml.Append(node("menu", "[760,700][840,760]") + "</node>");
        }
        Console.WriteLine(xml.Append("</node></hierarchy>UI hierchary dumped to: /dev/tty"));
    }
}

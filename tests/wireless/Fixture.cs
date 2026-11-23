using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

internal static class Fixture
{
    private static int Main(string[] args)
    {
        string command = String.Join(" ", args);
        if (Path.GetFileNameWithoutExtension(typeof(Fixture).Assembly.Location) == "scrcpy")
        {
            if (!command.StartsWith("--serial=USB123 ")) return 2;
            File.WriteAllText("stream.pid", Process.GetCurrentProcess().Id.ToString());
            Thread.Sleep(30000);
            return 0;
        }
        if (command == "mdns services")
        {
            if (!File.Exists("mdns.txt")) return 3;
            Console.Write(File.ReadAllText("mdns.txt"));
        }
        else if (command == "devices -l")
        {
            Console.WriteLine("List of devices attached\nUSB123 device model:TCL");
            if (File.Exists("connected")) Console.WriteLine("adb-TCL-aa._adb-tls-connect._tcp device model:TCL");
        }
        else if (command == "-s USB123 shell getprop ro.build.version.sdk") Console.WriteLine("34");
        else if (command.StartsWith("-s USB123 shell cmd package resolve-activity ") && command.EndsWith("-p com.onsongapp.app"))
            Console.WriteLine("com.onsongapp.app/.MainActivity");
        else if (command.StartsWith("pair "))
        {
            File.AppendAllText("actions.txt", command + "\n");
            if (command != "pair 192.0.2.7:33002" || Console.ReadLine() != "123456") return 4;
            Console.WriteLine("Successfully paired to 192.0.2.7:33002");
        }
        else if (command.StartsWith("connect "))
        {
            File.AppendAllText("actions.txt", command + "\n");
            if (command != "connect 192.0.2.7:39999") return 5;
            File.WriteAllText("connected", "");
            Console.WriteLine("connected to 192.0.2.7:39999");
        }
        else return 6;
        return 0;
    }
}

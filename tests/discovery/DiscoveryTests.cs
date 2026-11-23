using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using PcOnSongBridge;

internal static class DiscoveryTests
{
    static void Require(bool ok, string failure) { if (!ok) throw new Exception(failure); }
    static void Main(string[] args)
    {
        if (args.Length != 0 && args[0] == "--live")
        {
            foreach (var item in NdiDiscovery.FindAsync(CancellationToken.None).GetAwaiter().GetResult())
                Console.WriteLine("Stream " + item.Name + " at " + item.Address);
            return;
        }
        CheckToolTrust();
        string service = "_ndi._tcp.local", host = "Android.local", name = "LOCALHOST (Band.Τάμπλετ)";
        string full = name + "." + service;
        var items = new List<NdiSource>();
        var ptr = new NdiDiscovery.Record { Type = 12, Owner = service, Target = full };
        var srv = new NdiDiscovery.Record { Type = 33, Owner = full, Port = 5962, Target = host };
        var a = new NdiDiscovery.Record { Type = 1, Owner = host, Address = IPAddress.Parse("192.168.2.21") };
        var a6 = new NdiDiscovery.Record { Type = 28, Owner = host, Address = IPAddress.Parse("fe80::1") };
        NdiDiscovery.Apply(items, new[] { a6, a, srv, ptr });
        NdiDiscovery.Apply(items, new[] { ptr, srv, a });
        Require(items.Single().Name == name && items[0].Address == "192.168.2.21:5962", "Select advertised service address and port.");
        var second = new NdiDiscovery.Record { Type = 1, Owner = host, Address = IPAddress.Parse("192.168.2.90") };
        NdiDiscovery.Apply(items, new[] { second }); Require(items.Count == 1, "Do not reuse an unrelated address from another reply.");
        ptr.Removed = true; NdiDiscovery.Apply(items, new[] { ptr }); Require(items.Count == 0, "Honor stream departure.");
        ptr.Removed = false; NdiDiscovery.Apply(items, new[] { srv, ptr, a6 });
        Require(items.Single().Address == "Android.local:5962", "Use scoped system resolution for link-local IPv6.");
        try { NdiDiscovery.FindAsync(new CancellationToken(true)).GetAwaiter().GetResult(); throw new Exception("Expected cancellation"); }
        catch (OperationCanceledException) {}
        Console.WriteLine("PASS Windows DNS-SD service resolution, Unicode names, published ports, IPv6, deduplication, departure and cancellation.");
    }

    private static void CheckToolTrust()
    {
        foreach (string path in new[] { null, "", @".\tools", @"\tools", @"C:tools", @"\\example.invalid\tools", @"\\?\C:\tools" })
            Require(!WindowsToolTrust.IsLocalDrivePath(path), "Automatic lookup accepted a relative, UNC or device path.");
        var folder = Path.Combine(Path.GetTempPath(), "OnSong-TrustCheck-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string name = Environment.Is64BitProcess ? "Processing.NDI.Lib.x64.dll" : "Processing.NDI.Lib.x86.dll";
        string unsigned = Path.Combine(folder, name);
        string oldOverride = Environment.GetEnvironmentVariable("NDI_RUNTIME_DIR_V6");
        try
        {
            File.Copy(typeof(DiscoveryTests).Assembly.Location, unsigned);
            using (var check = WindowsToolTrust.OpenTrustedNdiLibrary(unsigned))
                Require(check == null, "Unsigned archive fixture accepted as an NDI runtime.");
            Environment.SetEnvironmentVariable("NDI_RUNTIME_DIR_V6", folder);
            string runtime = NdiRuntime.FindLibrary();
            Require(runtime == null || !String.Equals(runtime, unsigned, StringComparison.OrdinalIgnoreCase),
                "Unsigned environment override hid the official runtime.");
            if (runtime != null)
            {
                using (var check = WindowsToolTrust.OpenTrustedNdiLibrary(runtime))
                    Require(check != null, "Installed signed runtime did not revalidate.");
                Console.WriteLine("PASS Authenticode verification on the installed NDI 6 runtime.");
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("NDI_RUNTIME_DIR_V6", oldOverride);
            File.Delete(unsigned);
            Directory.Delete(folder);
        }
        Console.WriteLine("PASS local tool paths and rejection of an unsigned runtime override without loading it.");
    }
}

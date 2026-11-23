using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace PcOnSongBridge
{
    internal static class NdiDiscovery
    {
        private const string Service = "_ndi._tcp.local";

        internal static async Task<List<NdiSource>> FindAtAddressAsync(string tabletIp, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            IPAddress ip;
            string text = (tabletIp ?? "").Trim();
            if (!IPAddress.TryParse(text, out ip) || ip.AddressFamily != AddressFamily.InterNetwork ||
                ip.ToString() != text || IPAddress.IsLoopback(ip) || !OnLocalNetwork(ip))
                throw new ArgumentException("Enter the tablet’s Wi-Fi IPv4 address on the same network as this PC.");
            string runtime = NdiRuntime.FindLibrary();
            if (runtime == null) throw new InvalidOperationException("Set up Wi-Fi playback before connecting by address.");
            using (var receiver = new NdiReceiver(runtime))
                return await receiver.DiscoverAtAddressAsync(text, token).ConfigureAwait(false);
        }

        private static bool OnLocalNetwork(IPAddress target)
        {
            byte[] ip = target.GetAddressBytes();
            if (ip[0] == 0 || ip[0] >= 224) return false;
            foreach (var network in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up))
            foreach (var local in network.GetIPProperties().UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork))
            {
                if (local.IPv4Mask == null || local.Address.Equals(target)) continue;
                var own = local.Address.GetAddressBytes();
                var mask = local.IPv4Mask.GetAddressBytes();
                bool subnet = true, networkId = true, broadcast = true;
                for (int i = 0; i < 4; i++)
                {
                    if ((own[i] & mask[i]) != (ip[i] & mask[i])) subnet = false;
                    if ((ip[i] & ~mask[i]) != 0) networkId = false;
                    if ((ip[i] | mask[i]) != 255) broadcast = false;
                }
                if (subnet && !networkId && !broadcast) return true;
            }
            return false;
        }

        internal static async Task<List<NdiSource>> FindAsync(CancellationToken token)
        {
            var lan = Task.Run(() => BrowseWindows(token), token);
            var native = FindWithRuntimeAsync(token);
            await Task.WhenAll(lan, native).ConfigureAwait(false);
            var sources = new List<NdiSource>(lan.Result);
            foreach (var source in native.Result)
            {
                // Windows also provides an address when Android advertises its NDI host name as LOCALHOST.
                if (!sources.Any(found => String.Equals(found.Name, source.Name, StringComparison.OrdinalIgnoreCase) &&
                    (String.IsNullOrWhiteSpace(source.Address) || String.Equals(found.Address, source.Address, StringComparison.OrdinalIgnoreCase))))
                    sources.Add(source);
            }
            return sources.OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        private static async Task<List<NdiSource>> FindWithRuntimeAsync(CancellationToken token)
        {
            string path = NdiRuntime.FindLibrary();
            if (path != null)
            {
                try
                {
                    using (var receiver = new NdiReceiver(path))
                        return await receiver.DiscoverAsync(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception) { /* Windows can still discover devices with a missing or incompatible runtime. */ }
            }
            return new List<NdiSource>();
        }

        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        private delegate void BrowseCallback(uint status, IntPtr context, IntPtr records);
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct BrowseRequest
        {
            internal uint Version, InterfaceIndex;
            [MarshalAs(UnmanagedType.LPWStr)] internal string QueryName;
            internal BrowseCallback Callback;
            internal IntPtr Context;
        }
        [StructLayout(LayoutKind.Sequential)]
        private struct RecordHeader
        {
            internal IntPtr Next, Owner;
            internal ushort Type, Length;
            internal uint Flags, Ttl, Reserved;
        }
        [DllImport("dnsapi.dll")] private static extern uint DnsServiceBrowse(ref BrowseRequest request, ref IntPtr cancel);
        [DllImport("dnsapi.dll")] private static extern uint DnsServiceBrowseCancel(ref IntPtr cancel);
        [DllImport("dnsapi.dll")] private static extern void DnsFree(IntPtr ptr, int freeType);
        private static readonly BrowseCallback OnRecords = Receive;
        private static readonly Dictionary<IntPtr, List<NdiSource>> Searches = new Dictionary<IntPtr, List<NdiSource>>();
        private static int sequence;

        private static List<NdiSource> BrowseWindows(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var sources = new List<NdiSource>();
            var id = new IntPtr(Interlocked.Increment(ref sequence));
            var request = new BrowseRequest { Version = 1, QueryName = Service, Callback = OnRecords, Context = id };
            lock (Searches) Searches.Add(id, sources);
            IntPtr cancel = IntPtr.Zero;
            try
            {
                uint code = DnsServiceBrowse(ref request, ref cancel);
                if (code == 9506) // DNS_REQUEST_PENDING
                {
                    // An Android sender may advertise only when Go Live is tapped. Keep the first
                    // empty search open long enough to restart sharing, but return promptly on discovery.
                    try
                    {
                        var elapsed = Stopwatch.StartNew();
                        long firstResult = -1;
                        while (elapsed.ElapsedMilliseconds < 12000 && !token.IsCancellationRequested)
                        {
                            lock (Searches)
                                if (sources.Count > 0 && firstResult < 0) firstResult = elapsed.ElapsedMilliseconds;
                            if (firstResult >= 0 && elapsed.ElapsedMilliseconds - firstResult >= 420) break;
                            token.WaitHandle.WaitOne(60);
                        }
                    }
                    finally { DnsServiceBrowseCancel(ref cancel); }
                }
                token.ThrowIfCancellationRequested();
            }
            catch (EntryPointNotFoundException) { /* Older Windows builds can use runtime discovery. */ }
            finally { lock (Searches) Searches.Remove(id); }
            return sources;
        }

        internal sealed class Record
        {
            internal int Type, Port;
            internal string Owner, Target;
            internal IPAddress Address;
            internal bool Removed;
        }

        // dnsapi delivers a DNS-SD service's PTR, SRV and associated address records together.
        // Use the addresses in that result: a second device on the LAN can also call itself Android.local.
        internal static void Apply(List<NdiSource> sources, IList<Record> records)
        {
            var cmp = StringComparison.OrdinalIgnoreCase;
            string suffix = "." + Service;
            foreach (var ptr in records.Where(r => r.Type == 12 && String.Equals(r.Owner, Service, cmp) &&
                r.Target != null && r.Target.EndsWith(suffix, cmp)))
            {
                string name = ptr.Target.Substring(0, ptr.Target.Length - suffix.Length);
                if (String.IsNullOrWhiteSpace(name) || name.Any(Char.IsControl)) continue;
                if (ptr.Removed) { sources.RemoveAll(s => String.Equals(s.Name, name, cmp)); continue; }
                foreach (var srv in records.Where(r => r.Type == 33 && !r.Removed && r.Port > 0 && String.Equals(r.Owner, ptr.Target, cmp)))
                {
                    var ips = records.Where(r => r.Address != null && !r.Removed && String.Equals(r.Owner, srv.Target, cmp))
                        .Select(r => r.Address).Distinct().OrderBy(ip => ip.AddressFamily != AddressFamily.InterNetwork).ToArray();
                    string address = "";
                    if (ips.Length != 0 && !ips[0].IsIPv6LinkLocal) address = new IPEndPoint(ips[0], srv.Port).ToString();
                    else if (srv.Target != null && srv.Target.EndsWith(".local", cmp)) address = srv.Target + ":" + srv.Port;
                    if (address.Length == 0) continue;
                    if (!sources.Any(s => String.Equals(s.Name, name, cmp) && String.Equals(s.Address, address, cmp)))
                        sources.Add(new NdiSource(name, address));
                }
            }
        }

        private static void Receive(uint status, IntPtr context, IntPtr packet)
        {
            try
            {
                if (status != 0 || packet == IntPtr.Zero) return;
                var batch = new List<Record>();
                int offset = Marshal.SizeOf(typeof(RecordHeader));
                for (var p = packet; p != IntPtr.Zero && batch.Count < 2048;)
                {
                    var h = (RecordHeader)Marshal.PtrToStructure(p, typeof(RecordHeader));
                    var data = IntPtr.Add(p, offset);
                    var r = new Record { Type = h.Type, Owner = (Marshal.PtrToStringUni(h.Owner) ?? "").TrimEnd('.'),
                        Removed = h.Ttl == 0 || (h.Flags & 4) != 0 };
                    if ((h.Type == 12 && h.Length >= IntPtr.Size) || (h.Type == 33 && h.Length >= IntPtr.Size + 6))
                    {
                        r.Target = (Marshal.PtrToStringUni(Marshal.ReadIntPtr(data)) ?? "").TrimEnd('.');
                        if (h.Type == 33) r.Port = (ushort)Marshal.ReadInt16(data, IntPtr.Size + 4);
                    }
                    else if ((h.Type == 1 && h.Length == 4) || (h.Type == 28 && h.Length == 16))
                    {
                        var bytes = new byte[h.Length]; Marshal.Copy(data, bytes, 0, bytes.Length);
                        r.Address = new IPAddress(bytes);
                    }
                    batch.Add(r); p = h.Next;
                }
                lock (Searches)
                {
                    List<NdiSource> list;
                    if (Searches.TryGetValue(context, out list)) Apply(list, batch);
                }
            }
            finally { if (packet != IntPtr.Zero) DnsFree(packet, 1); }
        }
    }
}

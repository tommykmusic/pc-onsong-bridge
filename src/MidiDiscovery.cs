using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace PcOnSongBridge
{
    internal sealed class MidiPeer
    {
        internal readonly string Name, Address;
        internal readonly int Port;

        internal MidiPeer(string name, string address, int port)
        {
            if (port < 1 || port >= 65535) throw new ArgumentOutOfRangeException("port");
            IPAddress ip;
            if (!IPAddress.TryParse(address, out ip) || ip.AddressFamily != AddressFamily.InterNetwork ||
                ip.ToString() != address || ip.GetAddressBytes()[0] == 0 || ip.GetAddressBytes()[0] >= 224)
                throw new ArgumentException("Choose a tablet with a local IPv4 address.");
            Name = name ?? "";
            Address = address;
            Port = port;
        }

        public override string ToString() { return Name + " (" + Address + ":" + Port + ")"; }
    }

    internal static class MidiDiscovery
    {
        internal static Task<List<MidiPeer>> FindAsync(CancellationToken token)
        {
            return Task.Run(() =>
            {
                var result = new List<MidiPeer>();
                foreach (var service in NdiDiscovery.BrowseWindows("_apple-midi._udp.local", token))
                {
                    token.ThrowIfCancellationRequested();
                    int split = service.Address.LastIndexOf(':');
                    int port;
                    IPAddress ip;
                    if (split <= 0 || !int.TryParse(service.Address.Substring(split + 1), out port) ||
                        port < 1 || port >= 65535 || !IPAddress.TryParse(service.Address.Substring(0, split), out ip) ||
                        ip.AddressFamily != AddressFamily.InterNetwork || !NdiDiscovery.OnLocalNetwork(ip)) continue;
                    result.Add(new MidiPeer(service.Name, ip.ToString(), port));
                }
                return result.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            }, token);
        }
    }
}

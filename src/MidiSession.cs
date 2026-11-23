using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace PcOnSongBridge
{
    // Apple's network MIDI session protocol, with RFC 6295 short command lists.
    // Each accepted voice command is one datagram. Replaying an uncertain chart turn is unsafe.
    internal sealed class MidiSession : IDisposable
    {
        private readonly object gate = new object();
        private readonly Socket control, data;
        private readonly uint token, ssrc;
        private uint peerSsrc;
        private ushort sequence;
        private bool invited, disposed;
        private volatile bool connected;
        private long lastRequest, lastResponse;
        internal bool IsConnected { get { return connected; } }

        private MidiSession(Socket control, Socket data)
        {
            this.control = control;
            this.data = data;
            var seeds = new byte[10];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(seeds);
            token = Read32(seeds, 0);
            ssrc = Read32(seeds, 4);
            sequence = (ushort)((seeds[8] << 8) | seeds[9]);
        }

        internal static Task<MidiSession> ConnectAsync(MidiPeer peer, CancellationToken cancellation)
        {
            if (peer == null) throw new ArgumentNullException("peer");
            return Task.Run(() =>
            {
                cancellation.ThrowIfCancellationRequested();
                var address = IPAddress.Parse(peer.Address);
                if (!IPAddress.IsLoopback(address) && !NdiDiscovery.OnLocalNetwork(address))
                    throw new InvalidOperationException("The MIDI tablet must be on the same local network as this PC.");
                var session = OpenPair(address, peer.Port);
                try
                {
                    session.invited = true;
                    session.peerSsrc = session.Invite(session.control, null, cancellation);
                    session.Invite(session.data, session.peerSsrc, cancellation);
                    session.Synchronize(cancellation);
                    cancellation.ThrowIfCancellationRequested();
                    session.connected = true;
                    Task.Run((Action)session.Maintain);
                    return session;
                }
                catch { session.Dispose(); throw; }
            }, cancellation);
        }

        private static MidiSession OpenPair(IPAddress peer, int port)
        {
            for (int attempt = 0; attempt < 24; attempt++)
            {
                Socket first = null, second = null;
                try
                {
                    first = OpenSocket();
                    first.Connect(peer, port);
                    var local = (IPEndPoint)first.LocalEndPoint;
                    if (local.Port == 65535) continue;
                    second = OpenSocket();
                    second.Bind(new IPEndPoint(local.Address, local.Port + 1));
                    second.Connect(peer, port + 1);
                    var session = new MidiSession(first, second);
                    first = second = null;
                    return session;
                }
                catch (SocketException e)
                {
                    if (e.SocketErrorCode != SocketError.AddressAlreadyInUse && e.SocketErrorCode != SocketError.AccessDenied) throw;
                }
                finally
                {
                    if (first != null) first.Dispose();
                    if (second != null) second.Dispose();
                }
            }
            throw new IOException("Windows could not open a consecutive pair of local MIDI ports.");
        }

        private static Socket OpenSocket()
        {
            return new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
                { ExclusiveAddressUse = true, SendTimeout = 500 };
        }

        private uint Invite(Socket port, uint? expectedPeer, CancellationToken cancellation)
        {
            byte[] invitation = SessionPacket('I', 'N', true);
            byte[] reply = new byte[65535];
            var elapsed = Stopwatch.StartNew();
            long nextSend = 0;
            while (elapsed.ElapsedMilliseconds < 12000)
            {
                cancellation.ThrowIfCancellationRequested();
                if (elapsed.ElapsedMilliseconds >= nextSend)
                {
                    port.Send(invitation);
                    nextSend = elapsed.ElapsedMilliseconds + 1000;
                }
                if (!port.Poll(50000, SelectMode.SelectRead)) continue;
                int n = port.Receive(reply);
                if (n < 16 || Read32(reply, 4) != 2 || Read32(reply, 8) != token) continue;
                if (Command(reply, 'N', 'O'))
                {
                    if (port == control) invited = false;
                    throw new IOException("The tablet declined the MIDI invitation. Check OnSong’s Network Session.");
                }
                uint remoteId = Read32(reply, 12);
                if (Command(reply, 'O', 'K') && (!expectedPeer.HasValue || expectedPeer.Value == remoteId)) return remoteId;
            }
            throw new IOException("OnSong did not answer the MIDI invitation. Open Network Session on the tablet and try again.");
        }

        private static long Ticks()
        {
            return (long)(Stopwatch.GetTimestamp() * (10000.0 / Stopwatch.Frequency));
        }

        private void Synchronize(CancellationToken cancellation)
        {
            var buffer = new byte[65535];
            var elapsed = Stopwatch.StartNew();
            long nextSend = 0;
            while (elapsed.ElapsedMilliseconds < 5000)
            {
                cancellation.ThrowIfCancellationRequested();
                if (elapsed.ElapsedMilliseconds >= nextSend)
                {
                    RequestClock();
                    nextSend = elapsed.ElapsedMilliseconds + 600;
                }
                if (data.Poll(50000, SelectMode.SelectRead) && HandleClock(buffer, data.Receive(buffer))) return;
            }
            throw new IOException("The tablet accepted MIDI but did not synchronize its clock. Reconnect the network session.");
        }

        private void RequestClock()
        {
            lastRequest = Ticks();
            data.Send(ClockPacket(0, lastRequest, 0, 0));
        }

        private bool HandleClock(byte[] packet, int length)
        {
            if (length < 36 || !Command(packet, 'C', 'K') || Read32(packet, 4) != peerSsrc) return false;
            long first = Read64(packet, 12);
            if (packet[8] == 0) data.Send(ClockPacket(1, first, Ticks(), 0));
            if (packet[8] != 1 || first != lastRequest) return false;
            lastResponse = Ticks();
            data.Send(ClockPacket(2, first, Read64(packet, 20), lastResponse));
            return true;
        }

        private void Maintain()
        {
            var packet = new byte[65535];
            var ports = new List<Socket>(2);
            int requests = 0;
            long nextSync = Ticks() + 10000;
            try
            {
                while (connected)
                {
                    ports.Clear();
                    ports.Add(control);
                    ports.Add(data);
                    Socket.Select(ports, null, null, 200000);
                    foreach (var port in ports)
                    {
                        int n = port.Receive(packet);
                        if (n >= 16 && Command(packet, 'B', 'Y') && Read32(packet, 12) == peerSsrc) return;
                        if (port == data) HandleClock(packet, n);
                    }
                    long now = Ticks();
                    if (now - lastResponse >= 200000) return;
                    if (now >= nextSync)
                    {
                        RequestClock();
                        nextSync = now + (++requests < 3 ? 10000 : 50000);
                    }
                }
            }
            catch (SocketException) { }
            catch (ObjectDisposedException) { }
            finally { Dispose(); }
        }

        internal void SendControlChange(int channel, int controller, int value, CancellationToken cancellation)
        {
            if (channel < 1 || channel > 16) throw new ArgumentOutOfRangeException("channel");
            if (controller < 0 || controller > 127) throw new ArgumentOutOfRangeException("controller");
            if (value < 0 || value > 127) throw new ArgumentOutOfRangeException("value");
            lock (gate)
            {
                cancellation.ThrowIfCancellationRequested();
                if (!connected) throw new IOException("MIDI session disconnected. Reconnect the tablet before sending another command.");
                var packet = new byte[16];
                packet[0] = 0x80; packet[1] = 0xe1;
                packet[2] = (byte)(sequence >> 8); packet[3] = (byte)sequence++;
                Write32(packet, 4, (uint)Ticks()); Write32(packet, 8, ssrc);
                packet[12] = 3; // No journal, no initial delta time; three literal MIDI bytes.
                packet[13] = (byte)(0xb0 | (channel - 1)); packet[14] = (byte)controller; packet[15] = (byte)value;
                try { data.Send(packet); }
                catch (SocketException) { Dispose(); throw; }
            }
        }

        private byte[] SessionPacket(char a, char b, bool includeName)
        {
            byte[] name = includeName ? Encoding.UTF8.GetBytes("PC OnSong Bridge\0") : new byte[0];
            var packet = Header(a, b, 16 + name.Length);
            Write32(packet, 4, 2); Write32(packet, 8, token); Write32(packet, 12, ssrc);
            name.CopyTo(packet, 16);
            return packet;
        }

        private byte[] ClockPacket(byte count, long first, long second, long third)
        {
            var p = Header('C', 'K', 36);
            Write32(p, 4, ssrc); p[8] = count;
            Write64(p, 12, first); Write64(p, 20, second); Write64(p, 28, third);
            return p;
        }

        private static byte[] Header(char a, char b, int size)
        {
            var p = new byte[size]; p[0] = p[1] = 255; p[2] = (byte)a; p[3] = (byte)b; return p;
        }
        private static bool Command(byte[] p, char a, char b)
        {
            return p[0] == 255 && p[1] == 255 && p[2] == (byte)a && p[3] == (byte)b;
        }
        private static uint Read32(byte[] p, int i)
        {
            return ((uint)p[i] << 24) | ((uint)p[i + 1] << 16) | ((uint)p[i + 2] << 8) | p[i + 3];
        }
        private static void Write32(byte[] p, int i, uint v)
        {
            p[i] = (byte)(v >> 24); p[i + 1] = (byte)(v >> 16); p[i + 2] = (byte)(v >> 8); p[i + 3] = (byte)v;
        }
        private static long Read64(byte[] p, int i) { return ((long)Read32(p, i) << 32) | Read32(p, i + 4); }
        private static void Write64(byte[] p, int i, long v) { Write32(p, i, (uint)(v >> 32)); Write32(p, i + 4, (uint)v); }

        public void Dispose()
        {
            lock (gate)
            {
                if (disposed) return;
                disposed = true;
                connected = false;
                try { if (invited) control.Send(SessionPacket('B', 'Y', false)); }
                catch (SocketException) { }
                finally { control.Dispose(); data.Dispose(); }
            }
        }
    }
}

using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using PcOnSongBridge;

internal static class MidiTests
{
    private static void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
    private static uint U32(byte[] p, int i)
    {
        return ((uint)p[i] << 24) | ((uint)p[i + 1] << 16) | ((uint)p[i + 2] << 8) | p[i + 3];
    }
    private static byte[] Reply(byte[] request, char a, char b)
    {
        byte[] msg = request.Take(16).ToArray();
        msg[2] = (byte)a; msg[3] = (byte)b;
        msg[12] = 0x81; msg[13] = 0x23; msg[14] = 0x45; msg[15] = 0x67;
        return msg;
    }
    internal sealed class Tablet : IDisposable
    {
        internal readonly UdpClient Control, Data;
        internal readonly MidiPeer Peer;
        internal IPEndPoint PcControl, PcData;
        internal uint PcSsrc;
        internal byte[] FirstInvite;
        internal Tablet()
        {
            for (int i = 0; i < 24; i++)
            {
                Control = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                int port = ((IPEndPoint)Control.Client.LocalEndPoint).Port;
                try
                {
                    if (port < 65535)
                    {
                        Data = new UdpClient(new IPEndPoint(IPAddress.Loopback, port + 1));
                        Peer = new MidiPeer("Test tablet", "127.0.0.1", port);
                        Control.Client.ReceiveTimeout = Data.Client.ReceiveTimeout = 4000;
                        return;
                    }
                }
                catch (SocketException e) { if (e.SocketErrorCode != SocketError.AddressAlreadyInUse) throw; }
                Control.Close();
            }
            throw new IOException("Fixture could not allocate adjacent ports.");
        }
        internal void ControlInvite(bool accept)
        {
            FirstInvite = Control.Receive(ref PcControl);
            Check(FirstInvite.Length > 16 && FirstInvite[0] == 255 && FirstInvite[1] == 255 &&
                FirstInvite[2] == 'I' && FirstInvite[3] == 'N' && U32(FirstInvite, 4) == 2, "Invalid invitation.");
            PcSsrc = U32(FirstInvite, 12);
            var reply = Reply(FirstInvite, accept ? 'O' : 'N', accept ? 'K' : 'O');
            if (accept)
            {
                var spurious = (byte[])reply.Clone(); spurious[11] ^= 1;
                Control.Send(spurious, spurious.Length, PcControl);
            }
            Control.Send(reply, reply.Length, PcControl);
        }
        internal void DataInvite(bool accept)
        {
            var received = Data.Receive(ref PcData);
            Check(received.SequenceEqual(FirstInvite) && PcData.Port == PcControl.Port + 1, "Mismatched second invitation or port pair.");
            var reply = Reply(received, 'O', 'K');
            var wrong = (byte[])reply.Clone(); wrong[15] ^= 1;
            Data.Send(wrong, wrong.Length, PcData);
            if (accept) Data.Send(reply, reply.Length, PcData);
        }
        internal void Sync()
        {
            var ck = Data.Receive(ref PcData);
            Check(ck.Length == 36 && ck[0] == 255 && ck[1] == 255 && ck[2] == 'C' && ck[3] == 'K' &&
                ck[8] == 0 && U32(ck, 4) == PcSsrc, "Missing sync request.");
            var response = (byte[])ck.Clone();
            response[4] = 0x81; response[5] = 0x23; response[6] = 0x45; response[7] = 0x67;
            response[8] = 1; response[27] = 37;
            Data.Send(response, response.Length, PcData);
            var last = Data.Receive(ref PcData);
            Check(last.Length == 36 && last[8] == 2 && last.Skip(12).Take(16).SequenceEqual(response.Skip(12).Take(16)),
                "Clock synchronization did not finish.");
        }
        internal int ReceiveCC(int channel, int cc, int value)
        {
            var p = Data.Receive(ref PcData);
            Check(p.Length == 16 && p[0] == 0x80 && p[1] == 0xe1 && U32(p, 8) == PcSsrc && p[12] == 3 &&
                p[13] == 0xb0 + channel - 1 && p[14] == cc && p[15] == value, "Incorrect RTP-MIDI control-change message.");
            return p[2] * 256 + p[3];
        }
        internal void Bye()
        {
            var p = Control.Receive(ref PcControl);
            Check(p.Length == 16 && p[2] == 'B' && p[3] == 'Y' && U32(p, 8) == U32(FirstInvite, 8) &&
                U32(p, 12) == PcSsrc, "Session was not closed on the original peer.");
        }
        public void Dispose() { Control.Close(); Data.Close(); }
    }
    private static void Main()
    {
        using (var t = new Tablet())
        {
            var open = MidiSession.ConnectAsync(t.Peer, CancellationToken.None);
            t.ControlInvite(true); t.DataInvite(true); t.Sync();
            using (var s = open.GetAwaiter().GetResult())
            {
                Check(s.IsConnected, "Expected established session.");
                s.SendControlChange(16, 110, 127, CancellationToken.None);
                int seq = t.ReceiveCC(16, 110, 127);
                s.SendControlChange(16, 111, 127, CancellationToken.None);
                Check(t.ReceiveCC(16, 111, 127) == ((seq + 1) & 65535), "Invalid sequence progression.");
                try { s.SendControlChange(16, 110, 127, new CancellationToken(true)); throw new Exception("Sent after cancellation."); }
                catch (OperationCanceledException) { }
                t.Sync(); // First subsequent datagram must be the next clock exchange, with no replay.
                var bye = Reply(t.FirstInvite, 'B', 'Y');
                t.Control.Send(bye, bye.Length, t.PcControl);
                Check(SpinWait.SpinUntil(() => !s.IsConnected, 2000), "Peer disconnect was not detected.");
                try { s.SendControlChange(1, 2, 3, CancellationToken.None); throw new Exception("Sent on a closed session."); }
                catch (IOException) { }
            }
            t.Bye();
        }
        Console.WriteLine("PASS addressed two-port invitation, sync and keepalive, exactly one CC per command, cancellation and peer disconnect.");
        using (var t = new Tablet())
        using (var cancel = new CancellationTokenSource())
        {
            var open = MidiSession.ConnectAsync(t.Peer, cancel.Token);
            t.ControlInvite(true); t.DataInvite(false);
            cancel.Cancel();
            try { open.GetAwaiter().GetResult(); throw new Exception("Accepted a mismatched peer."); }
            catch (OperationCanceledException) { }
            t.Bye();
        }
        using (var t = new Tablet())
        {
            var open = MidiSession.ConnectAsync(t.Peer, CancellationToken.None);
            t.ControlInvite(false);
            try { open.GetAwaiter().GetResult(); throw new Exception("Ignored invitation rejection."); }
            catch (IOException) { }
            Check(!t.Control.Client.Poll(50000, SelectMode.SelectRead), "A declined peer received another control packet.");
            Check(!t.Data.Client.Poll(50000, SelectMode.SelectRead), "Data sent after rejected invitation.");
        }
        Console.WriteLine("PASS cancellation closes partial session, mismatched SSRC ignored, rejected invite sends no chart command.");
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Speech.AudioFormat;
using System.Speech.Synthesis;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using PcOnSongBridge;

internal static class ConnectTests
{
    private static readonly CancellationToken NoCancel = CancellationToken.None;
    private static readonly Uri Endpoint = ConnectSession.ParseAddress("10.20.30.40");
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static string Fails(Func<Task> action, string detail)
    {
        try { action().GetAwaiter().GetResult(); }
        catch (InvalidOperationException error) { Check(error.Message.Contains(detail), error.Message); return error.Message; }
        throw new Exception("Expected a refusal: " + detail);
    }

    [STAThread]
    private static void Main(string[] args)
    {
        Check(Endpoint.Host == "10.20.30.40" && Endpoint.Port == 5076 && Endpoint.AbsolutePath == "/api/_/", "Endpoint changed.");
        foreach (var bad in new[] { "8.8.8.8", "http://192.168.1.1", "192.168.1.1/path", "127.0.0.1", "192.168.1.1:0", "192.168.1.1:65536",
            "trusted.example", "192.168.01.1", "192.168.1.1@host", "192.168.1.1?#test" })
        {
            try { ConnectSession.ParseAddress(bad); throw new Exception("Accepted " + bad); } catch (ArgumentException) { }
        }
        var store = new ConnectTokenStore(Path.Combine(args[0], "Connect"));
        string token = store.GetOrCreate(Endpoint, "Fixture");
        Check(token.Length >= 32 && token == store.GetOrCreate(Endpoint, "Fixture") &&
            token != store.GetOrCreate(Endpoint, "Different tablet") &&
            token != store.GetOrCreate(ConnectSession.ParseAddress("10.20.30.41"), "Fixture"), "Pairing identity mismatch.");
        Check(Directory.GetFiles(Path.Combine(args[0], "Connect")).All(p => !Encoding.UTF8.GetString(File.ReadAllBytes(p)).Contains(token)),
            "Pairing token persisted in clear text.");
        Console.WriteLine("PASS private IPv4 address validation and DPAPI-protected tablet-specific tokens");
        CheckLoopbackRedirect(store);
        CheckPairingBodyOnWire(store);
        CheckAndroidAuthReadback(store);
        CheckRefusedCommands(store);

        var mock = new Tablet();
        using (var session = new ConnectSession(Endpoint, null, store, mock))
        {
            session.IdentifyAsync(NoCancel).Wait();
            Fails(() => session.AuthorizeAsync(NoCancel), "developer key");
            Fails(() => session.SendAsync("next", NoCancel), "Pair");
            Check(mock.AuthCalls == 0 && mock.Writes == 0, "Unauthenticated command or keyless pairing was attempted.");
        }
        mock = new Tablet();
        using (var session = new ConnectSession(Endpoint, "fixture-key", store, mock))
        {
            session.IdentifyAsync(NoCancel).Wait();
            Fails(() => session.AuthorizeAsync(NoCancel), "Approve");
            Fails(() => session.SendAsync("next", NoCancel), "Pair");
            mock.Accept = true;
            session.AuthorizeAsync(NoCancel).Wait();
            Check(session.Approved && mock.Registered && mock.Token == token, "Approved token not in use.");
            var first = session.SendAsync("previous", NoCancel).Result;
            Check(!first.ConfirmedAt.HasValue && first.Message.Contains("first") && mock.Writes == 0, "Wrapped at first song.");
            Check(session.SendAsync("next", NoCancel).Result.ConfirmedAt.HasValue && mock.Index == 1, "Next was not verified.");
            var last = session.SendAsync("next", NoCancel).Result;
            Check(!last.ConfirmedAt.HasValue && last.Message.Contains("last") && mock.Writes == 1, "Wrapped at last song.");
            mock.InlineSongs = false;
            Check(session.SendAsync("previous", NoCancel).Result.ConfirmedAt.HasValue && mock.Index == 0, "Ordered set lookup failed.");
            Check(session.SendAsync("scroll-down", NoCancel).Result.ConfirmedAt.HasValue && mock.Position == 0.2, "Scroll down failed.");
            Check(session.SendAsync("scroll-up", NoCancel).Result.ConfirmedAt.HasValue && mock.Position == 0.0, "Scroll up failed.");
            var top = session.SendAsync("scroll-up", NoCancel).Result;
            Check(!top.ConfirmedAt.HasValue && top.Message.Contains("top") && mock.Writes == 4, "Rewrote top position.");
            mock.Duplicate = true;
            Fails(() => session.SendAsync("next", NoCancel), "unique");
            mock.Duplicate = false;
            mock.IgnoreWrite = true;
            var unconfirmed = session.SendAsync("next", NoCancel).Result;
            Check(!unconfirmed.ConfirmedAt.HasValue && unconfirmed.Message.Contains("not confirmed") && mock.Writes == 5, "Unobserved move was reported or repeated.");
            mock.IgnoreWrite = false;
            CheckVoice(session, mock);
            mock.Revoked = true;
            string refusal = Fails(() => session.SendAsync("next", NoCancel), "did not accept the pairing token");
            Check(refusal.Contains("Pair with the tablet again"), "Missing re-pair instruction for a revoked token.");
            Check(!session.Approved, "Revoked token remained approved.");
            mock.Revoked = false;
            session.AuthorizeAsync(NoCancel).Wait();
            int oldAuth = mock.AuthCalls;
            mock.Name = "Changed";
            Fails(() => session.SendAsync("next", NoCancel), "different tablet");
            Check(!session.Approved && oldAuth == mock.AuthCalls, "Credentials sent to replacement before identity check.");
            mock.Redirect = true;
            var count = mock.Requests;
            Fails(() => session.IdentifyAsync(NoCancel), "redirected");
            Check(mock.Requests == count + 1, "Unexpected redirect retry.");
        }
        Console.WriteLine("PASS official headers, approval gate, duplicate and edge guards, confirmed next/previous/scroll, stale state and redirect handling");

        string originalKey = Environment.GetEnvironmentVariable(ConnectSession.KeyVariable);
        string settingsPath = @"Software\PCOnSongBridge-Test-" + Guid.NewGuid().ToString("N");
        var keyStore = new ConnectKeyStore(settingsPath);
        try
        {
            // Mask any real user-level key with an invalid, process-local fixture value.
            Environment.SetEnvironmentVariable(ConnectSession.KeyVariable, "invalid fixture key");
            using (var form = new ConnectVoiceForm("192.168.1.2", null, keyStore))
            {
                form.ShowInTaskbar = false; form.Opacity = 0; form.Show();
                var controls = Descendants(form).ToArray();
                Check(controls.OfType<Button>().Single(b => b.Text == "Check tablet").Enabled &&
                    !controls.OfType<Button>().Single(b => b.Text == "Start listening").Enabled &&
                    !controls.OfType<Button>().Single(b => b.Text == "Pair with tablet").Enabled &&
                    controls.OfType<Button>().Any(b => b.Text == "Developer key…" && b.Enabled) &&
                    controls.OfType<Label>().Any(l => l.Text.Contains("Choose Developer key")), "Missing key state inaccessible.");
                form.Close();
            }
            using (var dialog = new ConnectKeyForm(false, keyStore))
            {
                dialog.ShowInTaskbar = false; dialog.Opacity = 0; dialog.Show();
                var field = Descendants(dialog).OfType<TextBox>().Single();
                var save = Descendants(dialog).OfType<Button>().Single(b => b.Text == "Save key");
                Check(field.UseSystemPasswordChar && !save.Enabled, "Key entry should start empty and masked.");
                field.Text = "invalid fixture key";
                Check(!save.Enabled, "Invalid key accepted.");
                field.Text = "fixture-local-key";
                save.PerformClick();
                Check(dialog.DialogResult == DialogResult.OK && ConnectSession.ConfiguredKey(keyStore) == "fixture-local-key",
                    "Saved key failed to round trip or override the environment fallback.");
            }
            CheckMicrophoneHints(keyStore);
            CheckHandsFreeToggle(keyStore, store);
            using (var stored = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(settingsPath, true))
            {
                byte[] encrypted = (byte[])stored.GetValue("DeveloperKey");
                Check(!Encoding.UTF8.GetString(encrypted).Contains("fixture-local-key"), "Key stored in clear.");
                stored.SetValue("DeveloperKey", new byte[] { 1, 2, 3 });
            }
            Check(keyStore.Read() == null, "Corrupt storage should allow a replacement key.");
        }
        finally
        {
            Microsoft.Win32.Registry.CurrentUser.DeleteSubKey(settingsPath, false);
            Environment.SetEnvironmentVariable(ConnectSession.KeyVariable, originalKey);
        }
        CheckCommandFeedback();
        Console.WriteLine("PASS keyless dialog, microphone input feedback, confirmed timing and unchanged-chart boundaries");
    }

    private static void CheckMicrophoneHints(ConnectKeyStore keyStore)
    {
        using (var form = new ConnectVoiceForm("192.168.1.2", null, keyStore))
        {
            form.ShowInTaskbar = false; form.Opacity = 0; form.Show();
            var controls = Descendants(form).ToArray();
            var hint = controls.OfType<Label>().Single(l => l.AccessibleName == "Microphone availability");
            var status = controls.OfType<Label>().Single(l => l.AccessibleName != null && l.AccessibleName.StartsWith("Wi-Fi voice control."));
            var picker = controls.OfType<ComboBox>().Single();
            var devices = new List<AudioDevice> { new AudioDevice(1, "Laptop"), new AudioDevice(2, "Interface") };
            string pairingMessage = status.Text;
            form.SetSpeechInputs(devices, null, null);
            Check(hint.Visible && hint.Text.Contains("no English desktop speech recognizer"), "Missing recognizer hint.");
            var recognizer = Audio.FindEnglishRecognizer();
            if (recognizer != null)
            {
                form.SetSpeechInputs(new List<AudioDevice>(), recognizer, null);
                Check(hint.Visible && hint.Text.Contains("No Windows recording input"), "Missing microphone hint after recognizer refresh.");
                form.SetSpeechInputs(devices, recognizer, null);
                Check(hint.Visible && hint.Text.Contains("Choose your command microphone"), "Missing microphone selection hint.");
                picker.SelectedIndex = 1;
                Check(!hint.Visible && status.Text == pairingMessage, "Selecting a microphone retained an obsolete hint or overwrote pairing status.");
                form.SetSpeechInputs(devices, recognizer, devices[1]);
                Check(!hint.Visible && picker.SelectedItem == devices[1], "Refreshing lost the usable microphone.");
            }
            form.Close();
        }
    }

    private static async Task WaitFor(Func<bool> ready, string message)
    {
        var timer = Stopwatch.StartNew();
        while (!ready() && timer.ElapsedMilliseconds < 5000) await Task.Delay(10);
        Check(ready(), message);
    }

    private static void CheckHandsFreeToggle(ConnectKeyStore keyStore, ConnectTokenStore tokenStore)
    {
        var recognizer = Audio.FindEnglishRecognizer();
        if (recognizer == null) { Console.WriteLine("SKIP Wi-Fi voice toggle UI: no English SAPI engine"); return; }
        var tablet = new Tablet { AndroidAuth = true, Registered = true };
        var mic = new AudioDevice(1, "Talkback fixture");
        var phrases = new Queue<MicrophoneResult>();
        Action lastReady = null;
        int starts = 0, captures = 0;
        HandsFreeSession fixture = (device, engine, commands, ready, dispatch, noMatch, cancel) =>
        {
            Check(device == mic && engine == recognizer && commands.Length == 4, "Wrong Wi-Fi talkback configuration.");
            starts++;
            lastReady = ready;
            ready();
            return HandsFreeListening.RunAsync(async token =>
            {
                captures++;
                await Task.Yield();
                if (phrases.Count > 0) return phrases.Dequeue();
                await Task.Delay(Timeout.Infinite, token);
                throw new Exception("Canceled capture resumed.");
            }, dispatch, noMatch, cancel);
        };
        using (var form = new ConnectVoiceForm(Endpoint.Host, null, keyStore, fixture,
            (address, keys) => new ConnectSession(Endpoint, "fixture-key", tokenStore, tablet)))
        {
            Exception failure = null;
            form.ShowInTaskbar = false; form.Opacity = 0;
            form.Shown += async delegate
            {
                try
                {
                    form.SetSpeechInputs(new List<AudioDevice> { mic }, recognizer, mic);
                    var controls = Descendants(form).ToArray();
                    var pair = controls.OfType<Button>().Single(b => b.Text == "Pair with tablet");
                    var toggle = controls.OfType<Button>().Single(b => b.Text == "Start listening");
                    var status = controls.OfType<Label>().Single(l => l.AccessibleName != null && l.AccessibleName.StartsWith("Wi-Fi voice control."));
                    var hint = controls.OfType<Label>().Single(l => l.AccessibleName == "Microphone availability");
                    var hints = new List<string>();
                    hint.TextChanged += delegate { hints.Add(hint.Text); };
                    controls.OfType<Button>().Single(b => b.Text == "Check tablet").PerformClick();
                    await WaitFor(() => pair.Enabled, "Tablet check failed.");
                    pair.PerformClick();
                    await WaitFor(() => toggle.Enabled, "Fixture pairing failed.");
                    phrases.Enqueue(new MicrophoneResult(-12, "next", null, Stopwatch.GetTimestamp()));
                    phrases.Enqueue(new MicrophoneResult(-52, null));
                    phrases.Enqueue(new MicrophoneResult(Double.NegativeInfinity, null));
                    phrases.Enqueue(new MicrophoneResult(-12, "previous", null, Stopwatch.GetTimestamp()));
                    toggle.PerformClick();
                    await WaitFor(() => captures == 5 || toggle.Text == "Start listening", "One press did not rearm.");
                    Check(captures == 5, "Listening ended after " + captures + " reads: " + status.Text);
                    Check(toggle.Enabled && toggle.Text == "Stop listening" && starts == 1 && tablet.Writes == 2 && tablet.Index == 0 &&
                        hints.Any(s => s.Contains("very quiet")) && status.Text.Contains("previous song") && status.Text.Contains("confirmation received"),
                        "Wi-Fi talkback did not keep command feedback or return to the starting chart.");
                    toggle.PerformClick();
                    await WaitFor(() => toggle.Enabled && toggle.Text == "Start listening", "Pressing Stop did not rearm the toggle.");
                    lastReady();
                    Check(!hint.Visible && tablet.WriteAttempts == 2 && !status.Text.Contains("Sending"), "A late callback rearmed stopped listening.");

                    tablet.RefuseWriteStatus = 401;
                    phrases.Enqueue(new MicrophoneResult(-12, "next"));
                    toggle.PerformClick();
                    await WaitFor(() => pair.Enabled, "A permission refusal did not stop the listener and allow re-pairing.");
                    Check(!toggle.Enabled && toggle.Text == "Start listening" && tablet.WriteAttempts == 3 && status.Text.Contains("HTTP 401"),
                        "Refused write was retried or left the microphone enabled.");
                    pair.PerformClick();
                    await WaitFor(() => toggle.Enabled, "Fixture could not re-pair.");
                    toggle.PerformClick();
                    await WaitFor(() => starts == 3 && toggle.Text == "Stop listening", "Listener failed to start again.");
                }
                catch (Exception error) { failure = error; }
                finally { form.Close(); }
            };
            Application.Run(form);
            if (failure != null) throw new Exception("Wi-Fi voice toggle failed.", failure);
            Check(form.IsDisposed, "Close did not cancel and release the listener.");
            lastReady();
            Check(tablet.WriteAttempts == 3, "Closing issued a command.");
        }
        Console.WriteLine("PASS Wi-Fi voice toggle through next, quiet input, silence and previous, manual stop, refused write, restart and close");
    }

    private static void CheckCommandFeedback()
    {
        Check(Audio.NoCommandMessage(-15).StartsWith("No command recognized") &&
            Audio.NoCommandMessage(Double.NegativeInfinity).StartsWith("No microphone signal"), "Missing input feedback.");
        string quiet = Audio.NoCommandMessage(-52), muted = Audio.NoCommandMessage(-78);
        Check(quiet.Contains("very quiet") && quiet.Contains("dBFS"), "A quiet microphone did not report its level.");
        Check(muted.Contains("No usable microphone signal") && muted.Contains("dBFS"), "A noise-floor microphone did not warn about mute/connection.");
        foreach (double peak in new[] { -1.0, -0.5, 0.0 })
        {
            string high = Audio.NoCommandMessage(peak);
            Check(high.Contains("near full scale") && high.Contains("dBFS") && high.Contains("Lower the input gain"),
                "Near-full-scale input did not show its level and gain guidance.");
        }
        Check(!Audio.NoCommandMessage(-1.1).Contains("near full scale"), "High-level guidance appeared below its threshold.");
        long speechEnd = Stopwatch.GetTimestamp() - Stopwatch.Frequency / 5;
        var timedSample = new MicrophoneResult(-12, "next", null, speechEnd);
        var confirmed = new ConnectCommandResult("OnSong confirmed the song change.", speechEnd + Stopwatch.Frequency / 10);
        var timed = ConnectVoiceForm.FormatReply(timedSample, confirmed);
        Check(timed == "OnSong confirmed the song change. Tablet confirmation received 100 ms after speech ended.",
            "Use the tablet readback timestamp to measure confirmation.");
        foreach (var message in new[] { "Already at the last song.", "Command sent. OnSong has not confirmed the new position." })
        {
            var result = ConnectVoiceForm.FormatReply(timedSample, new ConnectCommandResult(message));
            Check(result == message, "An unchanged or unconfirmed chart displayed a timing figure.");
        }
    }
    private static void CheckLoopbackRedirect(ConnectTokenStore store)
    {
        var origin = new TcpListener(IPAddress.Loopback, 0);
        var destination = new TcpListener(IPAddress.Loopback, 0);
        origin.Start(); destination.Start();
        try
        {
            var local = (IPEndPoint)origin.LocalEndpoint;
            var redirect = (IPEndPoint)destination.LocalEndpoint;
            var target = new Uri("http://" + local + "/api/_/");
            var serve = Task.Run(() =>
            {
                using (var client = origin.AcceptTcpClient())
                using (var stream = client.GetStream())
                using (var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true))
                {
                    string line = reader.ReadLine();
                    Check(line.StartsWith("GET /api/_/ping "), "Unexpected wire request.");
                    while (!String.IsNullOrEmpty(line = reader.ReadLine()))
                        Check(line.IndexOf("Authorization:", StringComparison.OrdinalIgnoreCase) < 0 &&
                            line.IndexOf("X-OnSong-API-Key:", StringComparison.OrdinalIgnoreCase) < 0, "Credential on the wire before identification.");
                    var packet = Encoding.ASCII.GetBytes("HTTP/1.1 302 Found\r\nLocation: http://" + redirect +
                        "/api/_/ping\r\nConnection: close\r\nContent-Length: 0\r\n\r\n");
                    stream.Write(packet, 0, packet.Length);
                }
            });
            using (var session = new ConnectSession(target, "fixture-key", store, ConnectSession.LocalTransport()))
                Fails(() => session.IdentifyAsync(NoCancel), "redirected");
            serve.GetAwaiter().GetResult();
            Check(!destination.Pending(), "HTTP handler followed a redirect.");
            Console.WriteLine("PASS Windows HTTP transport rejected the loopback redirect without contacting its destination");
        }
        finally { destination.Stop(); origin.Stop(); }
    }

    private static IEnumerable<Control> Descendants(Control root)
    {
        foreach (Control child in root.Controls)
        {
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    private static void CheckPairingBodyOnWire(ConnectTokenStore store)
    {
        var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        try
        {
            var address = new Uri("http://" + server.LocalEndpoint + "/api/_/");
            Task serve = Task.Run(delegate
            {
                for (int i = 0; i < 4; i++)
                using (var socket = server.AcceptTcpClient())
                using (var stream = socket.GetStream())
                using (var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true))
                {
                    stream.ReadTimeout = 3000;
                    string request = reader.ReadLine();
                    string line;
                    int length = 0;
                    bool expect = false;
                    while (!String.IsNullOrEmpty(line = reader.ReadLine()))
                    {
                        if (line.StartsWith("Expect:", StringComparison.OrdinalIgnoreCase)) expect = true;
                        if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                            length = Int32.Parse(line.Substring("Content-Length:".Length).Trim());
                    }
                    string json;
                    if (i < 2) json = "{\"pong\":true,\"name\":\"Body fixture\",\"version\":\"2026.5\"}";
                    else if (i == 2) json = "{\"error\":\"Not Registered\"}";
                    else
                    {
                        Check(request.StartsWith("PUT /api/_/auth ") && !expect && length > 0 && length < 512,
                            "Pairing body required a preliminary HTTP exchange.");
                        var body = new char[length];
                        Check(reader.ReadBlock(body, 0, length) == length && new string(body).Contains("PC OnSong Bridge"),
                            "Pairing body missing.");
                        json = "{\"error\":\"Not Accepting Users\"}";
                    }
                    string status = i < 2 ? "200 OK" : "500 Internal Server Error";
                    var packet = Encoding.ASCII.GetBytes("HTTP/1.1 " + status + "\r\nConnection: close\r\nContent-Type: application/json\r\nContent-Length: " + json.Length + "\r\n\r\n" + json);
                    stream.Write(packet, 0, packet.Length);
                }
            });
            using (var session = new ConnectSession(address, "fixture-key", store, ConnectSession.LocalTransport()))
            {
                session.IdentifyAsync(NoCancel).GetAwaiter().GetResult();
                Fails(() => session.AuthorizeAsync(NoCancel), "Approve");
            }
            serve.GetAwaiter().GetResult();
            Console.WriteLine("PASS Windows HTTP pairing sends its JSON body directly and exposes the tablet approval response");
        }
        finally { server.Stop(); }
    }

    private static void CheckAndroidAuthReadback(ConnectTokenStore store)
    {
        var tablet = new Tablet { AndroidAuth = true };
        using (var session = new ConnectSession(Endpoint, "fixture-key", store, tablet))
        {
            session.IdentifyAsync(NoCancel).GetAwaiter().GetResult();
            session.AuthorizeAsync(NoCancel).GetAwaiter().GetResult();
            Check(session.Approved && tablet.AuthCalls == 1 && !tablet.Registered, "Android approval was not confirmed by GET alone.");
            foreach (object bad in new object[] { false, "true", 1 })
            {
                tablet.AndroidAuth = bad;
                Fails(() => session.AuthorizeAsync(NoCancel), "not approved");
                Check(!session.Approved, "Non-boolean approval accepted.");
            }
            tablet.AndroidAuth = true;
            tablet.AndroidStatus = 500;
            Fails(() => session.AuthorizeAsync(NoCancel), "HTTP 500");
            Check(!session.Approved && !tablet.Registered && tablet.Writes == 0, "Failed HTTP response approved the bridge.");
        }
        Console.WriteLine("PASS Android 2026.5 boolean auth readback and rejection of unapproved, malformed and non-success responses");
    }

    private static void CheckRefusedCommands(ConnectTokenStore store)
    {
        var tablet = new Tablet { AndroidAuth = true, Registered = true };
        using (var session = new ConnectSession(Endpoint, "fixture-key", store, tablet))
        {
            session.IdentifyAsync(NoCancel).GetAwaiter().GetResult();
            var denials = new[]
            {
                new { Code = 401, Reason = "Unauthorized", Known = true },
                new { Code = 403, Reason = "Forbidden", Known = true },
                new { Code = 403, Reason = "fixture-key {token}", Known = false },
                new { Code = 403, Reason = "Private fixture set name", Known = false },
                new { Code = 401, Reason = "", Known = false }
            };
            foreach (var denial in denials)
            {
                session.AuthorizeAsync(NoCancel).GetAwaiter().GetResult();
                Check(session.Approved, "A successful pairing check did not enable control.");
                tablet.RefuseWriteStatus = denial.Code;
                tablet.RefuseWriteReason = denial.Reason.Replace("{token}", tablet.Token);
                int attempts = tablet.WriteAttempts;
                string message = Fails(() => session.SendAsync("next", NoCancel), "change the chart (HTTP " + denial.Code);
                Check(message.Contains("remote-control access") && !message.Contains("expired") &&
                    !message.Contains("fixture-key") && !message.Contains(tablet.Token) &&
                    !message.Contains("Private fixture") && (denial.Known
                        ? message.Contains("Server detail: " + denial.Reason + ".")
                        : !message.Contains("Server detail:")), "Incorrect permission feedback or disclosure of tablet details.");
                Check(tablet.WriteAttempts == attempts + 1 && !session.Approved && tablet.Index == 0 && tablet.Writes == 0,
                    "A denied chart change was retried, applied, or left control enabled.");
                int requests = tablet.Requests;
                Fails(() => session.SendAsync("next", NoCancel), "Pair with the tablet");
                Check(requests == tablet.Requests, "A denied session continued sending requests.");
            }
            session.AuthorizeAsync(NoCancel).GetAwaiter().GetResult();
            tablet.InlineSongs = false;
            tablet.RefuseSetRead = true;
            string setMessage = Fails(() => session.SendAsync("next", NoCancel), "read the current set (HTTP 403");
            Check(!session.Approved && tablet.WriteAttempts == denials.Length && !setMessage.Contains("fixture-key"),
                "An unreadable set leaked data or attempted navigation.");
        }
        Console.WriteLine("PASS registered Android token and successful state read followed by refused write or set read, with no retry or credential disclosure");
    }

    private static void CheckVoice(ConnectSession session, Tablet mock)
    {
        var recognizer = Audio.FindEnglishRecognizer();
        if (recognizer == null) { Console.WriteLine("SKIP synthetic voice: no English SAPI engine"); return; }
        using (var synth = new SpeechSynthesizer())
        {
            var voice = synth.GetInstalledVoices().FirstOrDefault(v => v.Enabled && v.VoiceInfo.Culture.Name == recognizer.Culture.Name);
            if (voice == null) { Console.WriteLine("SKIP synthetic voice: no matching English synthesis voice"); return; }
            synth.SelectVoice(voice.VoiceInfo.Name);
            var commands = VoiceCommands.ForMode(TabletControlMode.Automatic, null).ToArray();
            foreach (string id in new[] { "next", "scroll-down", "scroll-up", "previous" })
            using (var pcm = new MemoryStream())
            {
                synth.SetOutputToAudioStream(pcm, new SpeechAudioFormatInfo(16000, AudioBitsPerSample.Sixteen, AudioChannel.Mono));
                synth.Speak(commands.Single(c => c.Id == id).Phrase);
                synth.SetOutputToNull();
                var audio = new byte[6400].Concat(pcm.ToArray()).Concat(new byte[16000]).ToArray();
                var heard = Audio.AnalyzeRecording(audio, 16000, 1, recognizer, commands, NoCancel);
                int writes = mock.Writes;
                Check(heard.Command == id && session.SendAsync(heard.Command, NoCancel).Result.ConfirmedAt.HasValue && mock.Writes == writes + 1,
                    "Voice-to-Connect dispatch failed for " + id);
            }
        }
        Console.WriteLine("PASS four synthetic speech commands each changed the authenticated fixture exactly once");
    }

    private sealed class Tablet : HttpMessageHandler
    {
        internal string Name = "Fixture", Token;
        internal int Index, AuthCalls, Writes, Requests;
        internal double Position;
        internal bool Accept, Registered, Duplicate, IgnoreWrite, Redirect, Revoked, InlineSongs = true;
        internal object AndroidAuth;
        internal int AndroidStatus = 200;
        internal int RefuseWriteStatus, WriteAttempts;
        internal string RefuseWriteReason;
        internal bool RefuseSetRead;
        private readonly JavaScriptSerializer json = new JavaScriptSerializer();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            cancel.ThrowIfCancellationRequested(); Requests++;
            Check(request.RequestUri.Host == Endpoint.Host && request.RequestUri.Query == "" && !request.RequestUri.AbsoluteUri.Contains("fixture-key"), "Credential URL leak.");
            string path = request.RequestUri.AbsolutePath;
            if (request.Content != null) Check(request.Headers.ExpectContinue == false, "Body sent without the Android-compatible HTTP setting.");
            if (path == "/api/_/ping")
            {
                Check(request.Headers.Authorization == null && !request.Headers.Contains("X-OnSong-API-Key"), "Credentials leaked on identify.");
                if (Redirect) return new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("https://invalid.example") } };
                return Reply(new { pong = true, name = Name, version = "2026.5" });
            }
            AuthCalls++;
            Check(request.Headers.GetValues("X-OnSong-API-Key").Single() == "fixture-key" && request.Headers.Authorization.Scheme == "Bearer", "Incorrect OnSong headers.");
            string sent = request.Headers.Authorization.Parameter;
            if (Token == null) Token = sent;
            Check(sent == Token && sent.Length >= 32, "Wrong token.");
            if (path == "/api/_/auth" && request.Method == HttpMethod.Get)
            {
                if (AndroidAuth != null) return Reply(new { authenticated = AndroidAuth }, AndroidStatus);
                return Registered ? Reply(new { success = "Token Registered" }) : Reply(new { error = "Not Registered" }, 500);
            }
            if (path == "/api/_/auth" && request.Method == HttpMethod.Put)
            {
                if (!Accept) return Reply(new { error = "Not Accepting Users" }, 403);
                Registered = true; return Reply(new { success = "Token Accepted" });
            }
            Check(Registered, "State accessed before approval.");
            if (Revoked) return Reply(new { error = "Not Registered" }, 500);
            string[] ids = Duplicate ? new[] { "a", "a" } : new[] { "a", "b" };
            var songs = ids.Select(s => new { ID = s }).ToArray();
            if (path == "/api/_/sets/practice/songs" && request.Method == HttpMethod.Get)
            {
                if (RefuseSetRead) return new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent("<html>fixture-key</html>") };
                return Reply(songs);
            }
            Check(path == "/api/_/state", "Unexpected route " + path);
            if (request.Method == HttpMethod.Get)
                return Reply(new { song = new { ID = ids[Index] }, set = new { ID = "practice", songs = InlineSongs ? songs : null }, position = Position });
            Check(request.Method == HttpMethod.Post, "Unexpected write.");
            WriteAttempts++;
            if (RefuseWriteStatus != 0)
            {
                var denied = Reply(new { error = RefuseWriteReason }, RefuseWriteStatus);
                denied.Headers.WwwAuthenticate.Add(new System.Net.Http.Headers.AuthenticationHeaderValue(
                    "Bearer", "realm=\"Private fixture tablet\""));
                return denied;
            }
            var update = json.Deserialize<Dictionary<string, object>>(await request.Content.ReadAsStringAsync());
            Writes++;
            if (!IgnoreWrite)
            {
                if (update.ContainsKey("song")) Index = Array.IndexOf(ids, (string)((Dictionary<string, object>)update["song"])["ID"]);
                else Position = Convert.ToDouble(update["position"]);
            }
            return Reply(new { acknowledged = true });
        }

        private HttpResponseMessage Reply(object obj, int code = 200)
        {
            return new HttpResponseMessage((HttpStatusCode)code) { Content = new StringContent(json.Serialize(obj), Encoding.UTF8, "application/json") };
        }
    }
}

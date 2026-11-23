using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace PcOnSongBridge
{
    // OnSong 2026 header authentication and state API: onsongapp.com/developers/connect/api/
    internal sealed class ConnectSession : IDisposable
    {
        internal const string KeyVariable = "ONSONG_CONNECT_API_KEY";
        internal readonly Uri Endpoint;
        internal bool Approved { get; private set; }
        internal string TabletName { get; private set; }
        private readonly HttpClient http;
        private readonly string developerKey;
        private readonly ConnectTokenStore tokens;
        private readonly JavaScriptSerializer json = new JavaScriptSerializer { MaxJsonLength = 4 * 1024 * 1024, RecursionLimit = 40 };
        private readonly SemaphoreSlim commandLock = new SemaphoreSlim(1, 1);
        private string token;

        internal static Uri ParseAddress(string text)
        {
            var parts = (text ?? "").Trim().Split(':');
            IPAddress address;
            int port = 5076;
            if (parts.Length > 2 || !IPAddress.TryParse(parts[0], out address) || address.AddressFamily != AddressFamily.InterNetwork ||
                address.ToString() != parts[0] || (parts.Length == 2 && (!Int32.TryParse(parts[1], NumberStyles.None,
                    CultureInfo.InvariantCulture, out port) || port <= 0 || port > 65535)))
                throw new ArgumentException("Enter the tablet’s Wi-Fi IPv4 address, optionally followed by its OnSong Connect port.");
            var b = address.GetAddressBytes();
            if (!(b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) ||
                (b[0] == 169 && b[1] == 254)))
                throw new ArgumentException("Choose the tablet’s private Wi-Fi address on this local network.");
            return new UriBuilder("http", address.ToString(), port, "/api/_/").Uri;
        }

        internal static ConnectSession Open(string address, ConnectKeyStore keyStore = null)
        {
            var endpoint = ParseAddress(address);
            byte[] target = IPAddress.Parse(endpoint.Host).GetAddressBytes();
            var adapters = NetworkInterface.GetAllNetworkInterfaces().Where(n =>
                n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
                n.NetworkInterfaceType != NetworkInterfaceType.Tunnel);
            bool local = adapters.SelectMany(n => n.GetIPProperties().UnicastAddresses).Any(n =>
            {
                if (n.Address.AddressFamily != AddressFamily.InterNetwork || n.IPv4Mask == null) return false;
                byte[] ip = n.Address.GetAddressBytes(), mask = n.IPv4Mask.GetAddressBytes();
                return mask.Any(v => v != 0) && Enumerable.Range(0, 4).All(i => (ip[i] & mask[i]) == (target[i] & mask[i]));
            });
            if (!local) throw new InvalidOperationException("Join the tablet’s local network and check its Wi-Fi address.");
            return new ConnectSession(endpoint, ConfiguredKey(keyStore), new ConnectTokenStore(), LocalTransport());
        }

        internal static HttpClientHandler LocalTransport()
        {
            return new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false, UseCookies = false, UseDefaultCredentials = false };
        }

        internal static string ConfiguredKey(ConnectKeyStore keyStore = null)
        {
            string saved = (keyStore ?? new ConnectKeyStore()).Read();
            if (saved != null) return saved;
            string key = Environment.GetEnvironmentVariable(KeyVariable);
            if (String.IsNullOrWhiteSpace(key))
                key = Environment.GetEnvironmentVariable(KeyVariable, EnvironmentVariableTarget.User);
            return ConnectKeyStore.IsValid(key) ? key : null;
        }

        internal ConnectSession(Uri endpoint, string key, ConnectTokenStore store, HttpMessageHandler handler)
        {
            Endpoint = endpoint;
            developerKey = key;
            tokens = store;
            http = new HttpClient(handler) { BaseAddress = endpoint, Timeout = TimeSpan.FromSeconds(5), MaxResponseContentBufferSize = 4 * 1024 * 1024 };
        }

        internal async Task<string> IdentifyAsync(CancellationToken cancel)
        {
            var info = Map(await Request(HttpMethod.Get, "ping", null, false, "identify the tablet", cancel).ConfigureAwait(false));
            var name = Text(info, "name");
            var version = Text(info, "version");
            Version parsed;
            if (!info.ContainsKey("pong") || info["pong"] == null || Object.Equals(info["pong"], false) ||
                String.IsNullOrWhiteSpace(name) || name.Length > 128 || name.Any(Char.IsControl) ||
                !Version.TryParse(version, out parsed) || parsed.Major < 2026)
                throw new InvalidOperationException("This address did not identify an OnSong 2026 or newer tablet.");
            if (TabletName != null && TabletName != name)
            {
                Approved = false;
                throw new InvalidOperationException("A different tablet answered. Check the address and identify the tablet again.");
            }
            TabletName = name;
            return name + " · OnSong " + version;
        }

        internal async Task AuthorizeAsync(CancellationToken cancel)
        {
            Approved = false;
            if (TabletName == null) throw new InvalidOperationException("Check the tablet address first.");
            if (String.IsNullOrEmpty(developerKey)) throw new InvalidOperationException("This bridge build needs its OnSong developer key.");
            await IdentifyAsync(cancel).ConfigureAwait(false);
            token = tokens.GetOrCreate(Endpoint, TabletName);
            var auth = Map(await Request(HttpMethod.Get, "auth", null, true, "check pairing", cancel, true).ConfigureAwait(false));
            if (!IsRegistered(auth, true) && Text(auth, "error") == "Not Registered")
            {
                auth = Map(await Request(HttpMethod.Put, "auth", new { name = "PC OnSong Bridge" }, true, "request tablet approval", cancel, true).ConfigureAwait(false));
                if (Text(auth, "error") == "Not Accepting Users")
                    throw new InvalidOperationException("Approve PC OnSong Bridge on the tablet, then select Pair with tablet again.");
                if (!IsRegistered(auth, false)) throw new InvalidOperationException("OnSong did not approve this bridge. Check its developer key and the tablet’s approval prompt.");
                auth = Map(await Request(HttpMethod.Get, "auth", null, true, "check pairing", cancel, true).ConfigureAwait(false));
            }
            Approved = IsRegistered(auth, true);
            if (!Approved) throw new InvalidOperationException("OnSong has not approved this bridge. Check the tablet and its internet connection for key verification.");
        }

        private static bool IsRegistered(Dictionary<string, object> auth, bool readback)
        {
            if (auth.ContainsKey("error")) return false;
            // OnSong 2026.5 for Android confirms an existing pairing with a boolean on GET /auth.
            object confirmed;
            if (readback && auth.TryGetValue("authenticated", out confirmed))
                return Object.Equals(confirmed, true);
            string value = Text(auth, "success");
            return value == "Token Registered" || value == "Token Accepted";
        }

        internal async Task<string> SendAsync(string id, CancellationToken cancel)
        {
            if (!new[] { "next", "previous", "scroll-up", "scroll-down" }.Contains(id))
                throw new ArgumentException("This command is unavailable through OnSong Connect.");
            await commandLock.WaitAsync(cancel).ConfigureAwait(false);
            try
            {
                if (!Approved) throw new InvalidOperationException("Pair with the tablet before sending a command.");
                await IdentifyAsync(cancel).ConfigureAwait(false);
                var initial = Map(await Request(HttpMethod.Get, "state", null, true, "read the current song", cancel).ConfigureAwait(false));
                string song = ObjectId(initial, "song"), set = ObjectId(initial, "set");
                if (String.IsNullOrEmpty(song)) throw new InvalidOperationException("Open a song in OnSong first.");
                bool moveSong = id == "next" || id == "previous";
                string targetSong = song;
                double targetPosition = 0;
                object update;
                if (moveSong)
                {
                    if (String.IsNullOrWhiteSpace(set)) throw new InvalidOperationException("Open a set on the tablet to navigate songs in set order.");
                    var currentSet = Map(initial["set"]);
                    object list;
                    if (!currentSet.TryGetValue("songs", out list) || !(list is object[]))
                    {
                        if (set == "." || set == ".." || set.Length > 512) throw new InvalidOperationException("OnSong returned an invalid set identifier.");
                        list = await Request(HttpMethod.Get, "sets/" + Uri.EscapeDataString(set) + "/songs", null, true, "read the current set", cancel).ConfigureAwait(false);
                        var latest = Map(await Request(HttpMethod.Get, "state", null, true, "recheck the current song and set", cancel).ConfigureAwait(false));
                        if (ObjectId(latest, "set") != set || ObjectId(latest, "song") != song)
                            throw new InvalidOperationException("The tablet’s song changed while reading its set. Try the command again.");
                    }
                    var rows = list as object[];
                    if (rows == null) throw new InvalidOperationException("OnSong did not return the order of the current set.");
                    var ids = rows.Select(row => Text(Map(row), "ID")).ToArray();
                    if (ids.Count(s => s == song) != 1)
                        throw new InvalidOperationException("OnSong did not identify a unique position for this song in the set (it may appear more than once in the setlist).");
                    int next = Array.IndexOf(ids, song) + (id == "next" ? 1 : -1);
                    if (next < 0 || next >= ids.Length) return next < 0 ? "Already at the first song." : "Already at the last song.";
                    targetSong = ids[next];
                    if (String.IsNullOrWhiteSpace(targetSong) || ids.Count(s => s == targetSong) != 1)
                        throw new InvalidOperationException("The adjacent set entry has no unique song ID.");
                    update = new { song = new { ID = targetSong } };
                }
                else
                {
                    double initialPosition = Position(initial);
                    targetPosition = Math.Max(0, Math.Min(1, initialPosition + (id == "scroll-down" ? 0.2 : -0.2)));
                    if (Math.Abs(initialPosition - targetPosition) < 0.0001) return targetPosition == 0 ? "Already at the top." : "Already at the bottom.";
                    update = new { position = targetPosition };
                }
                // An interrupted write must not repeat a song change. Only read back after this point.
                await Request(HttpMethod.Post, "state", update, true, moveSong ? "change the chart" : "scroll the chart", cancel).ConfigureAwait(false);
                for (int i = 0; i < 3; i++)
                {
                    if (i > 0) await Task.Delay(170, cancel).ConfigureAwait(false);
                    var state = Map(await Request(HttpMethod.Get, "state", null, true, moveSong ? "confirm the chart change" : "confirm the scroll position", cancel).ConfigureAwait(false));
                    if (ObjectId(state, "song") == targetSong && ObjectId(state, "set") == set &&
                        (moveSong || Math.Abs(Position(state) - targetPosition) <= 0.025))
                        return moveSong ? "OnSong confirmed the song change." : "OnSong confirmed the scroll position.";
                }
                return "Command sent. OnSong has not confirmed the new position.";
            }
            finally { commandLock.Release(); }
        }

        private static double Position(Dictionary<string, object> state)
        {
            object value;
            double position;
            if (!state.TryGetValue("position", out value) || !Double.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture),
                NumberStyles.Float, CultureInfo.InvariantCulture, out position) || Double.IsNaN(position) || position < 0 || position > 1)
                throw new InvalidOperationException("OnSong did not report a usable scroll position.");
            return position;
        }

        private static string ObjectId(Dictionary<string, object> parent, string name)
        {
            object child;
            return parent.TryGetValue(name, out child) && child is Dictionary<string, object> ? Text(Map(child), "ID") : null;
        }

        private static Dictionary<string, object> Map(object value)
        {
            var map = value as Dictionary<string, object>;
            if (map == null) throw new InvalidOperationException("OnSong returned an unexpected response.");
            return map;
        }

        private static string Text(Dictionary<string, object> values, string key)
        {
            object value;
            return values.TryGetValue(key, out value) ? value as string : null;
        }

        private string SafeServerDetail(string reason, HttpResponseMessage response)
        {
            string detail = reason;
            if (String.IsNullOrWhiteSpace(detail) && response.Headers.WwwAuthenticate != null && response.Headers.WwwAuthenticate.Count > 0)
                detail = String.Join(", ", response.Headers.WwwAuthenticate.Select(h => h.ToString()));
            if (String.IsNullOrWhiteSpace(detail)) return null;
            detail = new string(detail.Trim().Where(c => !Char.IsControl(c)).ToArray());
            if (String.IsNullOrWhiteSpace(detail)) return null;
            if (!String.IsNullOrEmpty(developerKey) && detail.IndexOf(developerKey, StringComparison.OrdinalIgnoreCase) >= 0) return null;
            if (!String.IsNullOrEmpty(token) && detail.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0) return null;
            return detail.Length <= 80 ? detail : detail.Substring(0, 80) + "…";
        }

        private async Task<object> Request(HttpMethod method, string route, object body, bool authenticated, string operation, CancellationToken cancel,
            bool allowAuthError = false)
        {
            using (var request = new HttpRequestMessage(method, route))
            {
                if (authenticated)
                {
                    request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
                    request.Headers.Add("X-OnSong-API-Key", developerKey);
                }
                if (body != null)
                {
                    // Send these small JSON bodies directly. Android OnSong can reject the interim 100-continue exchange.
                    request.Headers.ExpectContinue = false;
                    request.Content = new StringContent(json.Serialize(body), Encoding.UTF8, "application/json");
                }
                using (var response = await http.SendAsync(request, cancel).ConfigureAwait(false))
                {
                    int status = (int)response.StatusCode;
                    if (status >= 300 && status < 400)
                        throw new InvalidOperationException("The tablet redirected the request. Check its local Wi-Fi address.");
                    bool accessDenied = status == 401 || status == 403;
                    if (accessDenied) Approved = false;
                    string bodyText = response.Content == null ? "" : await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    object value = null;
                    try { value = json.DeserializeObject(bodyText); }
                    catch (ArgumentException) { if (response.IsSuccessStatusCode) throw new InvalidOperationException("The tablet did not return valid OnSong information."); }
                    catch (InvalidOperationException) { if (response.IsSuccessStatusCode) throw new InvalidOperationException("The tablet did not return valid OnSong information."); }
                    var map = value as Dictionary<string, object>;
                    string reason = map == null ? null : Text(map, "error");
                    if (reason == "Not Registered" || reason == "Invalid Token Length" || reason == "No Token")
                    {
                        Approved = false;
                        if (allowAuthError && reason == "Not Registered") return value;
                        throw new InvalidOperationException("OnSong did not accept the pairing token for this request to " + operation +
                            " (HTTP " + status + "). Pair with the tablet again.");
                    }
                    if (allowAuthError && reason == "Not Accepting Users") return value;
                    if (accessDenied)
                    {
                        string detail = SafeServerDetail(reason, response);
                        throw new InvalidOperationException("OnSong refused permission to " + operation +
                            (status == 401 ? " (HTTP 401 Unauthorized)." : " (HTTP 403 Forbidden).") +
                            (detail == null ? "" : " Server detail: " + detail + ".") +
                            " Check remote-control access with OnSong before trying again.");
                    }
                    if (!response.IsSuccessStatusCode || (map != null && map.ContainsKey("error")))
                        throw new InvalidOperationException("OnSong declined the request to " + operation + " (HTTP " + status + ").");
                    return value;
                }
            }
        }

        public void Dispose() { http.Dispose(); commandLock.Dispose(); }
    }
}

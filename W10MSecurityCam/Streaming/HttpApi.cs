using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using W10MSecurityCam.Rtsp;

namespace W10MSecurityCam.Streaming
{
    internal sealed class HttpRequest
    {
        public string Method = "GET";
        public string Path = "/";
        public string Query = "";
        public Dictionary<string, string> Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public string Body = "";
        /// <summary>True when the request came over the USB cable (Microsoft's IP-over-USB shows up as 127.0.0.1).</summary>
        public bool FromUsb;
        /// <summary>The target as sent on the request line (the Digest response must name it).</summary>
        public string Target = "/";
        /// <summary>The client's IP address (wrong logins are counted per address).</summary>
        public string RemoteIp;
    }

    /// <summary>
    /// The small web interface, served on the log port (http://PHONE-IP:5001) and on the RTSP ports:
    ///   /, /camera    the settings page
    ///   /log          the app log (plain text) - only with the Log page switch on
    ///   /stats        graphs of the last 12 hours of status samples - only with the Stats page switch on
    ///   /status       health as JSON (for monitoring)
    ///   /snapshot.jpg the latest picture, when snapshots are turned on (needs the login)
    ///   /config       view (GET) or change (POST, form fields) settings - only over the USB cable
    ///   /camera       the settings page; /camera/state its JSON; POST /camera/set changes
    ///   /login        asks the browser for the login, then back to /camera
    /// Reading is open on the home network (view only); changes and the snapshot need the stream login, over USB too.
    /// Changes (POST) also need the X-Requested-With: W10MSecurityCam header and the phone's address as Host.
    /// </summary>
    internal sealed class HttpApi
    {
        public Func<string> GetLog = () => "";
        public Func<string> GetStatusJson = () => "{}";
        /// <summary>Returns a JPEG, or null when snapshots are off or not possible.</summary>
        public Func<Task<byte[]>> GetSnapshotJpeg;
        public Func<IDictionary<string, string>, Task<string>> ApplyConfig;
        public Func<string> GetConfig = () => "";
        public Func<string> GetCameraJson = () => "{}";
        public DigestAuth Auth;
        public Action<string> Log = _ => { };
        /// <summary>The phone's Network switches: the web page (every page and JSON), the log page and the stats page.</summary>
        public volatile bool WebEnabled = true, LogEnabled, StatsEnabled;
        /// <summary>The stats samples as JavaScript string literals, for the stats page.</summary>
        public Func<string> GetStatsRows = () => "";

        private static byte[] SwitchedOff(string what) =>
            Reply(403, "Forbidden", "text/plain", "The " + what + " is switched off. Switch it on on the phone (Network and login)" + (what == "web page" ? "" : " or on the settings page") + ".\n");

        public async Task<byte[]> HandleAsync(HttpRequest r)
        {
            try
            {
                string path = r.Path.TrimEnd('/');
                if (!WebEnabled) return SwitchedOff("web page");
                // The pages, the log and the JSON are view only without a login (as in W10MWebCam). Changes, the snapshot
                // and /login need the stream login, over USB too: any program or web page on the PC the phone is plugged
                // into can reach the USB port, so being on USB proves nothing. A login the browser sends must be right,
                // though: a wrong or outdated one gets a 401, so the browser renews the nonce (stale) or asks again instead
                // of resending it forever. Checked once per request: with qop each nonce count may only be used once.
                string authorization = Header(r, "Authorization");
                if (Auth != null && (authorization != null || path == "/login") && Auth.IsLockedOut(r.RemoteIp))
                    return Reply(429, "Too Many Requests", "text/plain", "Too many wrong logins - try again in 30 seconds\n", "Retry-After: 30\r\n");
                bool canWrite = false, stale = false;
                if (Auth != null && authorization != null && !(canWrite = Auth.Check(r.Method, r.Target, authorization, r.RemoteIp, out stale)))
                    return Reply(401, "Unauthorized", "text/plain", "Login required\n", Auth.Challenge(stale));
                if (path == "/log")
                    return LogEnabled ? Reply(200, "OK", "text/plain; charset=utf-8", GetLog(), "Cache-Control: no-store\r\n") : SwitchedOff("log page");

                if (path == "/status")
                    return Reply(200, "OK", "application/json", GetStatusJson());

                if (path == "/login")
                {
                    // Already logged in: straight back to the page. Otherwise the browser asks for the login, then goes back.
                    if (canWrite) return Reply(302, "Found", "text/plain", "", "Location: /camera\r\n");
                    return Reply(401, "Unauthorized", "text/html; charset=utf-8",
                        "<!doctype html><meta http-equiv='refresh' content='0;url=/camera'><p>Login needed. <a href='/login'>Try again</a></p>\n",
                        Auth != null ? Auth.Challenge(stale) : "");
                }

                if (path == "/snapshot.jpg" || path == "/snapshot")
                {
                    if (!canWrite) return Reply(401, "Unauthorized", "text/plain", "Login required\n", Auth != null ? Auth.Challenge(stale) : "");
                    var jpg = GetSnapshotJpeg != null ? await GetSnapshotJpeg() : null;
                    if (jpg == null) return Reply(404, "Not Found", "text/plain", "Snapshots are turned off (or the camera is not streaming)\n");
                    return Reply(200, "OK", "image/jpeg", jpg, "Cache-Control: no-store\r\n");
                }

                if (path == "" || path == "/camera" || path == "/camera/state" || path == "/camera/set")
                    return await HandleCameraAsync(r, path == "" ? "/camera" : path, canWrite, stale);

                if (path == "/config")
                {
                    if (!r.FromUsb) return Reply(403, "Forbidden", "text/plain", "Settings can only be changed over the USB cable\n");
                    if (r.Method == "POST" && ApplyConfig != null)
                    {
                        if (!canWrite) return Reply(401, "Unauthorized", "text/plain", "Login required\n", Auth != null ? Auth.Challenge(stale) : "");
                        string refused = RefuseForeignPost(r);
                        if (refused != null) return Reply(403, "Forbidden", "text/plain", refused);
                        var form = ParseForm(r.Body); // changes only in the body, never in the URL
                        // As on the web page: a login change needs the current password too ("current" field).
                        string note = CheckCurrentPassword(form, form);
                        form.Remove("current");
                        string result = form.Count > 0 ? await ApplyConfig(form) : "";
                        if (note != null) result = note + (result.Length > 0 ? "\n" + result : "");
                        return Reply(200, "OK", "text/plain; charset=utf-8", result + "\n\n" + GetConfig());
                    }
                    return Reply(200, "OK", "text/plain; charset=utf-8", GetConfig());
                }

                return Reply(404, "Not Found", "text/plain", "Not found. Try /camera, /status, /log, /stats, /snapshot.jpg\n");
            }
            catch (Exception ex)
            {
                Log("Web request " + r.Method + " " + r.Path + " failed: " + ex.Message); // the details stay in the log
                return Reply(500, "Internal Server Error", "text/plain", "Internal error, see the app log\n");
            }
        }

        /// <summary>
        /// The camera-settings page: view only without the login; canWrite = the request carried the right login
        /// (checked in HandleAsync), which /camera/set needs - a 401 makes the browser ask for it.
        /// </summary>
        private async Task<byte[]> HandleCameraAsync(HttpRequest r, string path, bool canWrite, bool stale)
        {
            string result = null;
            bool loginSet = Auth != null && Auth.Enabled;

            if (path == "/camera")
                return Reply(200, "OK", "text/html; charset=utf-8", CameraPage.Html, "Cache-Control: no-store\r\n");

            if (path == "/camera/set")
            {
                if (r.Method != "POST") return Reply(405, "Method Not Allowed", "text/plain", "POST only\n");
                if (!canWrite)
                    return Reply(401, "Unauthorized", "text/plain", "Login needed\n", Auth != null ? Auth.Challenge(stale) : "");
                string refused = RefuseForeignPost(r);
                if (refused != null) return Reply(403, "Forbidden", "text/plain", refused);
                var all = ParseForm(r.Body); // changes only in the body, never in the URL
                var only = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var k in new[] { "camera", "focus", "exposure", "wb", "format", "bitrate", "highbw", "powersave", "stream", "engine", "user", "password", "savepower", "saveois", "savefocus", "probe", "torch", "log", "stats" })
                    if (all.TryGetValue(k, out var v)) only[k] = v;
                string bad = CheckLoginFields(only) ?? CheckCurrentPassword(only, all);
                if (bad != null) result = bad;
                else if (ApplyConfig != null && only.Count > 0) result = await ApplyConfig(only);
            }
            string json = GetCameraJson();
            if (result != null)
            {
                string first = result.Split('\n')[0].Replace("\\", "\\\\").Replace("\"", "\\\"");
                json = json.TrimEnd('}') + ",\"result\":\"" + first + "\"}";
            }
            if (json.StartsWith("{")) json = "{\"canWrite\":" + (canWrite ? "true" : "false") + ",\"loginSet\":" + (loginSet ? "true" : "false")
                + ",\"fromUsb\":" + (r.FromUsb ? "true" : "false")
                + ",\"defaultLogin\":" + (Auth != null && Auth.IsDefault ? "true" : "false") + ",\"user\":\"" + (canWrite && Auth != null ? Auth.UserName.Replace("\\", "").Replace("\"", "") : "") + "\"" + (json.Length > 2 ? "," : "") + json.Substring(1);
            return Reply(200, "OK", "application/json", json, "Cache-Control: no-store\r\n");
        }

        /// <summary>Returns an error text when the user/password fields of a web change are not acceptable, otherwise null.</summary>
        private static string CheckLoginFields(IDictionary<string, string> f)
        {
            if (f.TryGetValue("user", out var u) && !System.Text.RegularExpressions.Regex.IsMatch(u ?? "", @"^[A-Za-z0-9._-]{1,32}$"))
            { f.Remove("user"); f.Remove("password"); return "Not changed: the user name may use letters, digits . _ - (1-32 characters)"; }
            if (f.TryGetValue("password", out var p) && (p == null || p.Length < DigestAuth.MinPasswordLength || p.Length > 64 || p.Any(ch => ch < ' ')))
            { f.Remove("user"); f.Remove("password"); return "Not changed: the password needs " + DigestAuth.MinPasswordLength + "-64 characters"; }
            return null;
        }

        public const string ClientHeader = "W10MSecurityCam";

        /// <summary>
        /// Null when a change request may go ahead, otherwise why it is refused. A web page on another site could make the
        /// browser send a POST with the saved login. Such a page cannot add a custom header without our permission (never
        /// granted), and a DNS-rebinding page would send its own host name instead of an IP address, so both checks
        /// together leave only this app's own page and the tools. Over USB too.
        /// </summary>
        private static string RefuseForeignPost(HttpRequest r)
        {
            if (Header(r, "X-Requested-With") != ClientHeader)
                return "Use the settings page (missing X-Requested-With header)\n";
            if (!HostIsAddress(Header(r, "Host"), r.FromUsb))
                return "Use the phone's address (http://127.0.0.1:8020 over USB, http://PHONE-IP:5001 over Wi-Fi)\n";
            return null;
        }

        /// <summary>The Host header is the phone's address: 127.0.0.1 / localhost over USB, an IP address over Wi-Fi.</summary>
        private static bool HostIsAddress(string host, bool fromUsb)
        {
            if (string.IsNullOrEmpty(host)) return true; // HTTP/1.0 clients may leave it out; browsers always send it
            string name = host;
            if (name.StartsWith("["))
            {
                int close = name.IndexOf(']');
                name = close > 0 ? name.Substring(1, close - 1) : name;
            }
            else
            {
                int colon = name.LastIndexOf(':');
                if (colon > 0) name = name.Substring(0, colon);
            }
            if (fromUsb) return name == "127.0.0.1" || name.Equals("localhost", StringComparison.OrdinalIgnoreCase);
            return System.Net.IPAddress.TryParse(name, out _);
        }

        /// <summary>
        /// A login change from the web page needs the current password as well ("current" field): the request itself is
        /// signed with it, but a signed request captured on the network could otherwise be replayed with a new password.
        /// </summary>
        private string CheckCurrentPassword(IDictionary<string, string> only, IDictionary<string, string> all)
        {
            if (!only.ContainsKey("user") && !only.ContainsKey("password")) return null;
            if (Auth != null && Auth.IsCurrentPassword(all.TryGetValue("current", out var c) ? c : null)) return null;
            only.Remove("user"); only.Remove("password");
            return "Not changed: enter the current password to change the login";
        }

        private static string Header(HttpRequest r, string name) => r.Headers.TryGetValue(name, out var v) ? v : null;

        public static Dictionary<string, string> ParseForm(string s)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in (s ?? "").Split(new[] { '&', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = pair.IndexOf('=');
                string k = eq < 0 ? pair : pair.Substring(0, eq);
                string v = eq < 0 ? "" : pair.Substring(eq + 1);
                d[Unescape(k.Trim())] = Unescape(v.TrimEnd('\r'));
            }
            return d;
        }

        private static string Unescape(string s) => Uri.UnescapeDataString(s.Replace('+', ' '));

        /// <summary>Parses "GET /path?query HTTP/1.1" plus headers into a request.</summary>
        public static HttpRequest Parse(string requestLine, Dictionary<string, string> headers, string body, bool fromUsb, string remoteIp)
        {
            var parts = requestLine.Split(' ');
            string target = parts.Length > 1 ? parts[1] : "/";
            string original = target;
            // absolute form (http://host:port/path) -> path
            int scheme = target.IndexOf("://", StringComparison.Ordinal);
            if (scheme >= 0)
            {
                int slash = target.IndexOf('/', scheme + 3);
                target = slash < 0 ? "/" : target.Substring(slash);
            }
            int q = target.IndexOf('?');
            return new HttpRequest
            {
                Method = parts[0].ToUpperInvariant(),
                Path = q < 0 ? target : target.Substring(0, q),
                Query = q < 0 ? "" : target.Substring(q + 1),
                Headers = headers,
                Body = body ?? "",
                FromUsb = fromUsb,
                Target = original,
                RemoteIp = remoteIp
            };
        }

        public static byte[] Reply(int code, string reason, string contentType, string body, string extraHeaders = "")
            => Reply(code, reason, contentType, Encoding.UTF8.GetBytes(body ?? ""), extraHeaders);

        public static byte[] Reply(int code, string reason, string contentType, byte[] body, string extraHeaders = "")
        {
            byte[] head = Encoding.ASCII.GetBytes(
                "HTTP/1.0 " + code + " " + reason + "\r\n" +
                "Content-Type: " + contentType + "\r\n" +
                "Content-Length: " + body.Length + "\r\n" +
                // no framing by other sites (clickjacking the settings page), no guessing of content types
                "X-Frame-Options: DENY\r\n" +
                "Content-Security-Policy: frame-ancestors 'none'\r\n" +
                "X-Content-Type-Options: nosniff\r\n" +
                extraHeaders +
                "Connection: close\r\n\r\n");
            var all = new byte[head.Length + body.Length];
            Buffer.BlockCopy(head, 0, all, 0, head.Length);
            Buffer.BlockCopy(body, 0, all, head.Length, body.Length);
            return all;
        }
    }
}

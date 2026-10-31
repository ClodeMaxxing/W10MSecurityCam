using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using Windows.Security.Credentials;
using Windows.Security.Cryptography;
using Windows.Security.Cryptography.Core;

namespace W10MSecurityCam.Rtsp
{
    /// <summary>
    /// Login for RTSP and the web pages. Uses HTTP Digest authentication (RFC 2617): qop="auth" is offered (browsers,
    /// ffmpeg), and clients without qop (VLC/live555, many NVRs) still work. Basic can be allowed too for older NVRs.
    /// Nonces are random and kept here for 5 minutes; each response must name the URL it was made for. After 5 wrong
    /// logins within a minute an address is refused for 30 s.
    /// The user name and password are kept in the Windows PasswordVault, not in app settings.
    /// The login is always on: a fresh install uses admin / admin (change it on the settings page or the phone).
    /// </summary>
    internal sealed class DigestAuth
    {
        public const string Realm = "W10MSecurityCam";
        private const string VaultResource = "W10MSecurityCam.RTSP";
        /// <summary>Shortest new password accepted (passwords stored before keep working).</summary>
        public const int MinPasswordLength = 8;
        private static readonly TimeSpan NonceLifetime = TimeSpan.FromMinutes(5);
        private const int MaxNonces = 256;
        private const int MaxFailures = 5;                                          // wrong logins from one address
        private static readonly TimeSpan FailureWindow = TimeSpan.FromSeconds(60);  // within this time
        private static readonly TimeSpan LockoutTime = TimeSpan.FromSeconds(30);    // refuse the address this long
        private const int MaxTrackedAddresses = 256;
        private static readonly Stopwatch Clock = Stopwatch.StartNew();

        private readonly object _lock = new object();
        private string _user, _password;

        private sealed class NonceInfo { public TimeSpan Issued; public long LastNc; }
        private readonly Dictionary<string, NonceInfo> _nonces = new Dictionary<string, NonceInfo>(StringComparer.Ordinal);
        private readonly Queue<string> _nonceOrder = new Queue<string>(); // oldest first, dropped when the table is full
        private sealed class Failures { public int Count; public TimeSpan First, LockedUntil; }
        private readonly Dictionary<string, Failures> _failures = new Dictionary<string, Failures>(StringComparer.Ordinal);

        /// <summary>Also accept Basic authentication (password sent unencrypted). Off by default.</summary>
        public bool AllowBasic;

        public Action<string> Log = _ => { };

        public const string DefaultUser = "admin";
        public const string DefaultPassword = "admin";

        public bool Enabled { get { lock (_lock) return !string.IsNullOrEmpty(_password); } }
        /// <summary>True while the factory login (admin / admin) is in use.</summary>
        public bool IsDefault { get { lock (_lock) return _user == DefaultUser && _password == DefaultPassword; } }
        public string UserName { get { lock (_lock) return _user ?? ""; } }

        public void Load()
        {
            try
            {
                var vault = new PasswordVault();
                var cred = vault.FindAllByResource(VaultResource).FirstOrDefault();
                if (cred != null)
                {
                    cred.RetrievePassword();
                    lock (_lock) { _user = cred.UserName; _password = cred.Password; }
                }
            }
            catch { /* FindAllByResource throws when nothing is stored */ }
            if (!Enabled)
            {
                try { Save(DefaultUser, DefaultPassword); } // first run: the factory login
                catch (Exception ex) { Log("Login: could not store the default login (" + ex.Message + ") - nobody can log in until a login is saved"); }
            }
        }

        /// <summary>Sets the login. An empty password resets it to the factory login (admin / admin); the login is never off.</summary>
        public void Save(string user, string password)
        {
            var vault = new PasswordVault();
            try { foreach (var c in vault.FindAllByResource(VaultResource).ToList()) vault.Remove(c); } catch { }
            user = (user ?? "").Trim();
            if (string.IsNullOrEmpty(password)) { user = DefaultUser; password = DefaultPassword; }
            if (user.Length == 0) user = DefaultUser;
            vault.Add(new PasswordCredential(VaultResource, user, password));
            lock (_lock) { _user = user; _password = password; }
        }

        /// <summary>The WWW-Authenticate header(s) for a 401 answer (each line ends with CRLF).</summary>
        public string Challenge(bool stale = false)
        {
            string nonce = CryptographicBuffer.EncodeToHexString(CryptographicBuffer.GenerateRandom(16)).ToLowerInvariant();
            lock (_lock)
            {
                while (_nonceOrder.Count >= MaxNonces) _nonces.Remove(_nonceOrder.Dequeue());
                _nonces[nonce] = new NonceInfo { Issued = Clock.Elapsed };
                _nonceOrder.Enqueue(nonce);
            }
            // live555 (VLC) reads realm, nonce and stale in exactly this order, so qop comes last.
            string h = "WWW-Authenticate: Digest realm=\"" + Realm + "\", nonce=\"" + nonce + "\"" + (stale ? ", stale=TRUE" : "") + ", qop=\"auth\"\r\n";
            if (AllowBasic) h += "WWW-Authenticate: Basic realm=\"" + Realm + "\"\r\n";
            return h;
        }

        /// <summary>True while an address is refused after too many wrong logins.</summary>
        public bool IsLockedOut(string remoteIp)
        {
            if (string.IsNullOrEmpty(remoteIp)) return false;
            lock (_lock) return _failures.TryGetValue(remoteIp, out var f) && Clock.Elapsed < f.LockedUntil;
        }

        /// <summary>
        /// True if the request may proceed. 'method', 'requestUri' (the target on the request line) and the Authorization
        /// header come from the request; wrong logins are counted per 'remoteIp'. 'stale': the login was right but the
        /// nonce is old or unknown, so the client should retry with the new one (no password prompt).
        /// </summary>
        public bool Check(string method, string requestUri, string authorization, string remoteIp, out bool stale)
        {
            stale = false;
            if (IsLockedOut(remoteIp) || string.IsNullOrEmpty(authorization)) return false;
            bool ok = Verify(method, requestUri, authorization, out stale);
            if (ok) { if (!string.IsNullOrEmpty(remoteIp)) lock (_lock) _failures.Remove(remoteIp); }
            else if (!stale) NoteFailure(remoteIp);
            return ok;
        }

        private bool Verify(string method, string requestUri, string authorization, out bool stale)
        {
            stale = false;
            string user, password;
            lock (_lock) { user = _user; password = _password; }
            if (string.IsNullOrEmpty(password)) return false; // no login stored (password vault error): nobody gets in

            if (authorization.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
            {
                if (!AllowBasic) return false;
                try
                {
                    string decoded = Encoding.UTF8.GetString(Convert.FromBase64String(authorization.Substring(6).Trim()));
                    int colon = decoded.IndexOf(':');
                    return colon >= 0 && FixedEquals(decoded.Substring(0, colon), user) && FixedEquals(decoded.Substring(colon + 1), password);
                }
                catch { return false; }
            }

            if (!authorization.StartsWith("Digest ", StringComparison.OrdinalIgnoreCase)) return false;
            var p = ParseParams(authorization.Substring(7));
            string pu, realm, nonce, uri, response;
            if (!p.TryGetValue("username", out pu) || !p.TryGetValue("realm", out realm) || !p.TryGetValue("nonce", out nonce) ||
                !p.TryGetValue("uri", out uri) || !p.TryGetValue("response", out response))
                return false;
            if (!FixedEquals(pu, user) || realm != Realm) return false;
            if (NormalizeUri(uri) != NormalizeUri(requestUri)) return false; // a response made for another URL
            if (nonce.Length == 0 || nonce.Length > 64 || !nonce.All(IsHex)) return false;

            string ha1 = Md5(user + ":" + Realm + ":" + password);
            string ha2 = Md5(method + ":" + uri);
            string expected;
            string qop;
            long nc = 0;
            if (p.TryGetValue("qop", out qop) && qop.Length > 0)
            {
                string ncText, cnonce;
                if (qop != "auth" || !p.TryGetValue("nc", out ncText) || !p.TryGetValue("cnonce", out cnonce) || ncText.Length > 8 ||
                    !long.TryParse(ncText, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out nc) || nc <= 0)
                    return false;
                expected = Md5(ha1 + ":" + nonce + ":" + ncText + ":" + cnonce + ":" + qop + ":" + ha2);
            }
            else
            {
                expected = Md5(ha1 + ":" + nonce + ":" + ha2); // RFC 2069 style: VLC/live555, many NVRs
            }
            if (!FixedEquals(expected, response.ToLowerInvariant())) return false;

            // The login is right; the nonce must be one we handed out, still fresh, and (with qop) its count must go up.
            lock (_lock)
            {
                NonceInfo n;
                if (!_nonces.TryGetValue(nonce, out n) || Clock.Elapsed - n.Issued > NonceLifetime) { stale = true; return false; }
                if (nc > 0)
                {
                    if (nc <= n.LastNc) { stale = true; return false; } // replayed (or overtaken by a parallel request)
                    n.LastNc = nc;
                }
            }
            return true;
        }

        private void NoteFailure(string remoteIp)
        {
            if (string.IsNullOrEmpty(remoteIp)) return;
            bool locked = false;
            lock (_lock)
            {
                var now = Clock.Elapsed;
                if (!_failures.TryGetValue(remoteIp, out var f))
                {
                    if (_failures.Count >= MaxTrackedAddresses)
                    {
                        foreach (var k in _failures.Where(kv => now >= kv.Value.LockedUntil && now - kv.Value.First > FailureWindow).Select(kv => kv.Key).ToList())
                            _failures.Remove(k);
                        if (_failures.Count >= MaxTrackedAddresses) return;
                    }
                    f = new Failures { First = now };
                    _failures[remoteIp] = f;
                }
                if (now - f.First > FailureWindow) { f.First = now; f.Count = 0; }
                if (++f.Count >= MaxFailures) { f.Count = 0; f.First = now; f.LockedUntil = now + LockoutTime; locked = true; }
            }
            if (locked) Log("Login: " + MaxFailures + " wrong logins from " + remoteIp + " - refusing it for " + (int)LockoutTime.TotalSeconds + " s");
        }

        /// <summary>Path and query of a request target, without scheme://host[:port] and a trailing '/'.</summary>
        internal static string NormalizeUri(string u)
        {
            u = u ?? "";
            int scheme = u.IndexOf("://", StringComparison.Ordinal);
            if (scheme >= 0 && !u.StartsWith("/"))
            {
                int end = u.IndexOfAny(new[] { '/', '?' }, scheme + 3);
                u = end < 0 ? "" : u.Substring(end);
            }
            if (u.EndsWith("/")) u = u.Substring(0, u.Length - 1);
            return u;
        }

        private static Dictionary<string, string> ParseParams(string s)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            int i = 0;
            while (i < s.Length)
            {
                while (i < s.Length && (s[i] == ' ' || s[i] == ',')) i++;
                int eq = s.IndexOf('=', i);
                if (eq < 0) break;
                string key = s.Substring(i, eq - i).Trim();
                i = eq + 1;
                string value;
                if (i < s.Length && s[i] == '"')
                {
                    int end = s.IndexOf('"', i + 1);
                    if (end < 0) end = s.Length;
                    value = s.Substring(i + 1, end - i - 1);
                    i = end + 1;
                }
                else
                {
                    int end = s.IndexOf(',', i);
                    if (end < 0) end = s.Length;
                    value = s.Substring(i, end - i).Trim();
                    i = end;
                }
                d[key] = value;
            }
            return d;
        }

        private static string Md5(string s)
        {
            var alg = HashAlgorithmProvider.OpenAlgorithm(HashAlgorithmNames.Md5);
            var hash = alg.HashData(CryptographicBuffer.ConvertStringToBinary(s, BinaryStringEncoding.Utf8));
            return CryptographicBuffer.EncodeToHexString(hash).ToLowerInvariant();
        }

        private static bool IsHex(char ch) => (ch >= '0' && ch <= '9') || ((ch | 0x20) >= 'a' && (ch | 0x20) <= 'f');

        /// <summary>Compares without stopping at the first difference (no timing hints).</summary>
        private static bool FixedEquals(string a, string b)
        {
            if (a == null || b == null) return false;
            int diff = a.Length ^ b.Length;
            for (int i = 0; i < Math.Min(a.Length, b.Length); i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }
    }
}

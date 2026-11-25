using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace PastDow
{
    sealed class BrowserSubtitle
    {
        public string url { get; set; }
        public string lang { get; set; }
        public string cookie { get; set; }
        public string ext { get; set; }
    }

    /// <summary>The extension and protocol share this payload; credentials stay in memory.</summary>
    sealed class BrowserMedia
    {
        public string url { get; set; }
        public string kind { get; set; }
        public long size { get; set; }
        public string page { get; set; }
        public string title { get; set; }
        public string userAgent { get; set; }
        public string cookie { get; set; }
        public string key { get; set; }
        public List<BrowserSubtitle> subtitles { get; set; } = new List<BrowserSubtitle>();

        public string Identity => page + "\n" + new Uri(url).GetLeftPart(UriPartial.Path);
        public static bool Http(string value) => Uri.TryCreate(value, UriKind.Absolute, out var u) && (u.Scheme == "http" || u.Scheme == "https") && string.IsNullOrEmpty(u.UserInfo);
        static bool Header(string value) => value == null || (value.Length <= 65536 && !value.Any(c => c < 32 || c == 127));

        public bool Valid() => Http(url) && Http(page) && Header(userAgent) && Header(cookie) &&
            (title?.Length ?? 0) <= 4096 && (kind?.Length ?? 0) <= 40 && size >= 0 &&
            subtitles != null && subtitles.Count <= 100 && subtitles.All(s => s != null && Http(s.url) && Header(s.cookie) &&
                Regex.IsMatch(s.lang ?? "und", @"^[a-zA-Z0-9-]{1,20}$") && (s.ext == null || s.ext == "srt" || s.ext == "vtt"));

        public Job Job(bool audio, int height, string format) => new Job
        {
            Url = url, Page = page, Referer = page, Title = title, UserAgent = userAgent, Cookie = cookie,
            Captured = true, Audio = audio, MaxHeight = height, Format = format, MediaKind = kind,
            Subtitles = subtitles.Select(s => new BrowserSubtitle { url = s.url, lang = s.lang ?? "und", cookie = s.cookie, ext = s.ext }).ToList()
        };

        public static string Language(string url)
        {
            var tokens = Regex.Split(Uri.UnescapeDataString(url).ToLowerInvariant(), @"[^a-z]+");
            if (tokens.Any(t => t == "es" || t == "spa")) return "es";
            return tokens.Any(t => t == "en" || t == "eng") ? "en" : "und";
        }

        public static BrowserMedia Protocol(string arg)
        {
            try
            {
                if (arg == null || arg.Length > 1048576 || !Uri.TryCreate(arg, UriKind.Absolute, out var u) || u.Scheme != "pastdow" || u.Host != "add") return null;
                var query = u.Query.TrimStart('?').Split('&').FirstOrDefault(x => x.StartsWith("d=", StringComparison.Ordinal));
                if (query == null) return null;
                string data = Uri.UnescapeDataString(query.Substring(2)).Replace('-', '+').Replace('_', '/');
                data = data.PadRight((data.Length + 3) / 4 * 4, '=');
                var media = new JavaScriptSerializer { MaxJsonLength = 1048576 }.Deserialize<BrowserMedia>(Encoding.UTF8.GetString(Convert.FromBase64String(data)));
                return media != null && !string.IsNullOrEmpty(Settings.BrowserKey) && media.key == Settings.BrowserKey && media.Valid() ? media : null;
            }
            catch { return null; }
        }
    }

    /// <summary>Authenticated loopback receiver, never a wildcard listener.</summary>
    sealed class BrowserLink : IDisposable
    {
        public const int Port = 47821;
        readonly HttpListener listener = new HttpListener();
        readonly Func<BrowserMedia, bool, Task> receive;
        public BrowserLink(Func<BrowserMedia, bool, Task> receive) { this.receive = receive; }

        public void Start()
        {
            listener.Prefixes.Add("http://127.0.0.1:" + Port + "/");
            Sealed.Since = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            listener.Start();
            _ = Listen();
        }

        async Task Listen()
        {
            while (listener.IsListening)
            {
                try { var context = await listener.GetContextAsync().ConfigureAwait(false); _ = Handle(context); }
                catch (HttpListenerException) { break; }
                catch (ObjectDisposedException) { break; }
            }
        }

        /// <summary>
        /// One endpoint, POST /msg, with a sealed envelope { iv, ct, mac }. The pairing key never travels: an
        /// encryption key and a MAC key are derived from it, the payload (operation, time, one-time nonce, media with
        /// its cookies) is AES-256-CBC encrypted and the envelope is HMAC-SHA256 signed (encrypt-then-MAC). Whatever
        /// else gets a request (a program on this port while PastDow is closed, or a more specific http.sys prefix
        /// registered on it) only sees ciphertext. The answer signs the nonce so the extension knows PastDow got it.
        /// </summary>
        async Task Handle(HttpListenerContext context)
        {
            var req = context.Request;
            var res = context.Response;
            try
            {
                res.ContentType = "application/json";
                if (req.RemoteEndPoint == null || !IPAddress.IsLoopback(req.RemoteEndPoint.Address) || req.Url.Host != "127.0.0.1" ||
                    string.IsNullOrEmpty(Settings.BrowserKey)) { res.StatusCode = 403; return; }
                if (req.HttpMethod != "POST" || req.Url.AbsolutePath != "/msg") { res.StatusCode = 404; return; }
                if (req.ContentLength64 > 2097152) { res.StatusCode = 413; return; }
                string body;
                using (var reader = new StreamReader(req.InputStream, Encoding.UTF8))
                {
                    var buffer = new char[4096]; var text = new StringBuilder(); int count;
                    while ((count = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
                    {
                        text.Append(buffer, 0, count);
                        if (text.Length > 2097152) { res.StatusCode = 413; return; }
                    }
                    body = text.ToString();
                }
                var json = new JavaScriptSerializer { MaxJsonLength = 2097152 };
                var envelope = json.Deserialize<Dictionary<string, string>>(body);
                var message = Sealed.Open(envelope);
                if (message == null) { res.StatusCode = 403; return; }   // wrong key, tampered or replayed
                var plain = json.Deserialize<Message>(message);
                if (plain == null || !Sealed.Fresh(plain.ts, plain.nonce)) { res.StatusCode = 403; return; }
                if (plain.op == "found" || plain.op == "queue")
                {
                    var media = plain.media;
                    if (media == null || !media.Valid()) { res.StatusCode = 400; return; }
                    media.key = null;
                    await receive(media, plain.op == "queue").ConfigureAwait(false);
                }
                else if (plain.op != "ping") { res.StatusCode = 400; return; }
                byte[] bytes = Encoding.UTF8.GetBytes("{\"proof\":\"" + Sealed.Proof(plain.nonce) + "\"}");
                await res.OutputStream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
            }
            catch { try { res.StatusCode = 400; } catch { } }
            finally { try { res.Close(); } catch { } }
        }

        sealed class Message
        {
            public string op { get; set; }
            public long ts { get; set; }
            public string nonce { get; set; }
            public BrowserMedia media { get; set; }
        }

        public void Dispose() { listener.Close(); }
    }

    /// <summary>Envelope crypto shared with extension\background.js (keys derived from the pairing key).</summary>
    static class Sealed
    {
        static byte[] Derive(string label)
        {
            using (var h = new System.Security.Cryptography.HMACSHA256(Encoding.ASCII.GetBytes(Settings.BrowserKey)))
                return h.ComputeHash(Encoding.ASCII.GetBytes(label));
        }

        static string Hex(byte[] b) => BitConverter.ToString(b).Replace("-", "").ToLowerInvariant();

        static byte[] Mac(string text)
        {
            using (var h = new System.Security.Cryptography.HMACSHA256(Derive("pastdow-mac")))
                return h.ComputeHash(Encoding.ASCII.GetBytes(text));
        }

        /// <summary>The decrypted payload, or null when the MAC does not match.</summary>
        public static string Open(Dictionary<string, string> e)
        {
            if (e == null || !e.TryGetValue("iv", out var iv) || !e.TryGetValue("ct", out var ct) || !e.TryGetValue("mac", out var mac)) return null;
            if (!Regex.IsMatch(iv, "^[a-f0-9]{32}$") || !Regex.IsMatch(mac ?? "", "^[a-f0-9]{64}$")) return null;
            byte[] expected = Mac("pastdow-msg:" + iv + ":" + ct), given = Enumerable.Range(0, 32).Select(i => Convert.ToByte(mac.Substring(i * 2, 2), 16)).ToArray();
            int diff = 0; for (int i = 0; i < 32; i++) diff |= expected[i] ^ given[i];   // constant time
            if (diff != 0) return null;
            using (var aes = System.Security.Cryptography.Aes.Create())
            {
                aes.Key = Derive("pastdow-enc");
                aes.IV = Enumerable.Range(0, 16).Select(i => Convert.ToByte(iv.Substring(i * 2, 2), 16)).ToArray();
                aes.Mode = System.Security.Cryptography.CipherMode.CBC;
                aes.Padding = System.Security.Cryptography.PaddingMode.PKCS7;
                byte[] data = Convert.FromBase64String(ct);
                using (var d = aes.CreateDecryptor()) return Encoding.UTF8.GetString(d.TransformFinalBlock(data, 0, data.Length));
            }
        }

        static readonly Dictionary<string, long> seen = new Dictionary<string, long>();

        /// <summary>When this PastDow started listening: the seen-nonce list is in memory, so anything sealed before
        /// a restart is refused instead of being replayable inside the two-minute window.</summary>
        public static long Since;

        /// <summary>Within two minutes of now and never seen before: a captured request cannot be replayed.</summary>
        public static bool Fresh(long ts, string nonce)
        {
            if (nonce == null || !Regex.IsMatch(nonce, "^[a-f0-9]{32}$")) return false;
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (Math.Abs(now - ts) > 120000 || ts < Since) return false;
            lock (seen)
            {
                foreach (var old in seen.Where(x => now - x.Value > 300000).Select(x => x.Key).ToList()) seen.Remove(old);
                if (seen.ContainsKey(nonce) || seen.Count > 10000) return false;
                seen[nonce] = now;
                return true;
            }
        }

        public static string Proof(string nonce) => Hex(Mac("pastdow-ok:" + nonce));
    }
}

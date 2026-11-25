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

        async Task Handle(HttpListenerContext context)
        {
            var req = context.Request;
            var res = context.Response;
            try
            {
                res.ContentType = "application/json";
                // the extension proves it reached PastDow before it sends the key or any cookie: anything else
                // listening on this port (with PastDow closed) cannot answer, because it does not know the key
                if (req.HttpMethod == "GET" && req.Url.AbsolutePath == "/hello" && req.RemoteEndPoint != null && IPAddress.IsLoopback(req.RemoteEndPoint.Address))
                {
                    string challenge = req.QueryString["c"] ?? "";
                    if (string.IsNullOrEmpty(Settings.BrowserKey) || !Regex.IsMatch(challenge, "^[a-f0-9]{32,128}$")) { res.StatusCode = 400; return; }
                    byte[] hello = Encoding.UTF8.GetBytes("{\"proof\":\"" + Proof(challenge) + "\"}");
                    await res.OutputStream.WriteAsync(hello, 0, hello.Length).ConfigureAwait(false);
                    return;
                }
                if (req.RemoteEndPoint == null || !IPAddress.IsLoopback(req.RemoteEndPoint.Address) || req.Url.Host != "127.0.0.1" ||
                    string.IsNullOrEmpty(Settings.BrowserKey) || req.Headers["X-PastDow-Key"] != Settings.BrowserKey) { res.StatusCode = 403; return; }
                if (req.HttpMethod == "GET" && req.Url.AbsolutePath == "/ping") { res.StatusCode = 200; }
                else if (req.HttpMethod == "POST" && (req.Url.AbsolutePath == "/found" || req.Url.AbsolutePath == "/queue"))
                {
                    if (req.ContentLength64 > 1048576) { res.StatusCode = 413; return; }
                    string body;
                    using (var reader = new StreamReader(req.InputStream, Encoding.UTF8))
                    {
                        var buffer = new char[4096]; var text = new StringBuilder(); int count;
                        while ((count = await reader.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) > 0)
                        {
                            text.Append(buffer, 0, count);
                            if (text.Length > 1048576) { res.StatusCode = 413; return; }
                        }
                        body = text.ToString();
                    }
                    var media = new JavaScriptSerializer { MaxJsonLength = 1048576 }.Deserialize<BrowserMedia>(body);
                    if (media == null || !media.Valid()) { res.StatusCode = 400; return; }
                    media.key = null;
                    await receive(media, req.Url.AbsolutePath == "/queue").ConfigureAwait(false);
                }
                else { res.StatusCode = 404; return; }
                byte[] bytes = Encoding.UTF8.GetBytes("{\"ok\":true}");
                await res.OutputStream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
            }
            catch { try { res.StatusCode = 400; } catch { } }
            finally { try { res.Close(); } catch { } }
        }

        /// <summary>HMAC-SHA256 of "pastdow-hello:" + challenge, keyed with the pairing key (hex, lower case).</summary>
        public static string Proof(string challenge)
        {
            using (var h = new System.Security.Cryptography.HMACSHA256(Encoding.ASCII.GetBytes(Settings.BrowserKey)))
                return BitConverter.ToString(h.ComputeHash(Encoding.ASCII.GetBytes("pastdow-hello:" + challenge))).Replace("-", "").ToLowerInvariant();
        }

        public void Dispose() { listener.Close(); }
    }
}

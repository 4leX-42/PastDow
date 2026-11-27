param(
    [string]$Assembly = (Join-Path $PSScriptRoot '../bin/verify-browser/PastDow.exe'),
    [string]$FfmpegDir = (Join-Path $PSScriptRoot '../bin/test-tools')
)
$ErrorActionPreference = 'Stop'
$assemblyPath = (Resolve-Path -LiteralPath $Assembly).Path
$testDir = Join-Path $PSScriptRoot ('../bin/browser-tests-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $testDir | Out-Null
$testDir = (Resolve-Path -LiteralPath $testDir).Path
Add-Type -AssemblyName System.Web.Extensions
Add-Type -ReferencedAssemblies System.Web.Extensions,System.Core -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq.Expressions;
using System.Linq;
using System.Collections;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

public static class BrowserChecks
{
    static Assembly app;
    static string dir;
    static int received, queued, formatRuns;
    static object lastMedia;
    static bool failFormats;
    const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    static Type T(string name) { return app.GetType("PastDow." + name, true); }
    static void Set(object target, string name, object value) { target.GetType().GetField(name, Flags).SetValue(target, value); }
    static void Setting(string name, object value) { T("Settings").GetField(name, Flags).SetValue(null, value); }
    static object Call(Type type, object target, string method, params object[] args) { return type.GetMethod(method, Flags).Invoke(target, args); }
    static void Check(bool value, string label) { if (!value) throw new Exception(label); }
    static Task Receive(object media, bool queue) { lastMedia = media; received++; if (queue) queued++; return Task.FromResult(true); }
    static string Body(string url, string subtitle)
    {
        return new JavaScriptSerializer().Serialize(new { url = url, page = "https://page.example/recording", title = "Recording", kind = "MP4", size = 900000,
            userAgent = "Test agent", cookie = "media=value", subtitles = new[] { new { url = subtitle, lang = "und", cookie = "sub=value", ext = "vtt" } } });
    }
    static object Field(object obj, string name) { return obj.GetType().GetField(name, Flags).GetValue(obj); }
    static string FormatsJson = "{\"title\":\"Sample\",\"duration\":125.5,\"formats\":[{\"height\":720,\"vcodec\":\"avc1\",\"acodec\":\"aac\"},{\"height\":1080,\"vcodec\":\"avc1\",\"acodec\":\"none\"},{\"height\":720,\"vcodec\":\"vp9\",\"acodec\":\"none\"},{\"height\":480,\"vcodec\":\"avc1\"},{\"height\":360,\"vcodec\":\"avc1\"},{\"height\":90,\"vcodec\":\"images\"},{\"height\":null,\"vcodec\":\"none\",\"acodec\":\"opus\"}]}";
    static Task<string> FakeFormats(string url)
    {
        formatRuns++;
        if (failFormats) throw new InvalidOperationException("fake probe failure");
        return Task.FromResult(FormatsJson);
    }
    static Dictionary<string, object> FormatResult(object probe, string url)
    {
        return ((Task<Dictionary<string, object>>)Call(T("BrowserFormats"), probe, "Get", url)).GetAwaiter().GetResult();
    }
    static void ChoiceChecks(JavaScriptSerializer json)
    {
        Setting("Audio", false); Setting("MaxHeight", 720); Setting("VideoFormat", "webm"); Setting("AudioFormat", "opus");
        Func<string, object> choice = text => Call(T("BrowserMedia"), json.Deserialize(text, T("BrowserMedia")), "Choice");
        object value = choice("{}");
        Check(!(bool)Field(value, "Audio") && (int)Field(value, "Height") == 720 && (string)Field(value, "Format") == "webm", "missing choices use saved defaults");
        foreach (int height in new[] { 0, 2160, 1440, 1080, 720, 480, 360 })
        {
            value = choice("{\"audio\":false,\"q\":" + height + ",\"fmt\":\"mp4\"}");
            Check((int)Field(value, "Height") == height && (string)Field(value, "Format") == "mp4", "valid video quality " + height);
        }
        foreach (string format in new[] { "best", "mp3", "m4a", "flac", "wav", "opus" })
        {
            value = choice("{\"audio\":true,\"q\":1080,\"fmt\":\"" + format + "\"}");
            Check((bool)Field(value, "Audio") && (int)Field(value, "Height") == 0 && (string)Field(value, "Format") == format, "valid audio format " + format);
        }
        foreach (string text in new[] { "{\"q\":999,\"fmt\":\"exe\"}", "{\"audio\":\"bad\",\"q\":{},\"fmt\":false}", "{\"audio\":false,\"q\":-1,\"fmt\":\"mp3\"}" })
        {
            value = choice(text);
            Check(!(bool)Field(value, "Audio") && (int)Field(value, "Height") == 720 && (string)Field(value, "Format") == "webm", "invalid choices use saved defaults");
        }
        Setting("Audio", true);
        value = choice("{\"fmt\":\"mkv\"}");
        Check((bool)Field(value, "Audio") && (int)Field(value, "Height") == 0 && (string)Field(value, "Format") == "opus", "invalid audio format uses saved audio default");
        Setting("Audio", false); Setting("MaxHeight", 0); Setting("VideoFormat", "mkv"); Setting("AudioFormat", "best");
        object media = json.Deserialize("{\"url\":\"https://page.example/video\",\"page\":\"https://page.example/video\",\"kind\":\"PAGE\"}", T("BrowserMedia"));
        foreach (int height in new[] { 480, 360 })
        {
            object job = Call(T("BrowserMedia"), media, "Job", false, height, "mp4");
            Check(((string)Call(T("Runner"), null, "Args", job)).Contains("height<=" + height), "job arguments preserve lower quality " + height);
        }
        var formats = Activator.CreateInstance(T("BrowserFormats"));
        Set(formats, "Run", (Func<string, Task<string>>)FakeFormats);
        var result = FormatResult(formats, "https://page.example/sample");
        Check(string.Join(",", (int[])result["heights"]) == "1080,720,480,360" && (bool)result["audio"] && (string)result["title"] == "Sample" && (double)result["duration"] == 125.5, "parse distinct descending video heights and metadata");
        result["proof"] = "do not cache response signatures";
        Check(!FormatResult(formats, "https://page.example/sample").ContainsKey("proof") && formatRuns == 1, "URL cache returns independent response dictionaries");
        var cache = (IDictionary)Field(formats, "cache");
        var entry = cache["https://page.example/sample"];
        DateTime until = (DateTime)Field(entry, "Until");
        Check(until > DateTime.UtcNow.AddMinutes(9) && until <= DateTime.UtcNow.AddMinutes(10), "format cache lifetime is ten minutes");
        Set(entry, "Until", DateTime.UtcNow.AddSeconds(-1));
        FormatResult(formats, "https://page.example/sample"); Check(formatRuns == 2, "expired cache probes again");
        failFormats = true;
        try { FormatResult(formats, "https://page.example/failure"); throw new Exception("failure expected"); } catch (InvalidOperationException) { }
        failFormats = false;
        FormatResult(formats, "https://page.example/failure"); Check(formatRuns == 4, "failed probes are not cached");
        var silent = (Dictionary<string, object>)Call(T("BrowserFormats"), null, "Parse", "{\"formats\":[{\"height\":1080,\"vcodec\":\"avc1\",\"acodec\":\"none\"}]}");
        Check(!(bool)silent["audio"], "silent video does not offer audio");
        Console.WriteLine("Formats: fake runner only; sorted distinct heights, metadata, audio presence, ten-minute cache, expiry, independent signatures, failure retry; queue choices validated including 480/360");
    }
    static object historyStore;
    static int launches;
    static string launchedFolder, launchedFile;
    static void FakeLaunch(string folder, string file) { launches++; launchedFolder = folder; launchedFile = file; }
    static Task<bool> FakeReveal(string id) { return Task.FromResult((bool)Call(T("DownloadHistory"), historyStore, "Reveal", id, (Action<string, string>)FakeLaunch)); }
    static object[] Items(object store) { return (object[])Call(T("DownloadHistory"), store, "Items"); }
    static string EntryId(object store) { return (string)Items(store)[0].GetType().GetProperty("id").GetValue(Items(store)[0], null); }
    static string lastReply;
    static string Hex(byte[] b) { return BitConverter.ToString(b).Replace("-", "").ToLowerInvariant(); }
    static byte[] Hmac(byte[] key, string text) { using (var h = new System.Security.Cryptography.HMACSHA256(key)) return h.ComputeHash(Encoding.ASCII.GetBytes(text)); }
    // the extension's side of the envelope (extension/background.js seal)
    static string Seal(string key, string op, string media, long ts, string nonce, string id = null, string url = null)
    {
        byte[] k = Encoding.ASCII.GetBytes(key), enc = Hmac(k, "pastdow-enc"), mac = Hmac(k, "pastdow-mac");
        string plain = "{\"op\":\"" + op + "\",\"ts\":" + ts + ",\"nonce\":\"" + nonce + "\"" + (media != null ? ",\"media\":" + media : "") + (id != null ? ",\"id\":" + new JavaScriptSerializer().Serialize(id) : "") + (url != null ? ",\"url\":" + new JavaScriptSerializer().Serialize(url) : "") + "}";
        using (var aes = System.Security.Cryptography.Aes.Create())
        {
            aes.Key = enc; aes.GenerateIV(); aes.Mode = System.Security.Cryptography.CipherMode.CBC;
            byte[] data = Encoding.UTF8.GetBytes(plain);
            string ct; using (var e = aes.CreateEncryptor()) ct = Convert.ToBase64String(e.TransformFinalBlock(data, 0, data.Length));
            string iv = Hex(aes.IV);
            return "{\"iv\":\"" + iv + "\",\"ct\":\"" + ct + "\",\"mac\":\"" + Hex(Hmac(mac, "pastdow-msg:" + iv + ":" + ct)) + "\"}";
        }
    }
    static string Proof(string key, string nonce) { return Hex(Hmac(Hmac(Encoding.ASCII.GetBytes(key), "pastdow-mac"), "pastdow-ok:" + nonce)); }
    // Independent verifier for the returned data: no call to the app's canonicalizer or proof helper.
    static string Canonical(object value)
    {
        if (value == null) return "null";
        if (value is string)
        {
            var b = new StringBuilder("\"");
            foreach (char c in (string)value)
            {
                if (c == '"' || c == '\\') b.Append('\\').Append(c);
                else if (c < 32 || c > 126) b.Append("\\u").Append(((int)c).ToString("x4"));
                else b.Append(c);
            }
            return b.Append('"').ToString();
        }
        var map = value as Dictionary<string, object>;
        if (map != null) return "{" + string.Join(",", map.Keys.OrderBy(k => k, StringComparer.Ordinal).Select(k => Canonical(k) + ":" + Canonical(map[k]))) + "}";
        if (value is IEnumerable) return "[" + string.Join(",", ((IEnumerable)value).Cast<object>().Select(Canonical)) + "]";
        if (value is bool) return (bool)value ? "true" : "false";
        return Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
    }
    static bool Verify(string key, string nonce, string response)
    {
        var data = new JavaScriptSerializer { MaxJsonLength = 33554432 }.Deserialize<Dictionary<string, object>>(response);
        string proof = (string)data["proof"]; data.Remove("proof");
        using (var sha = System.Security.Cryptography.SHA256.Create())
            return proof == Hex(Hmac(Hmac(Encoding.ASCII.GetBytes(key), "pastdow-mac"), "pastdow-ok:" + nonce + ":" + Hex(sha.ComputeHash(Encoding.UTF8.GetBytes(Canonical(data))))));
    }
    static void HistoryChecks(object job)
    {
        Type type = T("DownloadHistory");
        historyStore = Activator.CreateInstance(type, new object[] { dir });
        Check(Items(historyStore).Length == 0, "missing history starts empty");
        string file = Path.Combine(dir, "history clip.mp4"); File.WriteAllText(file, "123456");
        Set(job, "File", file); Set(job, "Title", "Original"); Set(job, "CustomName", "Clip \u00f1 \ud83c\udfac <&> \"\\\n");
        Set(job, "Thumb", "data:image/jpeg;base64,AQID"); Set(job, "Audio", true);
        foreach (string state in new[] { "Skipped", "Failed", "Canceled" })
        {
            Set(job, "State", Enum.Parse(T("JobState"), state));
            Check(!(bool)Call(type, historyStore, "Add", job), "do not record " + state);
        }
        Set(job, "State", Enum.Parse(T("JobState"), "Done"));
        Check((bool)Call(type, historyStore, "Add", job), "record done");
        string id = EntryId(historyStore);
        var js = new JavaScriptSerializer { MaxJsonLength = 33554432 };
        var saved = js.Deserialize<List<Dictionary<string, object>>>(File.ReadAllText(Path.Combine(dir, "history.json")))[0];
        Check((string)saved["title"] == (string)job.GetType().GetField("CustomName", Flags).GetValue(job) && (int)saved["size"] == 6, "custom title and actual file bytes");
        Check((string)saved["url"] == "https://cdn.example/video.mp4?token=fresh" && (bool)saved["audio"] && (string)saved["thumb"] == "data:image/jpeg;base64,AQID", "source, audio and thumbnail persisted");
        Check(DateTimeOffset.Parse((string)saved["date"]).Offset == TimeSpan.Zero, "UTC date");
        object reload = Activator.CreateInstance(type, new object[] { dir });
        Check(Items(reload).Length == 1 && EntryId(reload) == id, "persisted stable id");
        Check(!(bool)Call(type, historyStore, "Reveal", "unknown", (Action<string, string>)FakeLaunch) && launches == 0, "unknown id never launches");
        Check(!(bool)Call(type, historyStore, "Reveal", file, (Action<string, string>)FakeLaunch) && launches == 0, "path never accepted as id");
        Check((bool)Call(type, historyStore, "Reveal", id, (Action<string, string>)FakeLaunch) && launchedFile == file, "existing file selected");
        File.Delete(file);
        Check((bool)Call(type, historyStore, "Reveal", id, (Action<string, string>)FakeLaunch) && launchedFile == null && launchedFolder == dir, "missing file opens existing folder");
        File.WriteAllText(file, "123456");
        // Concurrent completions must preserve the most recent 200 and always leave parseable JSON.
        var seed = new List<Dictionary<string, object>>();
        for (int i = 0; i < 199; i++)
        {
            var entry = new Dictionary<string, object>(saved); entry["id"] = Guid.NewGuid().ToString("N");
            entry["date"] = "2000-01-01T00:00:00.0000000Z"; seed.Add(entry);
        }
        seed.Add(saved); seed[199]["date"] = "1999-01-01T00:00:00.0000000Z";
        File.WriteAllText(Path.Combine(dir, "history.json"), js.Serialize(seed));
        historyStore = Activator.CreateInstance(type, new object[] { dir });
        Parallel.For(0, 8, i => { Check((bool)Call(type, historyStore, "Add", job), "concurrent add"); Items(historyStore); });
        Check(Items(historyStore).Length == 200, "200 entry limit");
        reload = Activator.CreateInstance(type, new object[] { dir });
        Check(Items(reload).Length == 200 && EntryId(reload) == EntryId(historyStore), "limit and newest first persisted");
        saved = js.Deserialize<List<Dictionary<string, object>>>(File.ReadAllText(Path.Combine(dir, "history.json")))[0];
        var all = js.Deserialize<List<Dictionary<string, object>>>(File.ReadAllText(Path.Combine(dir, "history.json")));
        Check(!all.Any(e => (string)e["id"] == id), "oldest evicted");
        Check(all.Zip(all.Skip(1), (a, b) => string.CompareOrdinal((string)a["date"], (string)b["date"]) >= 0).All(x => x), "newest first");
        Check(Directory.GetFiles(dir, "*.tmp").Length == 0, "atomic writes leave no temp files");
        foreach (string broken in new[] { "broken json", "null", "[null]" })
        {
            File.WriteAllText(Path.Combine(dir, "history.json"), broken);
            historyStore = Activator.CreateInstance(type, new object[] { dir });
            Check(Items(historyStore).Length == 0 && (bool)Call(type, historyStore, "Add", job), "corrupt history recovers");
        }
        Set(job, "CustomName", null); Set(job, "Thumb", "https://bad.example/thumb.jpg");
        Check((bool)Call(type, historyStore, "Add", job), "title fallback added");
        Check(js.Serialize(Items(historyStore)[0]).Contains("Original") && !js.Serialize(Items(historyStore)[0]).Contains("bad.example"), "title fallback and thumbnail sanitized");
        string realFile = (string)job.GetType().GetField("File", Flags).GetValue(job);
        Set(job, "File", Path.Combine(dir, "missing-folder", "clip.mp4"));
        Check((bool)Call(type, historyStore, "Add", job), "missing folder fixture");
        int beforeMissing = launches;
        Check(!(bool)Call(type, historyStore, "Reveal", EntryId(historyStore), (Action<string, string>)FakeLaunch) && launches == beforeMissing, "missing folder never launches");
        Set(job, "File", realFile);
        // Restore Unicode entry for the endpoint verifier.
        Set(job, "CustomName", "Clip \u00f1 \ud83c\udfac <&> \"\\\n");
        Check((bool)Call(type, historyStore, "Add", job), "endpoint fixture");
        launches = 0;
        Console.WriteLine("History: Done only, fields, thumbnail, stable ids, atomic persistence, concurrent adds, newest first, 200 limit, corrupt recovery and fake reveal passed");
    }
    static long Now() { return DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); }
    static string Nonce() { return Guid.NewGuid().ToString("N"); }
    static int Request(HttpListener listener, object receiver, int port, string method, string path, string body)
    {
        Task handling = Task.Run(async delegate { var context = await listener.GetContextAsync(); await (Task)Call(T("BrowserLink"), receiver, "Handle", context); });
        var req = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + port + path);
        req.Method = method; req.Proxy = null; req.Timeout = 5000;
        lastReply = null;
        if (body != null)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(body); req.ContentLength = bytes.Length; req.ContentType = "application/json";
            using (var stream = req.GetRequestStream()) stream.Write(bytes, 0, bytes.Length);
        }
        int status;
        try { using (var response = (HttpWebResponse)req.GetResponse()) { status = (int)response.StatusCode; using (var r = new StreamReader(response.GetResponseStream())) lastReply = r.ReadToEnd(); } }
        catch (WebException ex) { using (var response = (HttpWebResponse)ex.Response) status = (int)response.StatusCode; }
        handling.GetAwaiter().GetResult();
        return status;
    }
    public static void Run(string assembly, string directory)
    {
        app = Assembly.LoadFile(assembly); dir = directory;
        Setting("Home", dir); Setting("BinDir", Path.Combine(dir, "tools")); Setting("BrowserKey", new string('a', 64));
        Setting("VideoDir", dir); Setting("AudioDir", dir);
        string extension = Path.Combine(dir, "extension"), pairing = Path.Combine(extension, "pairing.json");
        Call(T("ExtensionSetup"), null, "RefreshPairing");
        Check(!Directory.Exists(extension), "startup does not create an extension folder");
        Directory.CreateDirectory(extension);
        File.WriteAllText(Path.Combine(extension, "sentinel.txt"), "unchanged");
        Call(T("ExtensionSetup"), null, "RefreshPairing");
        Check(File.ReadAllText(pairing) == "{\"key\":\"" + new string('a', 64) + "\"}", "startup writes current pairing key");
        Setting("BrowserKey", new string('b', 64));
        Call(T("ExtensionSetup"), null, "RefreshPairing");
        Check(File.ReadAllText(pairing) == "{\"key\":\"" + new string('b', 64) + "\"}", "startup atomically replaces stale pairing key");
        Check(File.ReadAllText(Path.Combine(extension, "sentinel.txt")) == "unchanged" && Directory.GetFiles(extension).Length == 2, "startup only changes pairing.json and leaves no temp files");
        Setting("BrowserKey", new string('a', 64));
        Console.WriteLine("Startup pairing: absent folder untouched, existing folder refreshed, stale key replaced, other files preserved, no temp files (temp Home)");

        var js = new JavaScriptSerializer();
        ChoiceChecks(js);
        string body = Body("https://cdn.example/video.mp4?token=fresh", "https://subs.example/caption.vtt?token=fresh");
        Type mediaType = T("BrowserMedia"); object media = js.Deserialize(body, mediaType);
        Check((bool)Call(mediaType, media, "Valid"), "valid media");
        Check(!(bool)Call(mediaType, js.Deserialize(Body("file:///bad.mp4", "https://subs.example/a.vtt"), mediaType), "Valid"), "reject file media");
        Check(!(bool)Call(mediaType, js.Deserialize(Body("https://cdn.example/a.mp4", "javascript:bad"), mediaType), "Valid"), "reject subtitle scheme");
        Check(!(bool)Call(mediaType, js.Deserialize(body.Replace("media=value", "media=value\\r\\nInjected: yes"), mediaType), "Valid"), "reject header injection");
        foreach (string thumb in new[] { "data:image/jpeg;base64,AQID", "data:image/png;base64,AQID", "http://example/image.jpg", "data:image/jpeg;base64," + new string('A', 120000), "data:image/jpeg;base64,<script>alert(1)</script>", "data:image/jpeg;base64,AQ==\\n" })
        {
            var fields = js.Deserialize<Dictionary<string, object>>(body); fields["thumb"] = thumb; fields["duration"] = 125.5;
            object candidate = js.Deserialize(js.Serialize(fields), mediaType);
            Check((bool)Call(mediaType, candidate, "Valid"), "bad thumbnail still accepts media");
            bool good = thumb == "data:image/jpeg;base64,AQID" || thumb == "data:image/png;base64,AQID";
            Check((string)mediaType.GetProperty("thumb").GetValue(candidate, null) == (good ? thumb : null), "thumbnail validation");
            Check((double)mediaType.GetProperty("duration").GetValue(candidate, null) == 125.5, "duration retained");
        }
        foreach (double duration in new[] { -1.0, 86401.0, double.NaN, double.PositiveInfinity, double.NegativeInfinity, 0.0, 86400.0 })
        {
            mediaType.GetProperty("duration").SetValue(media, duration, null);
            Check((bool)Call(mediaType, media, "Valid"), "bad duration still accepts media");
            Check((double)mediaType.GetProperty("duration").GetValue(media, null) == (duration >= 0 && duration <= 86400 ? duration : 0), "duration validation");
        }
        object pageMedia = js.Deserialize(js.Serialize(new { url = "https://www.youtube.com/watch?v=abcdefghijk", page = "https://www.youtube.com/watch?v=abcdefghijk", kind = "PAGE", title = "YouTube video" }), mediaType);
        Check((bool)Call(mediaType, pageMedia, "Valid"), "PAGE media accepted without captured headers");
        object pageJob = Call(mediaType, pageMedia, "Job", false, 1080, "mkv");
        Check(!(bool)pageJob.GetType().GetField("Captured", Flags).GetValue(pageJob), "PAGE creates a normal job");
        Check((string)pageJob.GetType().GetField("Url", Flags).GetValue(pageJob) == "https://www.youtube.com/watch?v=abcdefghijk", "PAGE keeps canonical URL");
        Check(pageJob.GetType().GetField("Cookie", Flags).GetValue(pageJob) == null && pageJob.GetType().GetField("Referer", Flags).GetValue(pageJob) == null && pageJob.GetType().GetField("UserAgent", Flags).GetValue(pageJob) == null, "PAGE has no captured credentials");
        Check((int)pageJob.GetType().GetField("MaxHeight", Flags).GetValue(pageJob) == 1080 && (string)pageJob.GetType().GetField("Format", Flags).GetValue(pageJob) == "mkv", "PAGE keeps quality and format");
        object audioPageJob = Call(mediaType, pageMedia, "Job", true, 0, "mp3");
        Check((bool)audioPageJob.GetType().GetField("Audio", Flags).GetValue(audioPageJob) && !(bool)audioPageJob.GetType().GetField("Captured", Flags).GetValue(audioPageJob), "PAGE supports normal audio jobs");
        string pageArgs = (string)Call(T("Runner"), null, "Args", pageJob);
        Check(!pageArgs.Contains("--load-info-json") && pageArgs.Contains("--embed-subs"), "PAGE uses normal extraction and subtitle options");
        mediaType.GetProperty("thumb").SetValue(media, "data:image/jpeg;base64,AQID", null);
        object job = Call(mediaType, media, "Job", false, 1080, "mkv");
        Check((string)job.GetType().GetField("Thumb", Flags).GetValue(job) == "data:image/jpeg;base64,AQID", "browser thumbnail carried onto job");
        Check((bool)job.GetType().GetField("Captured", Flags).GetValue(job), "captured job");
        string info = (string)Call(T("CapturedMedia"), null, "Info", job);
        var data = js.Deserialize<Dictionary<string, object>>(File.ReadAllText(info));
        var tracks = (Dictionary<string, object>)data["subtitles"];
        var track = (Dictionary<string, object>)((System.Collections.IList)tracks["und"])[0];
        Check(((Dictionary<string, object>)data["http_headers"])["Cookie"].ToString() == "media=value", "media cookies");
        Check(((Dictionary<string, object>)track["http_headers"])["Cookie"].ToString() == "sub=value", "per-subtitle cookies");
        Set(job, "InfoJson", info);
        string args = (string)Call(T("Runner"), null, "Args", job);
        Check(args.Contains("--load-info-json") && args.Contains("--embed-subs") && args.Contains("--sub-langs all"), "direct-file args");
        Check(!args.Contains("--add-header"), "do not override per-subtitle cookies");
        var protocol = js.Deserialize<Dictionary<string, object>>(body); protocol["key"] = new string('a', 64); protocol["audio"] = false; protocol["q"] = 480; protocol["fmt"] = "mp4";
        string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(js.Serialize(protocol))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Check(Call(mediaType, null, "Protocol", "pastdow://add?d=" + encoded) != null, "protocol accepts paired payload");
        var protocolChoice = Call(mediaType, Call(mediaType, null, "Protocol", "pastdow://add?d=" + encoded), "Choice");
        Check((int)Field(protocolChoice, "Height") == 480 && (string)Field(protocolChoice, "Format") == "mp4", "offline protocol preserves per-download options");
        Setting("BrowserKey", new string('b', 64));
        Check(Call(mediaType, null, "Protocol", "pastdow://add?d=" + encoded) == null, "protocol rejects wrong key");
        Setting("BrowserKey", new string('a', 64));
        Check(Call(mediaType, null, "Protocol", "pastdow://add?d=broken") == null, "protocol rejects malformed payload");
        Check((string)Call(mediaType, null, "Language", "https://subs.example/caption.eng.vtt") == "en", "language guess");
        string video = Path.Combine(dir, "clean.mkv"), sub = Path.Combine(dir, "clean.und.srt"), image = Path.Combine(dir, "clean.jpg");
        File.WriteAllText(video, "test"); File.WriteAllText(sub, "test"); File.WriteAllText(image, "test");
        Call(T("Runner"), null, "CleanSidecars", video);
        Check(File.Exists(sub) && !File.Exists(image), "preserve subtitle sidecars");
        HistoryChecks(job);
        Type receiverType = T("BrowserLink");
        Type callbackType = typeof(Func<,,>).MakeGenericType(mediaType, typeof(bool), typeof(Task));
        var p = Expression.Parameter(mediaType, "media"); var q = Expression.Parameter(typeof(bool), "queue");
        Delegate callback = Expression.Lambda(callbackType, Expression.Call(typeof(BrowserChecks).GetMethod("Receive", Flags), Expression.Convert(p, typeof(object)), q), p, q).Compile();
        object receiver = Activator.CreateInstance(receiverType, Flags, null, new object[] { callback }, null);
        Set(receiver, "History", historyStore); Set(receiver, "Reveal", (Func<string, Task<bool>>)FakeReveal);
        // Use a separate ephemeral port, never the user's running PastDow endpoint.
        var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start(); int port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
        using (var listener = new HttpListener())
        {
            listener.Prefixes.Add("http://127.0.0.1:" + port + "/"); listener.Start();
            string key = new string('a', 64), n;
            n = Nonce();
            Check(Request(listener, receiver, port, "POST", "/msg", Seal(key, "history", null, Now(), n)) == 200, "history endpoint");
            Check(Verify(key, n, lastReply), "response proof binds canonical history data");
            Check(!Verify(key, n, lastReply.Replace("Original", "Tampered")), "tampered history rejected");
            Check(!lastReply.Contains("\"file\"") && !lastReply.Contains("history.json") && !lastReply.Contains(dir.Replace("\\", "\\\\")), "history never sends full paths");
            Check(lastReply.Contains("\"name\":\"history clip.mp4\"") && lastReply.Contains("\"exists\":true"), "history sends filename and existence");
            string known = EntryId(historyStore);
            n = Nonce();
            Check(Request(listener, receiver, port, "POST", "/msg", Seal(key, "reveal", null, Now(), n, "unknown")) == 200 && lastReply.Contains("\"ok\":false") && launches == 0 && Verify(key, n, lastReply), "unknown id endpoint never launches");
            n = Nonce();
            Check(Request(listener, receiver, port, "POST", "/msg", Seal(key, "reveal", null, Now(), n, known)) == 200 && lastReply.Contains("\"ok\":true") && launches == 1 && Verify(key, n, lastReply), "known id endpoint uses fake launcher");
            Check(!Verify(key, n, lastReply.Replace("true", "false")), "tampered reveal result rejected");
            File.Delete(launchedFile);
            n = Nonce();
            Check(Request(listener, receiver, port, "POST", "/msg", Seal(key, "history", null, Now(), n)) == 200 && lastReply.Contains("\"exists\":false") && Verify(key, n, lastReply), "deleted file existence refreshed");
            Check(Request(listener, receiver, port, "POST", "/msg", Seal(new string('b', 64), "ping", null, Now(), Nonce())) == 403, "reject wrong key");
            Check(Request(listener, receiver, port, "GET", "/msg", null) == 404 && Request(listener, receiver, port, "POST", "/queue", body) == 404, "only sealed POST /msg");
            n = Nonce();
            Check(Request(listener, receiver, port, "POST", "/msg", Seal(key, "ping", null, Now(), n)) == 200 && Verify(key, n, lastReply) && lastReply.Contains("\"vfmt\":\"mkv\"") && lastReply.Contains("\"afmt\":\"best\"") && lastReply.Contains("\"q\":0") && lastReply.Contains("\"mode\":\"video\""), "sealed ping returns signed defaults");
            Check(Request(listener, receiver, port, "POST", "/msg", Seal(key, "found", body, Now(), Nonce())) == 200 && received == 1 && queued == 0, "found without queue");
            string queueMsg = Seal(key, "queue", body, Now(), Nonce());
            Check(Request(listener, receiver, port, "POST", "/msg", queueMsg) == 200 && received == 2 && queued == 1, "queue");
            Check(Request(listener, receiver, port, "POST", "/msg", queueMsg) == 403 && queued == 1, "replayed message rejected");
            Check(Request(listener, receiver, port, "POST", "/msg", Seal(key, "queue", body, Now() - 600000, Nonce())) == 403 && queued == 1, "stale message rejected");
            // sealed before this PastDow started (a capture replayed after a restart): refused even inside the window
            T("Sealed").GetField("Since", Flags).SetValue(null, Now());
            Check(Request(listener, receiver, port, "POST", "/msg", Seal(key, "queue", body, Now() - 60000, Nonce())) == 403 && queued == 1, "message from before a restart rejected");
            T("Sealed").GetField("Since", Flags).SetValue(null, 0L);
            string tampered = Seal(key, "queue", body, Now(), Nonce()); tampered = tampered.Replace("\"ct\":\"", "\"ct\":\"A");
            Check(Request(listener, receiver, port, "POST", "/msg", tampered) == 403 && queued == 1, "tampered ciphertext rejected");
            Check(Request(listener, receiver, port, "POST", "/msg", Seal(key, "queue", Body("file:///bad", "https://subs.example/a.vtt"), Now(), Nonce())) == 400 && queued == 1, "endpoint validates URLs");
            Check(Request(listener, receiver, port, "POST", "/msg", "malformed") == 400, "malformed JSON");
            Set(receiverType.GetField("Formats", Flags).GetValue(receiver), "Run", (Func<string, Task<string>>)FakeFormats);
            n = Nonce();
            Check(Request(listener, receiver, port, "POST", "/msg", Seal(key, "formats", null, Now(), n, null, "https://page.example/lookup")) == 200 && Verify(key, n, lastReply) && lastReply.Contains("\"heights\":[1080,720,480,360]"), "formats endpoint returns signed parsed fake data");
            n = Nonce();
            Check(Request(listener, receiver, port, "POST", "/msg", Seal(key, "formats", null, Now(), n, null, "https://page.example/lookup")) == 200 && Verify(key, n, lastReply), "cached formats response has fresh valid proof");
            Check(Request(listener, receiver, port, "POST", "/msg", Seal(key, "formats", null, Now(), Nonce(), null, "file:///bad")) == 400, "formats rejects non-HTTP URL");
            var options = js.Deserialize<Dictionary<string, object>>(body);
            options["audio"] = true; options["q"] = 480; options["fmt"] = "flac";
            Check(Request(listener, receiver, port, "POST", "/msg", Seal(key, "queue", js.Serialize(options), Now(), Nonce())) == 200, "queue custom audio options");
            Check((bool)mediaType.GetProperty("audio").GetValue(lastMedia, null) && (int)mediaType.GetProperty("q").GetValue(lastMedia, null) == 0 && (string)mediaType.GetProperty("fmt").GetValue(lastMedia, null) == "flac", "queue callback receives validated audio options");
            options["audio"] = false; options["q"] = 360; options["fmt"] = "mp4";
            Check(Request(listener, receiver, port, "POST", "/msg", Seal(key, "queue", js.Serialize(options), Now(), Nonce())) == 200 && (int)mediaType.GetProperty("q").GetValue(lastMedia, null) == 360, "queue accepts 360P");
            options["audio"] = "invalid"; options["q"] = 123; options["fmt"] = "exe";
            Check(Request(listener, receiver, port, "POST", "/msg", Seal(key, "queue", js.Serialize(options), Now(), Nonce())) == 200, "queue invalid options fall back");
            Check(!(bool)mediaType.GetProperty("audio").GetValue(lastMedia, null) && (int)mediaType.GetProperty("q").GetValue(lastMedia, null) == 0 && (string)mediaType.GetProperty("fmt").GetValue(lastMedia, null) == "mkv", "queue callback receives saved defaults for invalid options");

        }
        ((IDisposable)receiver).Dispose();
        Console.WriteLine("Browser: PAGE normal jobs, thumbnail and duration validation, header injection, job conversion, direct info JSON, subtitle headers, arguments, protocol auth, sidecar preservation and sealed HTTP endpoint (wrong key, replay, stale, pre-restart, tamper) passed");
        Type instanceType = T("SingleInstance");
        instanceType.GetField("Name", Flags).SetValue(null, "PastDow.Test." + Guid.NewGuid().ToString("N"));
        object first = Activator.CreateInstance(instanceType);
        using (var signal = new System.Threading.ManualResetEvent(false))
        {
            string[] forwarded = null;
            Action<string[]> receive = value => { forwarded = value; signal.Set(); };
            try
            {
                Check((bool)instanceType.GetProperty("First").GetValue(first, null), "first instance");
                Call(instanceType, first, "Listen", receive);
                object second = Activator.CreateInstance(instanceType);
                Check(!(bool)instanceType.GetProperty("First").GetValue(second, null), "second instance");
                ((IDisposable)second).Dispose();
                Check((bool)Call(instanceType, null, "Forward", new string[] { "pastdow://add?d=test", "https://page.example/" }, 5000), "argument pipe forwarding");
                Check(signal.WaitOne(5000) && forwarded.Length == 2 && forwarded[0] == "pastdow://add?d=test", "forwarded arguments");
            }
            finally { ((IDisposable)first).Dispose(); }
        }
        Console.WriteLine("Single instance: isolated per-user mutex and named-pipe forwarding passed, production pipe untouched");
    }

    static string Tool(string exe, string args)
    {
        using (var p = new System.Diagnostics.Process())
        {
            p.StartInfo = new System.Diagnostics.ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = dir };
            p.ErrorDataReceived += delegate { };
            p.Start(); p.BeginErrorReadLine(); string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(); Check(p.ExitCode == 0, "fixture tool failed"); return output;
        }
    }

    public static void Mux(string tools)
    {
        if (!File.Exists(Path.Combine(tools, "ffmpeg.exe")) || !File.Exists(Path.Combine(tools, "ffprobe.exe")))
        { Console.WriteLine("Subtitle mux: skipped, ffmpeg/ffprobe not found"); return; }
        Setting("BinDir", tools);
        string ffmpeg = Path.Combine(tools, "ffmpeg.exe"), probeExe = Path.Combine(tools, "ffprobe.exe");
        var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start(); int port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
        using (var listener = new HttpListener())
        {
            listener.Prefixes.Add("http://127.0.0.1:" + port + "/"); listener.Start();
            foreach (string fmt in new[] { "mkv", "mp4", "webm", "failed.mkv" })
            {
                string file = Path.Combine(dir, "mux." + fmt);
                if (fmt.StartsWith("failed")) File.WriteAllText(file, "not a valid video");
                else Tool(ffmpeg, "-nostdin -y -f lavfi -i color=c=black:s=32x32:d=1 -an -c:v " + (fmt == "webm" ? "libvpx-vp9" : "libx264") + " \"" + file + "\"");
                string subtitleUrl = "http://127.0.0.1:" + port + "/caption.vtt";
                object media = new JavaScriptSerializer().Deserialize(Body("https://cdn.example/play.m3u8", subtitleUrl), T("BrowserMedia"));
                object job = Call(T("BrowserMedia"), media, "Job", false, 0, fmt);
                Set(job, "File", file); Set(job, "MediaKind", "HLS");
                Task serve = Task.Run(async delegate
                {
                    var context = await listener.GetContextAsync();
                    Check(context.Request.Headers["Cookie"] == "sub=value", "subtitle download cookies");
                    Check(context.Request.UserAgent == "Test agent" && context.Request.Url.AbsolutePath == "/caption.vtt", "subtitle download agent and URL");
                    Check(context.Request.UrlReferrer.AbsoluteUri == "https://page.example/recording", "subtitle referer");
                    byte[] bytes = Encoding.UTF8.GetBytes("WEBVTT\n\n00:00:00.000 --> 00:00:00.800\nHello\n");
                    context.Response.ContentType = "text/vtt"; context.Response.ContentLength64 = bytes.Length;
                    await context.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length); context.Response.Close();
                });
                Call(T("CapturedMedia"), null, "Attach", job); serve.GetAwaiter().GetResult();
                string sidecar = Path.Combine(dir, Path.GetFileNameWithoutExtension(file) + ".und.1.srt");
                if (fmt.StartsWith("failed"))
                {
                    Check(File.Exists(sidecar), "failed embedding keeps SRT");
                    Call(T("Runner"), null, "CleanSidecars", file); Check(File.Exists(sidecar), "cleanup keeps failed embedding SRT");
                }
                else
                {
                    string codec = Tool(probeExe, "-v error -select_streams s -show_entries stream=codec_name -of csv=p=0 \"" + file + "\"").Trim();
                    Check(codec.Contains(fmt == "mp4" ? "mov_text" : fmt == "webm" ? "webvtt" : "subrip"), "embedded subtitle codec for " + fmt);
                    Check(!File.Exists(sidecar), "successful mux removes its sidecar");
                }
            }
        }
        Console.WriteLine("Subtitle mux: real ffmpeg MKV/SRT, MP4/mov_text, WebM/WebVTT, request headers and SRT preservation on failure passed");
    }

    public static void Direct(string tools)
    {
        string yt = Path.Combine(tools, "yt-dlp.exe"), ffmpeg = Path.Combine(tools, "ffmpeg.exe");
        if (!File.Exists(yt) || !File.Exists(ffmpeg)) { Console.WriteLine("Direct yt-dlp: skipped, tools not found"); return; }
        Setting("BinDir", tools);
        string fixture = Path.Combine(dir, "direct-fixture.mp4");
        Tool(ffmpeg, "-nostdin -y -f lavfi -i color=c=black:s=32x32:d=1 -an -c:v libx264 \"" + fixture + "\"");
        var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start(); int port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
        using (var listener = new HttpListener())
        {
            listener.Prefixes.Add("http://127.0.0.1:" + port + "/"); listener.Start();
            bool stop = false; int videos = 0, subtitles = 0;
            Task serve = Task.Run(async delegate
            {
                while (!stop)
                {
                    HttpListenerContext context;
                    try { context = await listener.GetContextAsync(); } catch { break; }
                    bool video = context.Request.Url.AbsolutePath.EndsWith(".mp4");
                    Check(context.Request.Headers["Cookie"] == (video ? "media=value" : "sub=value"), "direct per-resource cookies");
                    Check(context.Request.UserAgent == "Test agent" && context.Request.UrlReferrer.AbsoluteUri == "https://page.example/recording", "direct resource headers");
                    byte[] bytes = video ? File.ReadAllBytes(fixture) : Encoding.UTF8.GetBytes("WEBVTT\n\n00:00:00.000 --> 00:00:00.800\nHello\n");
                    context.Response.ContentLength64 = bytes.Length; context.Response.ContentType = video ? "video/mp4" : "text/vtt";
                    if (video) videos++; else subtitles++;
                    try { await context.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length); } finally { context.Response.Close(); }
                }
            });
            string prefix = "http://127.0.0.1:" + port;
            object media = new JavaScriptSerializer().Deserialize(Body(prefix + "/video.mp4", prefix + "/caption.vtt"), T("BrowserMedia"));
            object job = Call(T("BrowserMedia"), media, "Job", false, 0, "mkv"); Set(job, "Force", true);
            try
            {
                Call(T("Runner"), null, "Start", job);
                var timer = System.Diagnostics.Stopwatch.StartNew();
                while (Convert.ToInt32(job.GetType().GetField("State", Flags).GetValue(job)) < 3 && timer.ElapsedMilliseconds < 45000) System.Threading.Thread.Sleep(50);
                int state = Convert.ToInt32(job.GetType().GetField("State", Flags).GetValue(job));
                if (state < 3) Call(T("Runner"), null, "Cancel", job);
                Check(state == 3, "direct yt-dlp download: " + job.GetType().GetField("Error", Flags).GetValue(job));
                string file = (string)job.GetType().GetField("File", Flags).GetValue(job);
                Check(File.Exists(file) && videos > 0 && subtitles > 0, "direct video and subtitles downloaded");
                Check(Tool(Path.Combine(tools, "ffprobe.exe"), "-v error -select_streams s -show_entries stream=codec_name -of csv=p=0 \"" + file + "\"").Contains("subrip"), "direct embedded subtitle");
            }
            finally { stop = true; listener.Stop(); serve.GetAwaiter().GetResult(); }
        }
        Console.WriteLine("Direct yt-dlp: actual captured download via info JSON, separate video/subtitle cookies, headers and embedded subtitles passed");
    }
}
'@
try {
    [BrowserChecks]::Run($assemblyPath, $testDir)
    [BrowserChecks]::Mux($FfmpegDir)
    [BrowserChecks]::Direct($FfmpegDir)
}
finally {
    $safeRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../bin')) + [IO.Path]::DirectorySeparatorChar
    $safeTarget = [IO.Path]::GetFullPath($testDir)
    if (-not $safeTarget.StartsWith($safeRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'Test cleanup outside bin refused' }
    Remove-Item -LiteralPath $safeTarget -Recurse -Force
}

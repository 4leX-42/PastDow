param(
    [string]$Assembly = (Join-Path $PSScriptRoot '../bin/verify-browser/PastDow.exe'),
    [string]$FfmpegDir = (Join-Path $env:LOCALAPPDATA 'PastDow/bin')
)
$ErrorActionPreference = 'Stop'
$assemblyPath = (Resolve-Path -LiteralPath $Assembly).Path
$testDir = Join-Path $PSScriptRoot '../bin/browser-tests'
New-Item -ItemType Directory -Force $testDir | Out-Null
$testDir = (Resolve-Path -LiteralPath $testDir).Path
Add-Type -AssemblyName System.Web.Extensions
Add-Type -ReferencedAssemblies System.Web.Extensions,System.Core -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq.Expressions;
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
    static int received, queued;
    const BindingFlags Flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
    static Type T(string name) { return app.GetType("PastDow." + name, true); }
    static void Set(object target, string name, object value) { target.GetType().GetField(name, Flags).SetValue(target, value); }
    static void Setting(string name, object value) { T("Settings").GetField(name, Flags).SetValue(null, value); }
    static object Call(Type type, object target, string method, params object[] args) { return type.GetMethod(method, Flags).Invoke(target, args); }
    static void Check(bool value, string label) { if (!value) throw new Exception(label); }
    static Task Receive(object media, bool queue) { received++; if (queue) queued++; return Task.FromResult(true); }
    static string Body(string url, string subtitle)
    {
        return new JavaScriptSerializer().Serialize(new { url = url, page = "https://page.example/recording", title = "Recording", kind = "MP4", size = 900000,
            userAgent = "Test agent", cookie = "media=value", subtitles = new[] { new { url = subtitle, lang = "und", cookie = "sub=value", ext = "vtt" } } });
    }
    static int Request(HttpListener listener, object receiver, int port, string method, string path, string key, string body)
    {
        Task handling = Task.Run(async delegate { var context = await listener.GetContextAsync(); await (Task)Call(T("BrowserLink"), receiver, "Handle", context); });
        var req = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + port + path);
        req.Method = method; req.Proxy = null; req.Timeout = 5000;
        if (key != null) req.Headers["X-PastDow-Key"] = key;
        if (body != null)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(body); req.ContentLength = bytes.Length; req.ContentType = "application/json";
            using (var stream = req.GetRequestStream()) stream.Write(bytes, 0, bytes.Length);
        }
        int status;
        try { using (var response = (HttpWebResponse)req.GetResponse()) status = (int)response.StatusCode; }
        catch (WebException ex) { using (var response = (HttpWebResponse)ex.Response) status = (int)response.StatusCode; }
        handling.GetAwaiter().GetResult();
        return status;
    }
    public static void Run(string assembly, string directory)
    {
        app = Assembly.LoadFile(assembly); dir = directory;
        Setting("Home", dir); Setting("BrowserKey", new string('a', 64));
        Setting("VideoDir", dir); Setting("AudioDir", dir);
        var js = new JavaScriptSerializer();
        string body = Body("https://cdn.example/video.mp4?token=fresh", "https://subs.example/caption.vtt?token=fresh");
        Type mediaType = T("BrowserMedia"); object media = js.Deserialize(body, mediaType);
        Check((bool)Call(mediaType, media, "Valid"), "valid media");
        Check(!(bool)Call(mediaType, js.Deserialize(Body("file:///bad.mp4", "https://subs.example/a.vtt"), mediaType), "Valid"), "reject file media");
        Check(!(bool)Call(mediaType, js.Deserialize(Body("https://cdn.example/a.mp4", "javascript:bad"), mediaType), "Valid"), "reject subtitle scheme");
        Check(!(bool)Call(mediaType, js.Deserialize(body.Replace("media=value", "media=value\\r\\nInjected: yes"), mediaType), "Valid"), "reject header injection");
        object job = Call(mediaType, media, "Job", false, 1080, "mkv");
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
        var protocol = js.Deserialize<Dictionary<string, object>>(body); protocol["key"] = new string('a', 64);
        string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(js.Serialize(protocol))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        Check(Call(mediaType, null, "Protocol", "pastdow://add?d=" + encoded) != null, "protocol accepts paired payload");
        Setting("BrowserKey", new string('b', 64));
        Check(Call(mediaType, null, "Protocol", "pastdow://add?d=" + encoded) == null, "protocol rejects wrong key");
        Setting("BrowserKey", new string('a', 64));
        Check(Call(mediaType, null, "Protocol", "pastdow://add?d=broken") == null, "protocol rejects malformed payload");
        Check((string)Call(mediaType, null, "Language", "https://subs.example/caption.eng.vtt") == "en", "language guess");
        string video = Path.Combine(dir, "clean.mkv"), sub = Path.Combine(dir, "clean.und.srt"), image = Path.Combine(dir, "clean.jpg");
        File.WriteAllText(video, "test"); File.WriteAllText(sub, "test"); File.WriteAllText(image, "test");
        Call(T("Runner"), null, "CleanSidecars", video);
        Check(File.Exists(sub) && !File.Exists(image), "preserve subtitle sidecars");
        Type receiverType = T("BrowserLink");
        Type callbackType = typeof(Func<,,>).MakeGenericType(mediaType, typeof(bool), typeof(Task));
        var p = Expression.Parameter(mediaType, "media"); var q = Expression.Parameter(typeof(bool), "queue");
        Delegate callback = Expression.Lambda(callbackType, Expression.Call(typeof(BrowserChecks).GetMethod("Receive", Flags), Expression.Convert(p, typeof(object)), q), p, q).Compile();
        object receiver = Activator.CreateInstance(receiverType, Flags, null, new object[] { callback }, null);
        // Use a separate ephemeral port, never the user's running PastDow endpoint.
        var probe = new TcpListener(IPAddress.Loopback, 0); probe.Start(); int port = ((IPEndPoint)probe.LocalEndpoint).Port; probe.Stop();
        using (var listener = new HttpListener())
        {
            listener.Prefixes.Add("http://127.0.0.1:" + port + "/"); listener.Start();
            Check(Request(listener, receiver, port, "GET", "/ping", null, null) == 403, "reject missing key");
            Check(Request(listener, receiver, port, "GET", "/ping", "bad", null) == 403, "reject wrong key");
            string key = new string('a', 64);
            Check(Request(listener, receiver, port, "GET", "/ping", key, null) == 200, "authenticated ping");
            Check(Request(listener, receiver, port, "POST", "/found", key, body) == 200 && received == 1 && queued == 0, "found without queue");
            Check(Request(listener, receiver, port, "POST", "/queue", key, body) == 200 && received == 2 && queued == 1, "queue");
            Check(Request(listener, receiver, port, "POST", "/queue", key, Body("file:///bad", "https://subs.example/a.vtt")) == 400 && queued == 1, "endpoint validates URLs");
            Check(Request(listener, receiver, port, "POST", "/found", key, "malformed") == 400, "malformed JSON");
            Check(Request(listener, receiver, port, "GET", "/found", key, null) == 404, "wrong method");
        }
        ((IDisposable)receiver).Dispose();
        Console.WriteLine("Browser: validation, header injection, job conversion, direct info JSON, subtitle headers, arguments, protocol auth, sidecar preservation and isolated HTTP endpoint passed");
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
[BrowserChecks]::Run($assemblyPath, $testDir)
[BrowserChecks]::Mux($FfmpegDir)
[BrowserChecks]::Direct($FfmpegDir)

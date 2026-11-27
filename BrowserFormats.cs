using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace PastDow
{
    sealed class BrowserChoice
    {
        public static readonly int[] Heights = { 0, 2160, 1440, 1080, 720, 480, 360 };
        public bool Audio;
        public int Height;
        public string Format;

        public static BrowserChoice From(BrowserMedia media)
        {
            bool audio = media.audio is bool b ? b : Settings.Audio;
            int height = media.q is int n && Heights.Contains(n) ? n : Settings.MaxHeight;
            if (!Heights.Contains(height)) height = 0;
            string fmt = media.fmt as string;
            if (!(audio ? Settings.AudioFormats : Settings.VideoFormats).Contains(fmt))
                fmt = audio ? Settings.AudioFormat : Settings.VideoFormat;
            return new BrowserChoice { Audio = audio, Height = audio ? 0 : height, Format = fmt };
        }
    }

    /// <summary>Read-only format probes. The runner is replaceable so tests never launch the real engine.</summary>
    sealed class BrowserFormats
    {
        public Func<string, Task<string>> Run = RunEngine;
        sealed class Entry { public DateTime Until; public Task<Dictionary<string, object>> Task; }
        readonly Dictionary<string, Entry> cache = new Dictionary<string, Entry>();

        public async Task<Dictionary<string, object>> Get(string url)
        {
            if (!BrowserMedia.Http(url)) throw new ArgumentException("Invalid page URL");
            Entry entry;
            lock (cache)
            {
                foreach (var old in cache.Where(x => x.Value.Until <= DateTime.UtcNow).Select(x => x.Key).ToArray()) cache.Remove(old);
                if (!cache.TryGetValue(url, out entry))
                {
                    if (cache.Count >= 200) cache.Remove(cache.OrderBy(x => x.Value.Until).First().Key);
                    entry = new Entry { Until = DateTime.UtcNow.AddMinutes(10), Task = Probe(url) };
                    cache[url] = entry;
                }
            }
            try { return new Dictionary<string, object>(await entry.Task.ConfigureAwait(false)); }
            catch
            {
                lock (cache) { if (cache.TryGetValue(url, out var current) && current == entry) cache.Remove(url); }
                throw;
            }
        }

        async Task<Dictionary<string, object>> Probe(string url) => Parse(await Run(url).ConfigureAwait(false));

        public static Dictionary<string, object> Parse(string text)
        {
            var root = new JavaScriptSerializer { MaxJsonLength = 33554432 }.Deserialize<Dictionary<string, object>>(text);
            if (root == null || !root.TryGetValue("formats", out var formats) || !(formats is System.Collections.IList list))
                throw new ArgumentException("No format information");
            var heights = new HashSet<int>(); bool audio = false;
            foreach (var f in list.Cast<object>().OfType<Dictionary<string, object>>())
            {
                if (f.TryGetValue("acodec", out var ac) && ac is string a && a != "none" && a.Length > 0) audio = true;
                if (f.TryGetValue("vcodec", out var vc) && (Equals(vc, "none") || Equals(vc, "images"))) continue;
                if (f.TryGetValue("height", out var value) && int.TryParse(Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture), out var height) && height > 0 && height <= 16384)
                    heights.Add(height);
            }
            double duration = 0;
            if (root.TryGetValue("duration", out var d)) double.TryParse(Convert.ToString(d, System.Globalization.CultureInfo.InvariantCulture), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out duration);
            return new Dictionary<string, object>
            {
                ["heights"] = heights.OrderByDescending(h => h).ToArray(), ["audio"] = audio,
                ["title"] = root.TryGetValue("title", out var t) && t is string title ? title.Substring(0, Math.Min(title.Length, 4096)) : "",
                ["duration"] = !double.IsNaN(duration) && duration >= 0 && duration <= 86400 ? duration : 0
            };
        }

        static async Task<string> RunEngine(string url)
        {
            string args = "--ignore-config -J --no-playlist --skip-download --no-cache-dir --no-warnings --socket-timeout 20 --encoding utf-8 --js-runtimes " + Runner.Quote("deno:" + Engine.Deno) + " -- " + Runner.Quote(url);
            using (var process = Process.Start(Engine.Hidden(Engine.YtDlp, args)))
            {
                var output = process.StandardOutput.ReadToEndAsync();
                var error = process.StandardError.ReadToEndAsync();
                var exited = Task.Run(() => process.WaitForExit());
                var done = Task.WhenAll(output, error, exited);
                if (await Task.WhenAny(done, Task.Delay(25000)).ConfigureAwait(false) != done)
                {
                    // This Process is the one started above, never an installed app or a browser.
                    try { process.Kill(); } catch { }
                    throw new TimeoutException("Format lookup timed out");
                }
                await done.ConfigureAwait(false);
                if (process.ExitCode != 0) throw new InvalidOperationException("Could not read available formats");
                return await output.ConfigureAwait(false);
            }
        }
    }
}

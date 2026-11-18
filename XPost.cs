using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Web.Script.Serialization;

namespace PastDow
{
    /// <summary>
    /// X (Twitter) only hands out a post's video to signed-in users, so yt-dlp alone fails with
    /// "No video could be found in this tweet". The public FixTweet API (api.fxtwitter.com, the service behind
    /// fxtwitter/fixupx link previews) returns the same video files without a session. PastDow turns that answer
    /// into a yt-dlp info JSON and runs yt-dlp with --load-info-json, so quality choice, metadata, cover, folders
    /// and the download archive work as for any other site. If the API fails, the job falls back to yt-dlp directly.
    /// </summary>
    static class XPost
    {
        static readonly Regex Link = new Regex(
            @"^https?://(?:www\.|mobile\.)?(?:x|twitter|fixupx|fxtwitter|vxtwitter|fixvx)\.com/(?:[^/?#]+/status(?:es)?|i/web/status|i/status)/(\d+)",
            RegexOptions.IgnoreCase);
        static readonly Regex Size = new Regex(@"/(\d+)x(\d+)/");
        static readonly Regex TcoLink = new Regex(@"\s*https?://t\.co/\S+");

        public static bool Is(string url) => Link.IsMatch(url ?? "");

        /// <summary>Writes the info JSON for the post's videos and returns its path, or null to let yt-dlp try the link itself.</summary>
        public static string Resolve(string url, int jobId)
        {
            try
            {
                var m = Link.Match(url ?? "");
                if (!m.Success) return null;
                string id = m.Groups[1].Value;

                var req = (HttpWebRequest)WebRequest.Create("https://api.fxtwitter.com/status/" + id);
                req.UserAgent = "PastDow";
                req.Timeout = 20000;
                req.ReadWriteTimeout = 20000;
                string body;
                using (var resp = req.GetResponse())
                using (var r = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    body = r.ReadToEnd();

                var js = new JavaScriptSerializer { MaxJsonLength = int.MaxValue };
                var root = js.DeserializeObject(body) as Dictionary<string, object>;
                var tweet = Get(root, "tweet") as Dictionary<string, object>;
                var media = Get(tweet, "media") as Dictionary<string, object>;
                var all = Get(media, "all") as IEnumerable;
                if (all == null) return null;

                var videos = new List<Dictionary<string, object>>();
                foreach (var o in all)
                    if (o is Dictionary<string, object> v && (Str(v, "type") == "video" || Str(v, "type") == "gif")) videos.Add(v);
                if (videos.Count == 0) return null;

                var author = Get(tweet, "author") as Dictionary<string, object>;
                string name = Str(author, "name"), handle = Str(author, "screen_name");
                string text = Regex.Replace(TcoLink.Replace(Str(tweet, "text") ?? "", ""), @"\s+", " ").Trim();
                if (text.Length > 100) text = text.Substring(0, 100).TrimEnd() + "…";
                string title = string.IsNullOrEmpty(text) ? (name ?? handle ?? "X") + " - video" : (name ?? handle ?? "X") + " - " + text;
                string page = Str(tweet, "url") ?? url;
                object stamp = Get(tweet, "created_timestamp");

                var entries = new List<object>();
                for (int i = 0; i < videos.Count; i++)
                {
                    var v = videos[i];
                    bool gif = Str(v, "type") == "gif";
                    var formats = new List<object>();
                    if (Get(v, "formats") is IEnumerable fl)
                        foreach (var fo in fl)
                        {
                            if (!(fo is Dictionary<string, object> f) || Str(f, "container") != "mp4") continue;
                            string u = Str(f, "url");
                            if (string.IsNullOrEmpty(u)) continue;
                            formats.Add(Format(u, Get(f, "bitrate"), gif));
                        }
                    if (formats.Count == 0 && !string.IsNullOrEmpty(Str(v, "url"))) formats.Add(Format(Str(v, "url"), null, gif));
                    if (formats.Count == 0) continue;
                    entries.Add(new Dictionary<string, object>
                    {
                        ["id"] = videos.Count == 1 ? id : id + "_" + (i + 1),
                        ["title"] = videos.Count == 1 ? title : title + " (" + (i + 1) + ")",
                        ["uploader"] = name, ["uploader_id"] = handle, ["channel"] = name,
                        ["timestamp"] = stamp, ["duration"] = Get(v, "duration"),
                        ["thumbnail"] = Str(v, "thumbnail_url"),
                        ["webpage_url"] = page, ["original_url"] = url,
                        ["extractor"] = "twitter", ["extractor_key"] = "Twitter",
                        ["formats"] = formats
                    });
                }
                if (entries.Count == 0) return null;

                object info = entries.Count == 1 ? entries[0] : new Dictionary<string, object>
                {
                    ["_type"] = "playlist", ["id"] = id, ["title"] = title,
                    ["webpage_url"] = page, ["extractor"] = "twitter", ["extractor_key"] = "Twitter",
                    ["entries"] = entries
                };
                string dir = Path.Combine(Settings.Home, "tmp");
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, "x-" + id + "-" + jobId + ".info.json");
                File.WriteAllText(path, js.Serialize(info), new UTF8Encoding(false));
                return path;
            }
            catch { return null; }
        }

        static Dictionary<string, object> Format(string url, object bitrate, bool gif)
        {
            var f = new Dictionary<string, object>
            {
                ["url"] = url, ["ext"] = "mp4", ["protocol"] = "https",
                ["vcodec"] = "avc1", ["acodec"] = gif ? "none" : "mp4a"
            };
            double kbps = 0;
            if (bitrate != null) try { kbps = Convert.ToDouble(bitrate) / 1000; } catch { }
            f["format_id"] = kbps > 0 ? "http-" + (int)kbps : "http";
            if (kbps > 0) f["tbr"] = kbps;
            var s = Size.Match(url);
            if (s.Success) { f["width"] = int.Parse(s.Groups[1].Value); f["height"] = int.Parse(s.Groups[2].Value); }
            return f;
        }

        static object Get(Dictionary<string, object> d, string k) => d != null && d.TryGetValue(k, out var v) ? v : null;
        static string Str(Dictionary<string, object> d, string k) => Get(d, k) as string;
    }
}

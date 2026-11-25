using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace PastDow
{
    /// <summary>Feeds direct files to yt-dlp and keeps separately caught subtitle tracks.</summary>
    static class CapturedMedia
    {
        public static bool Manifest(Job j) => (j.MediaKind ?? "").IndexOf("HLS", StringComparison.OrdinalIgnoreCase) >= 0 ||
            (j.MediaKind ?? "").IndexOf("DASH", StringComparison.OrdinalIgnoreCase) >= 0 ||
            System.Text.RegularExpressions.Regex.IsMatch(new Uri(j.Url).AbsolutePath, @"\.(m3u8|mpd)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        static Dictionary<string, string> Headers(Job j, string cookie)
        {
            var h = new Dictionary<string, string>();
            if (!string.IsNullOrEmpty(j.Referer)) h["Referer"] = j.Referer;
            if (!string.IsNullOrEmpty(j.UserAgent)) h["User-Agent"] = j.UserAgent;
            if (!string.IsNullOrEmpty(cookie)) h["Cookie"] = cookie;
            return h;
        }

        public static string SubtitleExt(BrowserSubtitle s) => s.ext ?? (new Uri(s.url).AbsolutePath.EndsWith(".srt", StringComparison.OrdinalIgnoreCase) ? "srt" : "vtt");

        public static string Info(Job j)
        {
            string id;
            using (var hash = SHA256.Create()) id = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(new Uri(j.Url).GetLeftPart(UriPartial.Path)))).Replace("-", "").Substring(0, 16).ToLowerInvariant();
            string ext = Path.GetExtension(new Uri(j.Url).AbsolutePath).TrimStart('.').ToLowerInvariant();
            var extensions = new[] { "mp4", "m4v", "webm", "mov", "mkv", "mp3", "m4a", "ogg", "wav", "opus" };
            if (!extensions.Contains(ext))
            {
                string kind = (j.MediaKind ?? "").ToLowerInvariant();
                ext = extensions.Contains(kind) ? kind : kind == "mpeg" ? "mp3" : kind == "x-m4a" ? "m4a" : "mp4";
            }
            var subs = new Dictionary<string, List<object>>();
            foreach (var s in j.Subtitles)
            {
                string lang = s.lang ?? "und";
                if (!subs.ContainsKey(lang)) subs[lang] = new List<object>();
                subs[lang].Add(new { url = s.url, ext = SubtitleExt(s), http_headers = Headers(j, s.cookie) });
            }
            var info = new { id, title = string.IsNullOrWhiteSpace(j.Title) ? "Browser video" : j.Title,
                webpage_url = j.Page, url = j.Url, ext, http_headers = Headers(j, j.Cookie), subtitles = subs,
                extractor = "pastdow", extractor_key = "PastDow" };
            string dir = Path.Combine(Settings.Home, "tmp");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, "capture-" + j.Id + "-" + Guid.NewGuid().ToString("N") + ".info.json");
            File.WriteAllText(path, new JavaScriptSerializer().Serialize(info), new UTF8Encoding(false));
            return path;
        }

        /// <summary>Only local subtitle files reach ffmpeg; failed embedding preserves the sidecars.</summary>
        public static void Attach(Job j)
        {
            if (j.Audio || j.Subtitles.Count == 0 || !Manifest(j) || !File.Exists(j.File)) return;
            j.State = JobState.Processing;
            var tracks = new List<KeyValuePair<string, string>>();
            string fmt = Path.GetExtension(j.File).ToLowerInvariant();
            string output = Path.Combine(Path.GetDirectoryName(j.File), ".pastdow-" + Guid.NewGuid().ToString("N") + fmt);
            try
            {
                int index = 0;
                foreach (var s in j.Subtitles)
                {
                    if (j.CancelRequested) return;
                    string stem = Path.Combine(Path.GetDirectoryName(j.File), Path.GetFileNameWithoutExtension(j.File)) + "." + (s.lang ?? "und") + "." + (++index);
                    string raw = stem + ".source." + SubtitleExt(s), sidecar = stem + ".srt";
                    try
                    {
                        Fetch(j, s, raw);
                        if (Run(j, new[] { "-nostdin", "-y", "-i", raw, "-c:s", "srt", sidecar }))
                        {
                            tracks.Add(new KeyValuePair<string, string>(sidecar, s.lang ?? "und"));
                            File.Delete(raw);
                        }
                        else j.SubtitleNote = "Subtitle conversion failed, source kept beside the video";
                    }
                    catch { j.SubtitleNote = "Some subtitles could not be downloaded, replay the page and try again"; }
                }
                if (tracks.Count == 0 || j.CancelRequested) return;
                var args = new List<string> { "-nostdin", "-y", "-i", j.File };
                foreach (var t in tracks) args.AddRange(new[] { "-i", t.Key });
                args.AddRange(new[] { "-map", "0" });
                for (int i = 0; i < tracks.Count; i++) args.AddRange(new[] { "-map", (i + 1) + ":0" });
                args.AddRange(new[] { "-c", "copy" });
                args.AddRange(new[] { "-c:s", fmt == ".mp4" ? "mov_text" : fmt == ".webm" ? "webvtt" : "srt" });
                int streams = StreamCount(j);
                for (int i = 0; i < tracks.Count; i++) args.AddRange(new[] { "-metadata:s:" + (streams + i), "language=" + Iso3(tracks[i].Value) });
                args.Add(output);
                if (!Run(j, args) || j.CancelRequested) { j.SubtitleNote = "Subtitles kept beside the video, embedding failed"; return; }
                File.Replace(output, j.File, null);
                foreach (var t in tracks) File.Delete(t.Key);
            }
            catch { j.SubtitleNote = "Subtitles kept beside the video, embedding failed"; }
            finally { try { if (File.Exists(output)) File.Delete(output); } catch { } }
        }

        static int StreamCount(Job j)
        {
            int count = 0;
            using (var p = new Process { StartInfo = Engine.Hidden(Path.Combine(Settings.BinDir, "ffprobe.exe"),
                "-v error -show_entries stream=index -of csv=p=0 " + Runner.Quote(j.File)) })
            {
                p.OutputDataReceived += (s, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) count++; };
                p.ErrorDataReceived += (s, e) => { };
                j.Proc = p;
                try
                {
                    p.Start(); p.BeginOutputReadLine(); p.BeginErrorReadLine();
                    if (!p.WaitForExit(15000)) { try { p.Kill(); } catch { } throw new IOException("Could not read video tracks"); }
                    p.WaitForExit();
                    if (p.ExitCode != 0 || count == 0) throw new IOException("Could not read video tracks");
                    return count;
                }
                finally { j.Proc = null; }
            }
        }

        static void Fetch(Job j, BrowserSubtitle sub, string path)
        {
            string url = sub.url;
            for (int redirects = 0; redirects < 6; redirects++)
            {
                if (!BrowserMedia.Http(url)) throw new IOException("Invalid subtitle URL");
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Timeout = req.ReadWriteTimeout = 30000;
                req.AllowAutoRedirect = false;
                req.Referer = j.Referer;
                req.UserAgent = j.UserAgent;
                if (new Uri(url).Authority == new Uri(sub.url).Authority && !string.IsNullOrEmpty(sub.cookie)) req.Headers[HttpRequestHeader.Cookie] = sub.cookie;
                using (var response = (HttpWebResponse)req.GetResponse())
                {
                    if ((int)response.StatusCode >= 300 && (int)response.StatusCode < 400)
                    {
                        url = new Uri(new Uri(url), response.Headers["Location"]).AbsoluteUri;
                        continue;
                    }
                    using (var input = response.GetResponseStream())
                    using (var file = File.Create(path))
                    {
                        var buffer = new byte[8192]; int n, total = 0;
                        while ((n = input.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            if (j.CancelRequested) throw new OperationCanceledException();
                            total += n;
                            if (total > 20 * 1024 * 1024) throw new IOException("Subtitle too large");
                            file.Write(buffer, 0, n);
                        }
                    }
                    return;
                }
            }
            throw new IOException("Too many subtitle redirects");
        }

        static bool Run(Job j, IEnumerable<string> args)
        {
            if (j.CancelRequested) return false;
            using (var p = new Process { StartInfo = Engine.Hidden(Path.Combine(Settings.BinDir, "ffmpeg.exe"), string.Join(" ", args.Select(Runner.Quote))) })
            {
                j.Proc = p;
                p.OutputDataReceived += (s, e) => { };
                p.ErrorDataReceived += (s, e) => { };
                try
                {
                    p.Start(); p.BeginOutputReadLine(); p.BeginErrorReadLine();
                    if (j.CancelRequested) { try { p.Kill(); } catch { } }
                    if (!p.WaitForExit(120000)) { try { p.Kill(); } catch { } return false; }
                    p.WaitForExit();
                    return p.ExitCode == 0;
                }
                finally { j.Proc = null; }
            }
        }

        /// <summary>Matroska and MP4 tag languages with ISO 639-2 codes (spa, eng); players show "es" as unknown.</summary>
        static string Iso3(string lang)
        {
            switch ((lang ?? "").ToLowerInvariant())
            {
                case "es": return "spa"; case "en": return "eng"; case "pt": return "por"; case "fr": return "fra";
                case "de": return "deu"; case "it": return "ita"; case "ca": return "cat"; case "und": case "": return "und";
                default: return lang;
            }
        }
    }
}

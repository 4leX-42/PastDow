using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Ulvark
{
    enum JobState { Queued, Running, Processing, Done, Skipped, Failed, Canceled }

    /// <summary>One download. The process reader thread writes the fields; the UI paints them on a timer.</summary>
    sealed class Job
    {
        public string Url;
        public bool Audio;
        public int MaxHeight;
        public string Format;       // container / audio codec: mkv, mp4, webm / mp3, m4a, flac, wav, opus
        public bool Force;
        static int nextId;
        public readonly int Id = System.Threading.Interlocked.Increment(ref nextId);
        public volatile JobState State = JobState.Queued;
        public string Title;
        public double Percent;      // 0..100 of the current phase
        public int Phase;           // 1 = video, 2 = audio (separate formats)
        public bool Started;        // first progress line seen; before that the UI shows a spinner
        public string Speed = "", Eta = "";
        public string File;
        public string Error;
        public Process Proc;
        public bool CancelRequested;
        public bool EngineSuspect;  // the error looks like yt-dlp is out of date (site changed), not a user-side problem
        public bool FixTried;       // already retried after an automatic yt-dlp update

        // UI-only animation state
        public readonly long Added = Environment.TickCount;
        public double Shown;        // smoothed percent

        public bool Active => State == JobState.Running || State == JobState.Processing;
        public bool Finished => State >= JobState.Done;

        public string Folder => Audio ? Settings.AudioDir : Settings.VideoDir;

        static readonly CultureInfo En = CultureInfo.InvariantCulture;

        public string Detail
        {
            get
            {
                string kind = Audio ? "MP3" : MaxHeight > 0 ? MaxHeight + "p" : "Best quality";
                switch (State)
                {
                    case JobState.Queued: return kind + "  ·  waiting";
                    case JobState.Running:
                        if (!Started) return kind + "  ·  fetching video info";
                        var sb = new StringBuilder(kind);
                        if (Phase > 1) sb.Append("  ·  part ").Append(Phase);
                        if (Speed.Length > 0) sb.Append("  ·  ").Append(Speed);
                        if (Eta.Length > 0) sb.Append("  ·  ").Append(Eta).Append(" left");
                        return sb.ToString();
                    case JobState.Processing: return kind + "  ·  merging video and audio";
                    case JobState.Done:
                        try
                        {
                            var fi = new FileInfo(File);
                            return kind + "  ·  " + (fi.Length / 1048576.0).ToString("0.0", En) + " MB  ·  " + fi.Extension.TrimStart('.').ToUpperInvariant() + "  ·  double-click to open";
                        }
                        catch { return kind; }
                    case JobState.Skipped: return kind + "  ·  already downloaded (right-click to download again)";
                    case JobState.Canceled: return kind + "  ·  canceled";
                    default: return Error ?? "error";
                }
            }
        }
    }

    /// <summary>Runs yt-dlp (best quality, metadata, subtitles) and reads its progress.</summary>
    static class Runner
    {
        public static event Action<Job> Finished;

        // subtitle files also print a destination; they are not a video/audio part
        static readonly Regex SubFile = new Regex(@"\.(vtt|srt|ass|ttml|srv\d|json3)$", RegexOptions.IgnoreCase);
        static readonly Regex Ansi = new Regex(@"\x1B\[[0-9;]*[A-Za-z]");

        public static void Start(Job j)
        {
            j.State = JobState.Running;
            j.Percent = 0; j.Phase = 0; j.Started = false; j.Speed = j.Eta = ""; j.Error = null; j.File = null;
            try
            {
                Directory.CreateDirectory(j.Folder);
                var psi = Engine.Hidden(Engine.YtDlp, Args(j));
                psi.EnvironmentVariables["PYTHONIOENCODING"] = "utf-8";
                psi.EnvironmentVariables["PYTHONUTF8"] = "1";
                var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
                p.OutputDataReceived += (s, e) => { if (e.Data != null) Line(j, e.Data); };
                p.ErrorDataReceived += (s, e) => { if (e.Data != null) Line(j, e.Data); };
                p.Exited += (s, e) => Exit(j, p);
                j.Proc = p;
                p.Start();
                p.BeginOutputReadLine();
                p.BeginErrorReadLine();
            }
            catch (Exception ex)
            {
                j.State = JobState.Failed;
                j.Error = "Could not start yt-dlp: " + ex.Message;
                Finished?.Invoke(j);
            }
        }

        public static void Cancel(Job j)
        {
            j.CancelRequested = true;
            if (j.State == JobState.Queued) { j.State = JobState.Canceled; return; }
            var p = j.Proc;
            if (p == null) return;
            try
            {
                // whole tree: yt-dlp starts ffmpeg and deno
                using (var k = Process.Start(new ProcessStartInfo("taskkill", "/T /F /PID " + p.Id) { CreateNoWindow = true, UseShellExecute = false }))
                    k.WaitForExit(5000);
            }
            catch { }
        }

        /// <summary>
        /// Subtitles and thumbnails go inside the file; when a container cannot take them (WebM covers, some MP4
        /// subtitles) yt-dlp leaves them next to it. Remove those leftovers so only the video or song remains.
        /// </summary>
        static void CleanSidecars(string file)
        {
            try
            {
                if (file == null || !File.Exists(file)) return;
                string dir = Path.GetDirectoryName(file), stem = Path.GetFileNameWithoutExtension(file);
                foreach (var f in Directory.GetFiles(dir, stem + ".*"))
                {
                    if (string.Equals(f, file, StringComparison.OrdinalIgnoreCase)) continue;
                    string ext = Path.GetExtension(f).ToLowerInvariant();
                    if (ext == ".srt" || ext == ".vtt" || ext == ".ass" || ext == ".webp" || ext == ".jpg" || ext == ".jpeg" || ext == ".png")
                        File.Delete(f);
                }
            }
            catch { }
        }

        static void Exit(Job j, Process p)
        {
            int code = -1;
            try { p.WaitForExit(); code = p.ExitCode; } catch { }
            if (j.CancelRequested) j.State = JobState.Canceled;
            else if (j.State == JobState.Skipped) { }
            else if (code == 0 || (j.File != null && File.Exists(j.File))) j.State = JobState.Done;
            else
            {
                j.State = JobState.Failed;
                if (j.Error == null) j.Error = "yt-dlp exited with code " + code;
            }
            j.Speed = j.Eta = "";
            if (j.State == JobState.Done) CleanSidecars(j.File);
            try { p.Dispose(); } catch { }
            j.Proc = null;
            Finished?.Invoke(j);
        }

        static void Line(Job j, string raw)
        {
            string s = Ansi.Replace(raw, "").Trim();
            if (s.Length == 0) return;
            if (s.StartsWith("VGP|"))
            {
                var f = s.Split('|');
                if (f.Length >= 4)
                {
                    if (double.TryParse(f[1].Trim().TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var pct))
                        j.Percent = Math.Max(0, Math.Min(100, pct));
                    j.Speed = Clean(f[2]);
                    j.Eta = Clean(f[3]);
                    j.Started = true;
                    if (j.State == JobState.Processing) j.State = JobState.Running;
                }
                return;
            }
            if (s.StartsWith("VGT|")) { j.Title = s.Substring(4); return; }
            if (s.StartsWith("VGF|")) { j.File = s.Substring(4); return; }
            if (s.StartsWith("[download] Destination:") && !SubFile.IsMatch(s)) { j.Phase++; j.Percent = 0; j.Shown = 0; j.Started = true; return; }
            if (s.Contains("has already been recorded in the archive")) { j.State = JobState.Skipped; return; }
            if (s.StartsWith("[Merger]") || s.StartsWith("[ExtractAudio]") || s.StartsWith("[EmbedThumbnail]") || s.StartsWith("[Metadata]"))
            {
                j.State = JobState.Processing; j.Percent = 100; return;
            }
            if (s.StartsWith("ERROR:"))
            {
                string msg = s.Substring(6).Trim();
                j.Error = Friendly(msg);
                j.EngineSuspect = !UserSide(msg);
            }
        }

        static string Clean(string v)
        {
            v = v.Trim();
            return v == "NA" || v == "Unknown" || v.StartsWith("Unknown") ? "" : v;
        }

        /// <summary>Errors an update cannot fix: wrong link, private, members-only, region, no network, no such quality.</summary>
        static bool UserSide(string e) =>
            e.Contains("HTTP Error 404") || e.Contains("Private video") || e.Contains("Video unavailable") || e.Contains("is not a valid URL") ||
            e.Contains("Unsupported URL") || e.Contains("getaddrinfo") || e.Contains("timed out") || e.Contains("Requested format is not available") ||
            e.Contains("not available in your country") || e.Contains("members-only") || e.Contains("Join this channel") ||
            e.Contains("Sign in to confirm your age") || e.Contains("No space left") || e.Contains("Permission denied") ||
            e.Contains("logged-in") || e.Contains("login required") || e.Contains("log in") || e.Contains("empty media response") || e.Contains("--cookies");

        static string Friendly(string e)
        {
            if (e.Contains("Sign in to confirm your age")) return "Age-restricted video: YouTube asks to sign in for this one";
            if (e.Contains("Sign in to confirm") && e.Contains("bot")) return "YouTube is asking to verify you are not a bot · try again in a while";
            if (e.Contains("Private video")) return "Private video";
            if (e.Contains("HTTP Error 404")) return "Page not found · check the link";
            if (e.Contains("HTTP Error 403")) return "The site refused the download · try again later";
            if (e.Contains("Unable to download webpage") || e.Contains("getaddrinfo") || e.Contains("timed out")) return "Could not reach that page · check the link or your connection";
            if (e.Contains("Requested format is not available")) return "That quality is not available for this video · try BEST";
            if (e.Contains("is not available in your country") || e.Contains("geo")) return "Not available in your country";
            if (e.Contains("members-only") || e.Contains("Join this channel")) return "Members-only video";
            if (e.Contains("logged-in") || e.Contains("login required") || e.Contains("log in") || e.Contains("empty media response") || e.Contains("--cookies"))
                return "This one needs a login · Ulvark downloads public content only";
            if (e.Contains("Video unavailable")) return "Video unavailable";
            if (e.Contains("is not a valid URL") || e.Contains("Unsupported URL")) return "Unsupported link";
            return e.Length > 160 ? e.Substring(0, 160) + "…" : e;
        }

        /// <summary>yt-dlp arguments: best video+audio by resolution, fps and HDR, MKV with metadata, chapters, thumbnail, subtitles.</summary>
        static string Args(Job j)
        {
            var a = new List<string>
            {
                "--ignore-config", "--no-simulate", "--newline", "--progress", "--no-playlist",
                "--encoding", "utf-8",
                "--ffmpeg-location", Settings.BinDir,
                "--js-runtimes", "deno:" + Engine.Deno,
                "--progress-template", "download:VGP|%(progress._percent_str)s|%(progress._speed_str)s|%(progress._eta_str)s",
                "--print", "video:VGT|%(title)s",
                "--print", "after_move:VGF|%(filepath)s",
                "--no-quiet", // --print turns on --quiet, which hides "already in archive" and the destinations
                "-P", j.Folder,
                "-P", "temp:" + Path.Combine(Settings.Home, "tmp"),
                "--windows-filenames", "--trim-filenames", "200",
                "--embed-metadata", "--embed-thumbnail", "--sponsorblock-mark", "all",
                "--concurrent-fragments", "8", "--retries", "10", "--fragment-retries", "10", "--extractor-retries", "5",
                "--continue", "--no-mtime",
                "--ignore-errors" // a failing subtitle (e.g. HTTP 429) must not fail the whole video
            };
            string fmt = j.Format ?? (j.Audio ? "best" : "mkv");   // audio best = keep the source codec, no re-encode
            if (j.Audio)
            {
                a.AddRange(new[] { "-f", "ba/b", "-x", "--audio-format", fmt, "--audio-quality", "0",
                    "-o", (Settings.ChannelFolders ? "%(uploader,channel,uploader_id|Various)s/" : "") + "%(title).150B.%(ext)s" });
                if (fmt == "wav") a.Remove("--embed-thumbnail");   // WAV has nowhere to keep a cover
            }
            else
            {
                // same resolution and fps first; then prefer streams that fit the container without re-encoding
                if (fmt == "webm") a.Remove("--embed-thumbnail");   // WebM cannot hold a cover image
                string sort = fmt == "mp4" ? "res,fps,hdr,ext:mp4:m4a" : fmt == "webm" ? "res,fps,hdr,ext:webm:webm" : "res,fps,hdr,vcodec,br";
                a.AddRange(new[] { "-f", j.MaxHeight > 0 ? $"bv*[height<={j.MaxHeight}]+ba/b[height<={j.MaxHeight}]" : "bv*+ba/b",
                    "-S", sort, "--merge-output-format", fmt, "--remux-video", fmt,
                    "--embed-chapters", "--embed-subs", "--sub-langs", "es,en,es-orig,en-orig", "--write-subs", "--write-auto-subs",
                    "--convert-subs", fmt == "webm" ? "vtt" : "srt",
                    "-o", (Settings.ChannelFolders ? "%(uploader,channel,uploader_id,playlist_uploader|Various)s/" : "") + "%(title).150B [%(id)s].%(ext)s" });
            }
            if (j.Force) a.Add("--force-overwrites");
            else
            {
                a.Add("--no-overwrites");
                if (Settings.SkipDone) { a.Add("--download-archive"); a.Add(Settings.Archive(j.Audio)); }
            }
            a.Add("--");
            a.Add(j.Url);
            var sb = new StringBuilder();
            foreach (var x in a) { if (sb.Length > 0) sb.Append(' '); sb.Append(Quote(x)); }
            return sb.ToString();
        }

        /// <summary>Quoting per CommandLineToArgvW rules (backslashes before quotes and at the end).</summary>
        public static string Quote(string s)
        {
            if (s.Length > 0 && s.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return s;
            var sb = new StringBuilder("\"");
            int bs = 0;
            foreach (char c in s)
            {
                if (c == '\\') { bs++; continue; }
                if (c == '"') { sb.Append('\\', bs * 2 + 1); sb.Append('"'); }
                else { sb.Append('\\', bs); sb.Append(c); }
                bs = 0;
            }
            sb.Append('\\', bs * 2);
            return sb.Append('"').ToString();
        }
    }
}

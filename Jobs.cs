using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace VidGrab
{
    enum JobState { Queued, Running, Processing, Done, Skipped, Failed, Canceled }

    /// <summary>Una descarga. Los campos los escribe el hilo de lectura del proceso; la UI los pinta con un timer.</summary>
    sealed class Job
    {
        public string Url;
        public bool Audio;
        public int MaxHeight;
        public bool Force;
        public volatile JobState State = JobState.Queued;
        public string Title;
        public double Percent;      // 0..100 de la fase actual
        public int Phase;           // 1 = video, 2 = audio (formatos separados)
        public string Speed = "", Eta = "";
        public string File;
        public string Error;
        public Process Proc;
        public bool CancelRequested;

        public bool Active => State == JobState.Running || State == JobState.Processing;
        public bool Finished => State >= JobState.Done;

        public string Folder => Audio ? Settings.AudioDir : Settings.VideoDir;

        public string Detail
        {
            get
            {
                string kind = Audio ? "mp3" : MaxHeight > 0 ? MaxHeight + "p" : "máxima calidad";
                switch (State)
                {
                    case JobState.Queued: return kind + " · en cola";
                    case JobState.Running:
                        var sb = new StringBuilder(kind);
                        if (Phase > 1) sb.Append(" · parte ").Append(Phase);
                        if (Speed.Length > 0) sb.Append(" · ").Append(Speed);
                        if (Eta.Length > 0) sb.Append(" · quedan ").Append(Eta);
                        return sb.ToString();
                    case JobState.Processing: return kind + " · uniendo vídeo y audio";
                    case JobState.Done:
                        try
                        {
                            var fi = new FileInfo(File);
                            return kind + " · " + (fi.Length / 1048576.0).ToString("0.0", CultureInfo.GetCultureInfo("es-ES")) + " MB · " + fi.Extension.TrimStart('.') + " · doble clic para abrir";
                        }
                        catch { return kind; }
                    case JobState.Skipped: return kind + " · ya lo tenías (clic derecho para bajarlo otra vez)";
                    case JobState.Canceled: return kind + " · cancelado";
                    default: return Error ?? "error";
                }
            }
        }
    }

    /// <summary>Lanza yt-dlp con la configuracion del lab (maxima calidad, metadatos, subtitulos) y lee su progreso.</summary>
    static class Runner
    {
        public static event Action<Job> Finished;

        static readonly Regex Ansi = new Regex(@"\x1B\[[0-9;]*[A-Za-z]");

        public static void Start(Job j)
        {
            j.State = JobState.Running;
            j.Percent = 0; j.Phase = 0; j.Speed = j.Eta = ""; j.Error = null; j.File = null;
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
                j.Error = "No se pudo lanzar yt-dlp: " + ex.Message;
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
                // arbol completo: yt-dlp lanza ffmpeg y deno
                using (var k = Process.Start(new ProcessStartInfo("taskkill", "/T /F /PID " + p.Id) { CreateNoWindow = true, UseShellExecute = false }))
                    k.WaitForExit(5000);
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
                if (j.Error == null) j.Error = "yt-dlp terminó con código " + code;
            }
            j.Speed = j.Eta = "";
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
                    if (j.State == JobState.Processing) j.State = JobState.Running;
                }
                return;
            }
            if (s.StartsWith("VGT|")) { j.Title = s.Substring(4); return; }
            if (s.StartsWith("VGF|")) { j.File = s.Substring(4); return; }
            if (s.StartsWith("[download] Destination:")) { j.Phase++; j.Percent = 0; return; }
            if (s.Contains("has already been recorded in the archive")) { j.State = JobState.Skipped; return; }
            if (s.StartsWith("[Merger]") || s.StartsWith("[ExtractAudio]") || s.StartsWith("[EmbedThumbnail]") || s.StartsWith("[Metadata]"))
            {
                j.State = JobState.Processing; j.Percent = 100; return;
            }
            if (s.StartsWith("ERROR:")) j.Error = Friendly(s.Substring(6).Trim());
        }

        static string Clean(string v)
        {
            v = v.Trim();
            return v == "NA" || v == "Unknown" || v.StartsWith("Unknown") ? "" : v;
        }

        static string Friendly(string e)
        {
            if (e.Contains("Sign in to confirm your age")) return "Vídeo con restricción de edad: activa cookies del navegador (abajo)";
            if (e.Contains("Sign in to confirm") && e.Contains("bot")) return "YouTube pide verificar: activa cookies del navegador (abajo)";
            if (e.Contains("Private video")) return "Vídeo privado";
            if (e.Contains("Video unavailable")) return "Vídeo no disponible";
            if (e.Contains("is not a valid URL") || e.Contains("Unsupported URL")) return "Enlace no soportado";
            if (e.Contains("could not find") && e.Contains("cookies")) return "No encuentro las cookies de ese navegador: cambia o quita la opción de abajo";
            return e.Length > 160 ? e.Substring(0, 160) + "…" : e;
        }

        /// <summary>Argumentos equivalentes a yt-dlp.conf + tools\yt.ps1 del lab descargar-videos.</summary>
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
                "--no-quiet", // --print activa --quiet y esconderia "ya en el archivo" y los destinos
                "-P", j.Folder,
                "-P", "temp:" + Path.Combine(Settings.Home, "tmp"),
                "--windows-filenames", "--trim-filenames", "200",
                "--embed-metadata", "--embed-thumbnail", "--sponsorblock-mark", "all",
                "--concurrent-fragments", "8", "--retries", "10", "--fragment-retries", "10", "--extractor-retries", "5",
                "--continue", "--no-mtime",
                "--ignore-errors" // un subtitulo que falla (p. ej. HTTP 429) no tumba el video
            };
            if (j.Audio)
            {
                a.AddRange(new[] { "-f", "ba/b", "-x", "--audio-format", "mp3", "--audio-quality", "0",
                    "-o", "%(uploader,channel,uploader_id|Varios)s/%(title).150B.%(ext)s" });
            }
            else
            {
                a.AddRange(new[] { "-f", j.MaxHeight > 0 ? $"bv*[height<={j.MaxHeight}]+ba/b[height<={j.MaxHeight}]" : "bv*+ba/b",
                    "-S", "res,fps,hdr,vcodec,br", "--merge-output-format", "mkv", "--remux-video", "mkv",
                    "--embed-chapters", "--embed-subs", "--sub-langs", "es,en,es-orig,en-orig", "--write-subs", "--write-auto-subs", "--convert-subs", "srt",
                    "-o", "%(uploader,channel,uploader_id,playlist_uploader|Varios)s/%(title).150B [%(id)s].%(ext)s" });
            }
            if (j.Force) a.Add("--force-overwrites");
            else
            {
                a.Add("--no-overwrites");
                if (Settings.SkipDone) { a.Add("--download-archive"); a.Add(Settings.Archive(j.Audio)); }
            }
            if (Settings.Cookies != "none") { a.Add("--cookies-from-browser"); a.Add(Settings.Cookies); }
            a.Add("--");
            a.Add(j.Url);
            var sb = new StringBuilder();
            foreach (var x in a) { if (sb.Length > 0) sb.Append(' '); sb.Append(Quote(x)); }
            return sb.ToString();
        }

        /// <summary>Comillas segun las reglas de CommandLineToArgvW (barras antes de comillas y al final).</summary>
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

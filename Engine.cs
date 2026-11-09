using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace VidGrab
{
    /// <summary>
    /// Motor: yt-dlp + ffmpeg + deno (runtime JS que YouTube exige) en %LOCALAPPDATA%\VidGrab\bin.
    /// Se descargan la primera vez de sus releases oficiales y se comprueba el SHA-256 publicado.
    /// No van dentro del .exe: yt-dlp caduca en semanas y se actualiza solo (yt-dlp -U).
    /// </summary>
    static class Engine
    {
        public static string YtDlp => Path.Combine(Settings.BinDir, "yt-dlp.exe");
        public static string Ffmpeg => Path.Combine(Settings.BinDir, "ffmpeg.exe");
        public static string Deno => Path.Combine(Settings.BinDir, "deno.exe");

        public static bool Ready => File.Exists(YtDlp) && File.Exists(Ffmpeg) && File.Exists(Path.Combine(Settings.BinDir, "ffprobe.exe")) && File.Exists(Deno);

        const string YtUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";
        const string YtSums = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/SHA2-256SUMS";
        const string FfUrl = "https://github.com/yt-dlp/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip";
        const string FfSums = "https://github.com/yt-dlp/FFmpeg-Builds/releases/download/latest/checksums.sha256";
        const string DenoUrl = "https://github.com/denoland/deno/releases/latest/download/deno-x86_64-pc-windows-msvc.zip";
        const string DenoSums = DenoUrl + ".sha256sum";

        /// <summary>Descarga lo que falte. progress: (texto, 0..1).</summary>
        public static async Task Install(Action<string, double> progress, CancellationToken ct)
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            Directory.CreateDirectory(Settings.BinDir);
            string tmp = Path.Combine(Settings.Home, "setup");
            Directory.CreateDirectory(tmp);
            try
            {
                if (!File.Exists(YtDlp))
                {
                    string f = Path.Combine(tmp, "yt-dlp.exe");
                    await Fetch(YtUrl, f, p => progress("Descargando yt-dlp", p * 0.15), ct);
                    Verify(f, await FetchText(YtSums, ct), "yt-dlp.exe");
                    Replace(f, YtDlp);
                }
                if (!File.Exists(Ffmpeg) || !File.Exists(Path.Combine(Settings.BinDir, "ffprobe.exe")))
                {
                    string z = Path.Combine(tmp, "ffmpeg.zip");
                    await Fetch(FfUrl, z, p => progress("Descargando ffmpeg", 0.15 + p * 0.6), ct);
                    progress("Comprobando ffmpeg", 0.75);
                    Verify(z, await FetchText(FfSums, ct), "ffmpeg-master-latest-win64-gpl.zip");
                    Extract(z, "ffmpeg.exe", "ffprobe.exe");
                }
                if (!File.Exists(Deno))
                {
                    string z = Path.Combine(tmp, "deno.zip");
                    await Fetch(DenoUrl, z, p => progress("Descargando deno", 0.76 + p * 0.22), ct);
                    Verify(z, await FetchText(DenoSums, ct), "deno-x86_64-pc-windows-msvc.zip");
                    Extract(z, "deno.exe");
                }
                File.WriteAllText(Stamp, DateTime.UtcNow.ToString("o"));
                progress("Listo", 1);
            }
            finally
            {
                try { Directory.Delete(tmp, true); } catch { }
            }
        }

        static string Stamp => Path.Combine(Settings.Home, "update.stamp");

        /// <summary>yt-dlp -U como mucho una vez al dia, en segundo plano. Devuelve la linea de estado o null.</summary>
        public static Task<string> AutoUpdate()
        {
            return Task.Run(() =>
            {
                try
                {
                    if (File.Exists(Stamp) && DateTime.UtcNow - File.GetLastWriteTimeUtc(Stamp) < TimeSpan.FromHours(20)) return null;
                    var psi = Hidden(YtDlp, "-U");
                    using (var p = Process.Start(psi))
                    {
                        string o = p.StandardOutput.ReadToEnd();
                        p.WaitForExit(90000);
                        File.WriteAllText(Stamp, DateTime.UtcNow.ToString("o"));
                        return o.Contains("Updated yt-dlp to") ? "yt-dlp actualizado" : null;
                    }
                }
                catch { return null; }
            });
        }

        public static ProcessStartInfo Hidden(string exe, string args) => new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Settings.Home
        };

        static HttpWebRequest Req(string url)
        {
            var r = (HttpWebRequest)WebRequest.Create(url);
            r.UserAgent = "VidGrab";
            r.Timeout = 30000;
            r.ReadWriteTimeout = 60000;
            return r;
        }

        static async Task Fetch(string url, string dest, Action<double> progress, CancellationToken ct)
        {
            using (var resp = (HttpWebResponse)await Req(url).GetResponseAsync())
            using (var src = resp.GetResponseStream())
            using (var dst = File.Create(dest))
            {
                long total = resp.ContentLength, done = 0;
                var buf = new byte[1 << 16];
                int n, last = -1;
                while ((n = await src.ReadAsync(buf, 0, buf.Length, ct)) > 0)
                {
                    await dst.WriteAsync(buf, 0, n, ct);
                    done += n;
                    if (total > 0)
                    {
                        int pct = (int)(done * 100 / total);
                        if (pct != last) { last = pct; progress(pct / 100.0); }
                    }
                }
            }
        }

        static async Task<string> FetchText(string url, CancellationToken ct)
        {
            using (var resp = await Req(url).GetResponseAsync())
            using (var r = new StreamReader(resp.GetResponseStream()))
            {
                ct.ThrowIfCancellationRequested();
                return await r.ReadToEndAsync();
            }
        }

        /// <summary>Busca el hash en la linea que nombra el fichero (o el unico hash si no lo nombra) y lo compara.</summary>
        static void Verify(string file, string sums, string name)
        {
            string expected = null;
            foreach (var line in sums.Split('\n'))
            {
                var m = Regex.Match(line, "[0-9a-fA-F]{64}");
                if (!m.Success) continue;
                if (line.Contains(name)) { expected = m.Value; break; }
                if (expected == null) expected = m.Value;
            }
            if (expected == null) throw new Exception("No hay suma SHA-256 publicada para " + name);
            string actual;
            using (var s = File.OpenRead(file)) using (var h = SHA256.Create())
                actual = BitConverter.ToString(h.ComputeHash(s)).Replace("-", "");
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new Exception("SHA-256 no coincide en " + name + ": descarga corrupta o manipulada");
        }

        static void Extract(string zip, params string[] names)
        {
            using (var a = ZipFile.OpenRead(zip))
                foreach (var name in names)
                {
                    ZipArchiveEntry hit = null;
                    foreach (var e in a.Entries)
                        if (e.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) { hit = e; break; }
                    if (hit == null) throw new Exception(name + " no esta en " + Path.GetFileName(zip));
                    string tmp = Path.Combine(Settings.BinDir, name + ".new");
                    hit.ExtractToFile(tmp, true);
                    Replace(tmp, Path.Combine(Settings.BinDir, name));
                }
        }

        static void Replace(string src, string dst)
        {
            if (File.Exists(dst)) File.Delete(dst);
            File.Move(src, dst);
        }
    }
}

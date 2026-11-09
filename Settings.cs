using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Ulvark
{
    /// <summary>Settings in %LOCALAPPDATA%\Ulvark\settings.ini (key=value).</summary>
    static class Settings
    {
        public static readonly string Home = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ulvark");
        public static readonly string BinDir = Path.Combine(Home, "bin");
        // one history per mode: getting the audio of something already downloaded as video is not a repeat
        public static string Archive(bool audio) => Path.Combine(Home, audio ? "archive-audio.txt" : "archive-video.txt");
        static readonly string File_ = Path.Combine(Home, "settings.ini");

        public static string VideoDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "Ulvark");
        public static string AudioDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "Ulvark");
        public static bool Audio;
        public static int MaxHeight;          // 0 = no cap
        public static string Cookies = "none"; // none, firefox, brave, chrome, edge
        public static bool SkipDone = true;   // skip what was already downloaded (archive files)

        public static void Load()
        {
            try
            {
                // pre-release builds were called VidGrab: move that folder over (engine included)
                string old = Path.Combine(Path.GetDirectoryName(Home), "VidGrab");
                if (!Directory.Exists(Home) && Directory.Exists(old)) { Directory.Move(old, Home); File.Delete(File_); }
                Directory.CreateDirectory(Home);
                if (!File.Exists(File_)) return;
                var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var line in File.ReadAllLines(File_, Encoding.UTF8))
                {
                    int i = line.IndexOf('=');
                    if (i > 0) d[line.Substring(0, i).Trim()] = line.Substring(i + 1).Trim();
                }
                if (d.TryGetValue("video_dir", out var v) && v.Length > 0) VideoDir = v;
                if (d.TryGetValue("audio_dir", out v) && v.Length > 0) AudioDir = v;
                if (d.TryGetValue("audio", out v)) Audio = v == "1";
                if (d.TryGetValue("max_height", out v)) int.TryParse(v, out MaxHeight);
                if (d.TryGetValue("cookies", out v) && v.Length > 0) Cookies = v;
                if (d.TryGetValue("skip_done", out v)) SkipDone = v != "0";
            }
            catch { }
        }

        public static void Save()
        {
            try
            {
                Directory.CreateDirectory(Home);
                File.WriteAllLines(File_, new[]
                {
                    "video_dir=" + VideoDir, "audio_dir=" + AudioDir, "audio=" + (Audio ? "1" : "0"),
                    "max_height=" + MaxHeight, "cookies=" + Cookies, "skip_done=" + (SkipDone ? "1" : "0")
                }, Encoding.UTF8);
            }
            catch { }
        }
    }
}

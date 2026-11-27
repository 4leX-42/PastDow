using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace PastDow
{
    sealed class HistoryEntry
    {
        public string id { get; set; }
        public string title { get; set; }
        public string file { get; set; }
        public long size { get; set; }
        public string date { get; set; }
        public bool audio { get; set; }
        public string url { get; set; }
        public string thumb { get; set; }
    }

    /// <summary>Local paths never leave this store. All reads and atomic writes share its lock.</summary>
    sealed class DownloadHistory
    {
        readonly object gate = new object();
        readonly string path;
        List<HistoryEntry> entries;
        static JavaScriptSerializer Json() => new JavaScriptSerializer { MaxJsonLength = 33554432 };

        public DownloadHistory(string home) { path = Path.Combine(home, "history.json"); }

        void Load()
        {
            if (entries != null) return;
            try
            {
                entries = Json().Deserialize<List<HistoryEntry>>(File.ReadAllText(path, Encoding.UTF8));
                if (entries == null || entries.Any(e => e == null || !Guid.TryParse(e.id, out _) ||
                    string.IsNullOrEmpty(e.file) || !Path.IsPathRooted(e.file) || e.file.IndexOf('"') >= 0 ||
                    !DateTimeOffset.TryParse(e.date, out _))) throw new FormatException();
                entries = entries.OrderByDescending(e => e.date, StringComparer.Ordinal).Take(200).ToList();
                foreach (var e in entries) e.thumb = BrowserMedia.CleanThumb(e.thumb);
            }
            catch { entries = new List<HistoryEntry>(); }
        }

        public bool Add(Job job)
        {
            if (job.State != JobState.Done || string.IsNullOrEmpty(job.File)) return false;
            lock (gate)
            {
                Load();
                string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    string file = Path.GetFullPath(job.File);
                    long size = 0;
                    try { size = new FileInfo(file).Length; } catch { }
                    var next = new List<HistoryEntry>(entries);
                    next.Insert(0, new HistoryEntry { id = Guid.NewGuid().ToString("N"), title = job.CustomName ?? job.Title,
                        file = file, size = size, date = DateTime.UtcNow.ToString("o"), audio = job.Audio,
                        url = job.Url, thumb = BrowserMedia.CleanThumb(job.Thumb) });
                    if (next.Count > 200) next.RemoveRange(200, next.Count - 200);
                    Directory.CreateDirectory(Path.GetDirectoryName(path));
                    File.WriteAllText(temp, Json().Serialize(next), new UTF8Encoding(false));
                    if (File.Exists(path)) File.Replace(temp, path, null);
                    else File.Move(temp, path);
                    entries = next;
                    return true;
                }
                catch { return false; }
                finally { try { if (File.Exists(temp)) File.Delete(temp); } catch { } }
            }
        }

        public object[] Items()
        {
            lock (gate)
            {
                Load();
                return entries.Select(e => (object)new { e.id, e.title, name = Path.GetFileName(e.file), e.size,
                    e.date, e.audio, e.thumb, exists = File.Exists(e.file) }).ToArray();
            }
        }

        // The caller supplies a launcher on the UI thread; tests supply a recorder instead of Explorer.
        public bool Reveal(string id, Action<string, string> launch)
        {
            string file;
            lock (gate) { Load(); file = entries.FirstOrDefault(e => e.id == id)?.file; }
            if (file == null || launch == null) return false;
            try
            {
                string folder = Path.GetDirectoryName(file);
                if (File.Exists(file)) launch(folder, file);
                else if (Directory.Exists(folder)) launch(folder, null);
                else return false;
                return true;
            }
            catch { return false; }
        }
    }
}

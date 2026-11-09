using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Ulvark
{
    /// <summary>
    /// The window: fixed 1120x630 (scaled by DPI), borderless, hosting ui\index.html in WebView2. The page draws
    /// everything and talks to this class through postMessage; downloads, settings, folders and the genie live here.
    /// </summary>
    sealed class AppForm : Form
    {
        const int Parallel = 2;
        public const string AppName = "Ulvark";
        public const string ReadyEvent = "Local\\Ulvark.Ready";
        Splash splash;
        const string RepoUrl = "https://github.com/4leX-42/Ulvark";

        readonly WebView2 web = new WebView2();
        readonly JavaScriptSerializer json = new JavaScriptSerializer();
        readonly List<Job> jobs = new List<Job>();
        readonly System.Windows.Forms.Timer push = new System.Windows.Forms.Timer { Interval = 120 };
        readonly string[] startUrls;
        bool pageReady, engineReady, animating, dirty;
        string engineStatus = "", lastClip = "", updateNote = "Checks once a day";
        double enginePct = -1;
        Bitmap lastSnap;
        CancellationTokenSource setupCts;

        public AppForm(string[] args)
        {
            startUrls = args;
            Text = AppName;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Color.Black;
            ClientSize = new Size(Theme.S(1120), Theme.S(630));
            MaximizeBox = false;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            web.Dock = DockStyle.Fill;
            web.DefaultBackgroundColor = Color.Black;
            Controls.Add(web);

            Runner.Finished += j => { dirty = true; try { BeginInvoke((Action)(() => { Pump(); PushState(); if (j.State == JobState.Failed && j.EngineSuspect) AutoFix(); })); } catch { } };
            push.Tick += (s, e) => { if (dirty || jobs.Any(j => j.Active)) { dirty = false; PushJobs(); } };
            push.Start();
        }

        // ---------- window ----------
        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.Style |= 0x00020000;              // WS_MINIMIZEBOX: minimise from the taskbar
                cp.ClassStyle |= 0x00020000;         // CS_DROPSHADOW
                return cp;
            }
        }

        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int v, int size);
        [DllImport("user32.dll")] static extern bool ReleaseCapture();
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, IntPtr l);

        void Cloak(bool on) { int v = on ? 1 : 0; DwmSetWindowAttribute(Handle, 13, ref v, 4); }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int on = 1; DwmSetWindowAttribute(Handle, 20, ref on, 4);
            int round = 2; DwmSetWindowAttribute(Handle, 33, ref round, 4);
            int border = ColorTranslator.ToWin32(Color.FromArgb(24, 24, 24)); DwmSetWindowAttribute(Handle, 34, ref border, 4);
            Genie.DisableSystemAnimation(this);
            Cloak(true);   // revealed by the genie once the page has painted
        }

        protected override void WndProc(ref Message m)
        {
            const int WM_SYSCOMMAND = 0x112, SC_MINIMIZE = 0xF020, SC_RESTORE = 0xF120, SC_MAXIMIZE = 0xF030;
            if (m.Msg == WM_SYSCOMMAND)
            {
                int cmd = m.WParam.ToInt32() & 0xFFF0;
                if (cmd == SC_MAXIMIZE) return;   // fixed frame
                if (!animating && cmd == SC_MINIMIZE && WindowState == FormWindowState.Normal) { Minimize(); return; }
                if (!animating && cmd == SC_RESTORE && WindowState == FormWindowState.Minimized) { Restore(ref m); return; }
            }
            base.WndProc(ref m);
        }

        protected override async void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Genie.LocateTarget(this);
            // WebView2 takes a moment (seconds on the very first run): show a small splash meanwhile,
            // unless the installer window is still covering the hand-over
            if (!startUrls.Contains("--from-setup")) { splash = new Splash(Bounds); splash.Show(); }
            try
            {
                Embedded.ExtractLoader();
                CoreWebView2Environment.SetLoaderDllFolderPath(Embedded.NativeDir);
                // ULVARK_DEVTOOLS=1 opens DevTools and the remote debugging port 9333 (UI tests)
                var opts = Environment.GetEnvironmentVariable("ULVARK_DEVTOOLS") == "1" ? new CoreWebView2EnvironmentOptions("--remote-debugging-port=9333") : null;
                var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(Settings.Home, "webview"), opts);
                await web.EnsureCoreWebView2Async(env);
            }
            catch (Exception ex)
            {
                splash?.Close(); Cloak(false);
                MessageBox.Show(this, "Ulvark needs the Microsoft Edge WebView2 Runtime (included in Windows 11)\n\n" + ex.Message, AppName);
                Close();
                return;
            }
            var s = web.CoreWebView2.Settings;
            s.AreDefaultContextMenusEnabled = false;
            s.AreDevToolsEnabled = Environment.GetEnvironmentVariable("ULVARK_DEVTOOLS") == "1";
            s.IsZoomControlEnabled = false;
            s.IsStatusBarEnabled = false;
            s.AreBrowserAcceleratorKeysEnabled = false;
            s.IsPinchZoomEnabled = false;
            web.CoreWebView2.WebMessageReceived += (o, a) => OnMessage(a.WebMessageAsJson);
            web.CoreWebView2.NewWindowRequested += (o, a) => { a.Handled = true; Shell(a.Uri); };
            web.NavigateToString(Embedded.Text("ui.html"));
        }

        // ---------- genie ----------
        async Task<Bitmap> Snap()
        {
            try
            {
                using (var ms = new MemoryStream())
                {
                    var t = web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, ms);
                    if (await Task.WhenAny(t, Task.Delay(600)) != t) return null;
                    ms.Position = 0;
                    using (var raw = new Bitmap(ms))
                    {
                        var b = new Bitmap(ClientSize.Width, ClientSize.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                        using (var g = Graphics.FromImage(b)) g.DrawImage(raw, 0, 0, b.Width, b.Height);
                        Genie.Round(b);
                        return b;
                    }
                }
            }
            catch { return null; }
        }

        async void Reveal()
        {
            animating = true;
            var snap = await Snap();
            splash?.Close(); splash = null;   // only now: the genie overlay follows immediately
            if (snap != null) { Genie.Play(snap, Bounds, false, () => Cloak(false)); Remember(snap); }
            else Cloak(false);
            animating = false;
            Activate();
            Post(new { t = "reveal" });
            try { using (var ev = System.Threading.EventWaitHandle.OpenExisting(ReadyEvent)) ev.Set(); } catch { }
        }

        async void Minimize()
        {
            animating = true;
            var snap = await Snap();
            Genie.LocateTarget(this);
            if (snap != null) { Genie.Play(snap, Bounds, true, () => Cloak(true)); Remember(snap); }
            WindowState = FormWindowState.Minimized;
            Cloak(false);
            animating = false;
        }

        void Restore(ref Message m)
        {
            animating = true;
            Cloak(true);
            base.WndProc(ref m);
            Update();
            if (lastSnap != null) Genie.Play(lastSnap, Bounds, false, () => Cloak(false));
            else Cloak(false);
            animating = false;
            Activate();
        }

        void Remember(Bitmap b) { var old = lastSnap; lastSnap = b; if (old != null && old != b) old.Dispose(); }

        // ---------- page <-> app ----------
        void Post(object o)
        {
            if (!pageReady) return;
            try { web.CoreWebView2.PostWebMessageAsJson(json.Serialize(o)); } catch { }
        }

        void OnMessage(string raw)
        {
            Dictionary<string, object> m;
            try { m = json.Deserialize<Dictionary<string, object>>(raw); } catch { return; }
            string S(string k) => m.TryGetValue(k, out var v) && v != null ? v.ToString() : "";
            int I(string k) { int.TryParse(S(k), out var n); return n; }
            Job J() => jobs.FirstOrDefault(x => x.Id == I("id"));

            switch (S("t"))
            {
                case "ready":
                    pageReady = true;
                    PushState(); PushJobs();
                    foreach (var u in startUrls.Where(a => !a.StartsWith("--"))) Add(u, false, Settings.MaxHeight, Settings.VideoFormat);
                    Reveal();
                    PrepareEngine();
                    break;
                case "add":
                    {
                        // what is picked on screen is for this session only; the saved defaults change in the menu
                        bool audio = S("mode") == "audio";
                        string fmt = S("fmt");
                        if (audio ? !Settings.AudioFormats.Contains(fmt) : !Settings.VideoFormats.Contains(fmt)) fmt = audio ? "best" : "mkv";
                        Add(S("url"), audio, audio ? 0 : I("q"), fmt);
                        break;
                    }
                case "def":
                    {
                        string k = S("key"), v = S("value");
                        if (k == "q" && new[] { 0, 2160, 1440, 1080, 720 }.Contains(I("value"))) Settings.MaxHeight = I("value");
                        if (k == "vfmt" && Settings.VideoFormats.Contains(v)) Settings.VideoFormat = v;
                        if (k == "afmt" && Settings.AudioFormats.Contains(v)) Settings.AudioFormat = v;
                        Settings.Save(); PushState();
                        break;
                    }
                case "defReset":
                    {
                        Settings.MaxHeight = 0; Settings.VideoFormat = "mkv"; Settings.AudioFormat = "best";
                        Settings.Save(); PushState();
                        break;
                    }
                case "cancel": { var j = J(); if (j != null) { Runner.Cancel(j); Pump(); } break; }
                case "retry":
                    {
                        var j = J();
                        if (j != null && j.Finished)
                        {
                            jobs[jobs.IndexOf(j)] = new Job { Url = j.Url, Audio = j.Audio, MaxHeight = j.MaxHeight, Format = j.Format, Force = true, Title = j.Title };
                            Pump();
                        }
                        break;
                    }
                case "remove": { var j = J(); if (j != null && j.Finished) { jobs.Remove(j); dirty = true; } break; }
                case "clearDone": jobs.RemoveAll(x => x.Finished); dirty = true; break;
                case "open": { var j = J(); if (j?.File != null && File.Exists(j.File)) Shell(j.File); break; }
                case "reveal": { var j = J(); if (j != null) OpenFolder(j.File != null ? Path.GetDirectoryName(j.File) : j.Folder, j.File); break; }
                case "copy": try { Clipboard.SetText(S("text")); lastClip = S("text"); } catch { } break;
                case "openFolder": OpenFolder(Settings.Audio ? Settings.AudioDir : Settings.VideoDir, null); break;
                case "pickFolder":
                    {
                        bool a = S("audio") == "True" || S("audio") == "true";
                        string cur = a ? Settings.AudioDir : Settings.VideoDir;
                        try { Directory.CreateDirectory(cur); } catch { }
                        string p = FolderPicker.Pick(Handle, a ? "Folder for audio" : "Folder for videos", cur);
                        if (p != null) { if (a) Settings.AudioDir = p; else Settings.VideoDir = p; Settings.Save(); PushState(); }
                        break;
                    }
                case "skip": Settings.SkipDone = !Settings.SkipDone; Settings.Save(); PushState(); break;
                case "folders": Settings.ChannelFolders = !Settings.ChannelFolders; Settings.Save(); PushState(); break;
                case "update": ForceUpdate(); break;
                case "source": Shell(RepoUrl); break;
                case "min": Minimize(); break;
                case "close": Close(); break;
                case "drag": ReleaseCapture(); SendMessage(Handle, 0xA1, (IntPtr)2, IntPtr.Zero); break;
            }
        }

        void PushState()
        {
            Post(new
            {
                t = "state",
                q = Settings.MaxHeight, vfmt = Settings.VideoFormat, afmt = Settings.AudioFormat,
                videoDir = Settings.VideoDir, audioDir = Settings.AudioDir, videoFree = Free(Settings.VideoDir), audioFree = Free(Settings.AudioDir), skip = Settings.SkipDone, channelFolders = Settings.ChannelFolders, update = updateNote,
                engine = engineStatus, enginePct = enginePct, ready = engineReady, version = Installer.Version
            });
        }

        void PushJobs()
        {
            Post(new
            {
                t = "jobs",
                jobs = jobs.Select(j => new
                {
                    id = j.Id, audio = j.Audio, url = j.Url, title = j.Title ?? "", state = j.State.ToString().ToLowerInvariant(), started = j.Started,
                    pct = Math.Round(j.Percent, 1), speed = j.Speed ?? "", eta = j.Eta ?? "", error = j.Error ?? "",
                    kind = j.Audio ? (j.Format == null || j.Format == "best" ? "BEST AUDIO" : j.Format.ToUpperInvariant()) : (j.MaxHeight > 0 ? j.MaxHeight + "P" : "BEST") + " · " + (j.Format ?? "mkv").ToUpperInvariant(),
                    size = SizeOf(j.File), file = j.File != null ? Path.GetFileName(j.File) : ""
                }).ToList()
            });
        }

        static string Free(string dir)
        {
            try
            {
                var gb = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(dir))).AvailableFreeSpace / 1073741824.0;
                return gb >= 1024 ? (gb / 1024).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " TB" : ((int)gb) + " GB";
            }
            catch { return ""; }
        }

        static string SizeOf(string f)
        {
            try { return f != null && File.Exists(f) ? (new FileInfo(f).Length / 1048576.0).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + " MB" : ""; }
            catch { return ""; }
        }

        // ---------- engine and queue ----------
        async void PrepareEngine()
        {
            if (Engine.Ready) { engineReady = true; PushState(); Pump(); AutoUpdate(); return; }
            setupCts = new CancellationTokenSource();
            engineStatus = "Setting up yt-dlp and ffmpeg · first run only"; enginePct = 0; PushState();
            try
            {
                await Engine.Install((t, p) => BeginInvoke((Action)(() => { if (!engineReady) { engineStatus = t; enginePct = p; PushState(); } })), setupCts.Token);
                engineReady = true; engineStatus = ""; enginePct = -1; PushState();
                Pump(); AutoUpdate();
            }
            catch (Exception ex)
            {
                if (IsDisposed) return;
                engineStatus = "Error setting up: " + ex.Message; enginePct = -1; PushState();
            }
        }

        async void AutoUpdate()
        {
            var up = await Engine.AutoUpdate();
            if (up != null && !IsDisposed) { updateNote = up; PushState(); }
        }

        // ---------- self-repair: a failure that smells like an outdated yt-dlp triggers an update and one retry ----------
        bool fixing;
        DateTime lastFix = DateTime.MinValue;

        async void AutoFix()
        {
            if (fixing || !engineReady) return;
            var broken = jobs.Where(j => j.State == JobState.Failed && j.EngineSuspect && !j.FixTried).ToList();
            if (broken.Count == 0) return;
            if (DateTime.UtcNow - lastFix < TimeSpan.FromMinutes(15))
            {
                foreach (var j in broken) { j.FixTried = true; j.Error += " · still failing with the latest yt-dlp"; }
                dirty = true; return;
            }
            fixing = true; lastFix = DateTime.UtcNow;
            engineStatus = "Updating yt-dlp to fix a failed download…"; PushState();
            var r = await Engine.AutoUpdate(true);
            fixing = false;
            if (r == "yt-dlp updated")
            {
                updateNote = "Updated after a failed download";
                engineStatus = "yt-dlp updated · retrying"; PushState();
                foreach (var j in jobs.Where(x => x.State == JobState.Failed && x.EngineSuspect && !x.FixTried).ToList())
                    jobs[jobs.IndexOf(j)] = new Job { Url = j.Url, Audio = j.Audio, MaxHeight = j.MaxHeight, Format = j.Format, Title = j.Title, FixTried = true };
                Pump();
            }
            else
            {
                foreach (var j in broken) { j.FixTried = true; j.Error += " · still failing with the latest yt-dlp"; }
                engineStatus = ""; dirty = true; PushState();
            }
            await Task.Delay(4000);
            if (!fixing && engineStatus.StartsWith("yt-dlp updated")) { engineStatus = ""; PushState(); }
        }

        async void ForceUpdate()
        {
            if (!engineReady) return;
            updateNote = "Checking…"; PushState();
            var r = await Engine.AutoUpdate(true);
            updateNote = r ?? "Could not check"; PushState();
        }

        static bool IsUrl(string s) => Uri.TryCreate(s, UriKind.Absolute, out var u) && (u.Scheme == "http" || u.Scheme == "https");

        void Add(string text, bool audio, int q, string fmt)
        {
            foreach (var u in (text ?? "").Split(new[] { '\r', '\n', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
                if (IsUrl(u.Trim())) { jobs.Add(new Job { Url = u.Trim(), Audio = audio, MaxHeight = q, Format = fmt }); lastClip = u.Trim(); }
            Pump();
        }

        void Pump()
        {
            if (IsDisposed) return;
            if (engineReady)
            {
                int running = jobs.Count(j => j.Active);
                foreach (var j in jobs.Where(j => j.State == JobState.Queued).ToList())
                {
                    if (running >= Parallel) break;
                    Runner.Start(j);
                    running++;
                }
            }
            dirty = true;
            var act = jobs.Where(j => j.Active).ToList();
            Text = act.Count == 0 ? AppName : $"{AppName} · {act.Average(j => j.State == JobState.Processing ? 100 : j.Percent):0}%";
        }

        protected override void OnActivated(EventArgs e)
        {
            base.OnActivated(e);
            try
            {
                if (!Clipboard.ContainsText()) return;
                string c = Clipboard.GetText().Trim();
                if (c == lastClip) return;
                lastClip = c;
                if (IsUrl(c) && !jobs.Any(j => j.Url == c)) Post(new { t = "clip", url = c });
            }
            catch { }
        }

        static void Shell(string target)
        {
            try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); } catch { }
        }

        static void OpenFolder(string dir, string file)
        {
            try
            {
                Directory.CreateDirectory(dir);
                if (file != null && File.Exists(file)) Process.Start("explorer.exe", "/select,\"" + file + "\"");
                else Process.Start("explorer.exe", "\"" + dir + "\"");
            }
            catch { }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // anything half-done resumes next time (--continue)
            setupCts?.Cancel();
            foreach (var j in jobs.Where(j => j.Active).ToList()) Runner.Cancel(j);
            Settings.Save();
            base.OnFormClosing(e);
        }
    }
}

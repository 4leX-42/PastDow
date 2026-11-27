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

namespace PastDow
{
    /// <summary>
    /// The window: fixed 1120x630 (scaled by DPI), borderless, hosting ui\index.html in WebView2. The page draws
    /// everything and talks to this class through postMessage; downloads, settings, folders and the genie live here.
    /// </summary>
    sealed class AppForm : Form
    {
        const int Parallel = 2;
        public const string AppName = "PastDow";
        public const string ReadyEvent = "Local\\PastDow.Ready";
        Splash splash;
        const string RepoUrl = "https://github.com/4leX-42/PastDow";

        readonly WebView2 web = new WebView2();
        readonly JavaScriptSerializer json = new JavaScriptSerializer();
        readonly List<Job> jobs = new List<Job>();
        readonly List<KeyValuePair<int, BrowserMedia>> found = new List<KeyValuePair<int, BrowserMedia>>();
        readonly List<string> pendingArgs = new List<string>();
        BrowserLink browser;
        readonly DownloadHistory history = new DownloadHistory(Settings.Home);
        int nextFound;
        string browserStatus = "";
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

            Runner.Finished += j => { dirty = true; try { BeginInvoke((Action)(() => { if (!ToCapture(j)) { Count(j); Pump(); PushState(); if (j.State == JobState.Failed && j.EngineSuspect) AutoFix(); BatchDone(); } })); } catch { } };
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
            try
            {
                browser = new BrowserLink((media, queue) =>
                {
                    var completion = new TaskCompletionSource<bool>();
                    try { BeginInvoke((Action)(() => { try { AddFound(media); if (queue) QueueFound(media, false, Settings.MaxHeight, Settings.VideoFormat); completion.SetResult(true); } catch (Exception ex) { completion.SetException(ex); } })); }
                    catch (Exception ex) { completion.SetException(ex); }
                    return completion.Task;
                });
                browser.History = history;
                browser.Reveal = id =>
                {
                    var completion = new TaskCompletionSource<bool>();
                    try { BeginInvoke((Action)(() => completion.SetResult(history.Reveal(id, (dir, file) =>
                        Process.Start("explorer.exe", file != null ? "/select,\"" + file + "\"" : "\"" + dir + "\""))))); }
                    catch (Exception ex) { completion.SetException(ex); }
                    return completion.Task;
                };
                browser.Start();
            }
            catch (Exception ex) { browserStatus = "Browser connection unavailable: " + ex.Message; }
            Genie.LocateTarget(this);
            // WebView2 takes a moment (seconds on the very first run): show a small splash meanwhile,
            // unless the installer window is still covering the hand-over
            if (!startUrls.Contains("--from-setup")) { splash = new Splash(Bounds); splash.Show(); }
            try
            {
                Embedded.ExtractLoader();
                CoreWebView2Environment.SetLoaderDllFolderPath(Embedded.NativeDir);
                // PASTDOW_DEVTOOLS=1 opens DevTools and the remote debugging port 9333 (UI tests)
                var opts = Environment.GetEnvironmentVariable("PASTDOW_DEVTOOLS") == "1" ? new CoreWebView2EnvironmentOptions("--remote-debugging-port=9333") : null;
                var env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(Settings.Home, "webview"), opts);
                await web.EnsureCoreWebView2Async(env);
            }
            catch (Exception ex)
            {
                splash?.Close(); Cloak(false);
                MessageBox.Show(this, "PastDow needs the Microsoft Edge WebView2 Runtime (included in Windows 11)\n\n" + ex.Message, AppName);
                Close();
                return;
            }
            var s = web.CoreWebView2.Settings;
            s.AreDefaultContextMenusEnabled = false;
            s.AreDevToolsEnabled = Environment.GetEnvironmentVariable("PASTDOW_DEVTOOLS") == "1";
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
                    PushState(); PushJobs(); PushFound();
                    AcceptArguments(startUrls.Concat(pendingArgs).ToArray()); pendingArgs.Clear();
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
                case "foundClear": found.Clear(); PushFound(); break;
                case "foundQueue":
                    {
                        var media = found.FirstOrDefault(x => x.Key == I("id")).Value;
                        if (media != null) QueueFound(media, S("mode") == "audio", I("q"), S("fmt"), S("name"));
                        break;
                    }
                case "browserKey": try { Clipboard.SetText(Settings.BrowserKey); lastClip = Settings.BrowserKey; } catch { } break;
                case "installExt":
                    {
                        // PastDow clicks through the browser's own steps (ExtensionAutoInstall); if that fails the
                        // folder path goes to the clipboard and the page shows the two steps left
                        string browser = S("browser");
                        Task.Run(() =>
                        {
                            string result = null;
                            try { result = ExtensionSetup.Install(browser, text => { try { BeginInvoke((Action)(() => Post(new { t = "extStep", text }))); } catch { } }); } catch { }
                            try
                            {
                                BeginInvoke((Action)(() =>
                                {
                                    if (result == "guided") { try { Clipboard.SetText(ExtensionSetup.Folder); lastClip = ExtensionSetup.Folder; } catch { } }
                                    Post(new { t = "extOpened", ok = result != null, auto = result == "done", folder = ExtensionSetup.Folder });
                                    Activate();
                                }));
                            }
                            catch { }
                        });
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
                            var fresh = j.Captured ? found.FirstOrDefault(x => x.Value.page == j.Page && new Uri(x.Value.url).GetLeftPart(UriPartial.Path) == new Uri(j.Url).GetLeftPart(UriPartial.Path)).Value : null;
                            // External browser sessions cannot be replayed inside the capture window.
                            var retry = fresh != null ? fresh.Job(j.Audio, j.MaxHeight, j.Format) : j.Captured
                                ? new Job { Url = j.Url, Page = j.Page, Referer = j.Referer, UserAgent = j.UserAgent, Cookie = j.Cookie, Title = j.Title,
                                    Captured = true, Subtitles = j.Subtitles, MediaKind = j.MediaKind, Audio = j.Audio, MaxHeight = j.MaxHeight, Format = j.Format }
                                : new Job { Url = j.Url, Audio = j.Audio, MaxHeight = j.MaxHeight, Format = j.Format, Title = j.Title, ForceCapture = j.ForceCapture };
                            retry.Thumb = fresh?.thumb ?? j.Thumb;
                            retry.Page = j.Page;
                            retry.Force = true;
                            retry.CustomName = j.CustomName; if (j.CustomName != null) retry.Title = j.CustomName;
                            jobs[jobs.IndexOf(j)] = retry;
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
                engine = engineStatus, enginePct = enginePct, ready = engineReady, version = Installer.Version,
                browserKey = Settings.BrowserKey, browserStatus, browsers = Browsers()
            });
        }

        List<object> browsers;
        /// <summary>Chromium browsers found on this PC (looked up once), for the extension install buttons.</summary>
        List<object> Browsers()
        {
            if (browsers == null)
                try { browsers = ExtensionSetup.Found().Select(x => (object)new { id = x.Id, name = x.Name }).ToList(); }
                catch { browsers = new List<object>(); }
            return browsers;
        }

        void PushFound()
        {
            Post(new { t = "found", items = found.Select(x => new { id = x.Key, title = x.Value.title, kind = x.Value.kind, size = x.Value.size,
                host = new Uri(x.Value.url).Host, subtitles = x.Value.subtitles.Count, thumb = x.Value.thumb, duration = x.Value.duration }).ToList() });
        }

        void AddFound(BrowserMedia media)
        {
            int index = found.FindIndex(x => x.Value.Identity == media.Identity);
            int id = index >= 0 ? found[index].Key : ++nextFound;
            if (index >= 0) {
                var previous = found[index].Value;
                media.size = Math.Max(media.size, previous.size);
                media.thumb = media.thumb ?? previous.thumb;
                if (media.duration == 0) media.duration = previous.duration;
                found.RemoveAt(index);
            }
            found.Insert(0, new KeyValuePair<int, BrowserMedia>(id, media));
            if (found.Count > 200) found.RemoveAt(found.Count - 1);
            PushFound();
        }

        void QueueFound(BrowserMedia media, bool audio, int q, string fmt, string name = null)
        {
            if (audio ? !Settings.AudioFormats.Contains(fmt) : !Settings.VideoFormats.Contains(fmt)) fmt = audio ? Settings.AudioFormat : Settings.VideoFormat;
            if (!new[] { 0, 720, 1080, 1440, 2160 }.Contains(q)) q = 0;
            var job = media.Job(audio, audio ? 0 : q, fmt);
            string chosen = BrowserMedia.CleanName(name);
            if (chosen != null) { job.CustomName = chosen; job.Title = chosen; }
            jobs.Add(job);
            Pump();
        }

        /// <summary>Called by the per-user argument pipe, marshaled to the window thread.</summary>
        public void ReceiveArguments(string[] args)
        {
            try { BeginInvoke((Action)(() => { if (!pageReady) pendingArgs.AddRange(args ?? new string[0]); else AcceptArguments(args); })); } catch { }
        }

        void AcceptArguments(string[] args)
        {
            foreach (var arg in args ?? new string[0])
            {
                if (arg.StartsWith("pastdow:", StringComparison.OrdinalIgnoreCase))
                {
                    var media = BrowserMedia.Protocol(arg);
                    if (media != null) { AddFound(media); QueueFound(media, false, Settings.MaxHeight, Settings.VideoFormat); }
                }
                else if (!arg.StartsWith("--")) Add(arg, false, Settings.MaxHeight, Settings.VideoFormat);
            }
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
                    size = SizeOf(j.File), file = j.File != null ? Path.GetFileName(j.File) : "", subtitleNote = j.SubtitleNote
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
                    jobs[jobs.IndexOf(j)] = new Job { Url = j.Url, Audio = j.Audio, MaxHeight = j.MaxHeight, Format = j.Format, Title = j.Title, FixTried = true,
                        Captured = j.Captured, Page = j.Page, Referer = j.Referer, Cookie = j.Cookie, UserAgent = j.UserAgent, MediaKind = j.MediaKind, Subtitles = j.Subtitles };
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

        // ---------- "done" notice: one small tick when a batch of downloads ends ----------
        int doneVideos, doneAudio;
        string lastDoneFile;

        void Count(Job j)
        {
            if (j.State != JobState.Done) return;
            history.Add(j);
            if (j.Audio) doneAudio++; else doneVideos++;
            lastDoneFile = j.File;
        }

        /// <summary>
        /// When nothing is left running or queued: a tick in the window, and when PastDow is not in front a Windows
        /// notification (click opens the folder) plus a taskbar flash.
        /// </summary>
        void BatchDone()
        {
            if (doneVideos + doneAudio == 0 || jobs.Any(x => x.Active || x.State == JobState.Queued) || PastDow.Capture.Busy) return;
            string text = Plural(doneVideos, "video") + (doneVideos > 0 && doneAudio > 0 ? " and " : "") + Plural(doneAudio, "track") + " downloaded";
            string file = lastDoneFile;
            doneVideos = doneAudio = 0;
            Post(new { t = "done", text = text.ToUpperInvariant() });
            if (ContainsFocus && WindowState != FormWindowState.Minimized) return;
            Flash();
            try
            {
                // Windows already shows "PastDow" as the header: the title is the news, the body the last file
                string body = file != null ? Path.GetFileName(file) : Settings.VideoDir;
                var tray = new NotifyIcon { Icon = Icon, Text = AppName, Visible = true, BalloonTipTitle = "✓ " + char.ToUpper(text[0]) + text.Substring(1), BalloonTipText = body, BalloonTipIcon = ToolTipIcon.None };
                tray.BalloonTipClicked += (o, e) => OpenFolder(file != null ? Path.GetDirectoryName(file) : Settings.VideoDir, file);
                tray.ShowBalloonTip(6000);
                var gone = new System.Windows.Forms.Timer { Interval = 12000 };
                gone.Tick += (o, e) => { gone.Stop(); gone.Dispose(); tray.Visible = false; tray.Dispose(); };
                gone.Start();
            }
            catch { }
        }

        static string Plural(int n, string word) => n == 0 ? "" : n == 1 ? "1 " + word : n + " " + word + "s";

        [StructLayout(LayoutKind.Sequential)] struct FLASHWINFO { public uint cbSize; public IntPtr hwnd; public uint dwFlags, uCount, dwTimeout; }
        [DllImport("user32.dll")] static extern bool FlashWindowEx(ref FLASHWINFO f);

        void Flash()
        {
            // FLASHW_TRAY | FLASHW_TIMERNOFG: the taskbar button glows until the window comes to the front
            var f = new FLASHWINFO { cbSize = (uint)Marshal.SizeOf(typeof(FLASHWINFO)), hwnd = Handle, dwFlags = 0x2 | 0xC, uCount = 3 };
            try { FlashWindowEx(ref f); } catch { }
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
                    if (j.NeedsCapture)
                    {
                        if (PastDow.Capture.Busy) continue;   // one capture window at a time; the rest wait their turn
                        j.State = JobState.Running;
                        j.Title = j.Title ?? "Find the video in the browser window";
                        PastDow.Capture.Open(j, this, Captured);
                    }
                    else Runner.Start(j);
                    running++;
                }
            }
            dirty = true;
            var act = jobs.Where(j => j.Active).ToList();
            Text = act.Count == 0 ? AppName : $"{AppName} · {act.Average(j => j.State == JobState.Processing ? 100 : j.Percent):0}%";
        }

        // ---------- capture window: pages yt-dlp cannot read ----------
        /// <summary>yt-dlp said the page is not supported: look for the video in the capture window instead.</summary>
        bool ToCapture(Job j)
        {
            if (j.State != JobState.Failed || !j.Unsupported || j.Captured || j.ForceCapture || !jobs.Contains(j)) return false;
            jobs[jobs.IndexOf(j)] = new Job { Url = j.Url, Audio = j.Audio, MaxHeight = j.MaxHeight, Format = j.Format, Force = j.Force, ForceCapture = true };
            Pump(); PushState();
            return true;
        }

        void Captured(Job j)
        {
            if (IsDisposed) return;
            if (j.CancelRequested) { j.State = JobState.Canceled; j.Started = false; }
            else { j.Captured = true; j.State = JobState.Queued; }
            dirty = true;
            Pump();
            Activate();
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
            browser?.Dispose();
            foreach (var j in jobs.Where(j => j.Active).ToList()) Runner.Cancel(j);
            Settings.Save();
            base.OnFormClosing(e);
        }
    }
}

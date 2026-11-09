using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace Ulvark
{
    sealed class MainForm : Form
    {
        const int Parallel = 2;
        public const string AppName = "Ulvark";
        const string RepoUrl = "https://github.com/4leX-42/Ulvark";

        readonly GlyphButton btnMin = new GlyphButton(""), btnClose = new GlyphButton("");
        readonly GlyphButton btnMenu = new GlyphButton("") { Label = "MENU" };
        readonly UrlBox url = new UrlBox();
        readonly SweepButton btnGo = new SweepButton("DOWNLOAD");
        readonly Choice mode = new Choice("VIDEO", "AUDIO");
        readonly Choice quality = new Choice("BEST", "2160P", "1440P", "1080P", "720P");
        static readonly int[] Heights = { 0, 2160, 1440, 1080, 720 };
        readonly QueueView queue = new QueueView();
        readonly TextLink linkClear = new TextLink("CLEAR");
        readonly TextLink linkFolder = new TextLink("") { Mono = true };
        readonly TextLink linkOpen = new TextLink("OPEN FOLDER");
        readonly SideMenu side = new SideMenu();
        readonly System.Windows.Forms.Timer frame = new System.Windows.Forms.Timer { Interval = 15 };
        readonly Stopwatch clock = Stopwatch.StartNew();
        double lastT;
        readonly ContextMenuStrip menu = new ContextMenuStrip { Renderer = new DarkRenderer(), ShowImageMargin = false };
        readonly string[] startUrls;

        string status = "";
        double setupPct = -1;
        bool engineReady;
        string lastClip = "";
        string updateNote = "Checks once a day";
        CancellationTokenSource setupCts;

        int M => Theme.S(40);

        public MainForm(string[] args)
        {
            startUrls = args;
            Text = AppName;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Theme.Bg;
            ClientSize = new Size(Theme.S(760), Theme.S(640));
            MinimumSize = new Size(Theme.S(600), Theme.S(480));
            DoubleBuffered = true;
            KeyPreview = true;
            AllowDrop = true;
            Opacity = 0;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            url.Box.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Enqueue(); } };
            btnGo.Click += (s, e) => Enqueue();
            btnMin.Click += (s, e) => MinimizeGenie();
            btnClose.Click += (s, e) => Close();
            btnMenu.Click += (s, e) => ToggleMenu();

            mode.Selected = Settings.Audio ? 1 : 0;
            quality.Selected = Math.Max(0, Array.IndexOf(Heights, Settings.MaxHeight));
            quality.Enabled = !Settings.Audio;
            mode.Changed += (s, e) => { Settings.Audio = mode.Selected == 1; quality.Enabled = !Settings.Audio; UpdateFooter(); Settings.Save(); };
            quality.Changed += (s, e) => { Settings.MaxHeight = Heights[quality.Selected]; Settings.Save(); };

            queue.Empty = new[] { "Nothing here yet", "Paste a link above or drop it on the window · several at once works too" };
            queue.ItemDoubleClick += OpenJob;
            queue.ItemClick += (j, e) => { if (e.Button == MouseButtons.Right) ShowMenu(j, e.Location); };
            queue.MouseDown += (s, e) => CloseMenu();
            MouseDown += (s, e) => CloseMenu();

            linkClear.Click += (s, e) => { queue.Jobs.RemoveAll(j => j.Finished); queue.Clamp(); Redraw(); };
            linkFolder.Click += (s, e) => ChooseFolder(Settings.Audio);
            linkOpen.Click += (s, e) => OpenFolder(Settings.Audio ? Settings.AudioDir : Settings.VideoDir, null);

            side.Items.Add(new SideMenu.Item { Label = "VIDEO FOLDER", Value = () => Settings.VideoDir, Click = () => ChooseFolder(false) });
            side.Items.Add(new SideMenu.Item { Label = "AUDIO FOLDER", Value = () => Settings.AudioDir, Click = () => ChooseFolder(true) });
            side.Items.Add(new SideMenu.Item
            {
                Label = "SKIP DOWNLOADED",
                Value = () => Settings.SkipDone ? "On" : "Off",
                Click = () => { Settings.SkipDone = !Settings.SkipDone; Settings.Save(); }
            });
            side.Items.Add(new SideMenu.Item { Label = "UPDATE YT-DLP", Value = () => updateNote, Click = ForceUpdate });
            side.Items.Add(new SideMenu.Item { Label = "SOURCE CODE", Value = () => RepoUrl.Replace("https://", ""), Click = () => Shell(RepoUrl) });
            side.Moved += PlaceMenu;

            Controls.AddRange(new Control[] { side, btnMenu, btnMin, btnClose, url, btnGo, mode, quality, queue, linkClear, linkFolder, linkOpen });
            UpdateFooter();

            Runner.Finished += j => { try { BeginInvoke((Action)Pump); } catch { } };
            frame.Tick += (s, e) => Animate();
            frame.Start();
        }

        static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);

        // ---------- animation clock: one timer for the whole window ----------
        void Animate()
        {
            double t = clock.Elapsed.TotalSeconds, dt = Math.Min(0.05, t - lastT);
            lastT = t;
            foreach (Control c in Controls)
                if (c is IAnim a && c.Visible && a.Step(dt)) c.Invalidate();
            if (queue.Jobs.Any(j => j.Active)) { UpdateTitle(); Invalidate(CountRect); }
        }

        // ---------- borderless window ----------
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
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, string l);

        protected override void WndProc(ref Message m)
        {
            const int WM_NCHITTEST = 0x84, WM_SYSCOMMAND = 0x112, SC_MINIMIZE = 0xF020, SC_RESTORE = 0xF120;
            if (m.Msg == WM_SYSCOMMAND && !animating)
            {
                int cmd = m.WParam.ToInt32() & 0xFFF0;
                if (cmd == SC_MINIMIZE && WindowState == FormWindowState.Normal) { MinimizeGenie(); return; }
                if (cmd == SC_RESTORE && WindowState == FormWindowState.Minimized) { RestoreGenie(ref m); return; }
            }
            base.WndProc(ref m);
            if (m.Msg == WM_NCHITTEST && WindowState == FormWindowState.Normal)
            {
                int lp = m.LParam.ToInt32();
                var p = PointToClient(new Point((short)(lp & 0xFFFF), (short)(lp >> 16)));
                int g = Theme.S(6);
                bool l = p.X < g, r = p.X >= Width - g, t = p.Y < g, b = p.Y >= Height - g;
                if (t && l) m.Result = (IntPtr)13; else if (t && r) m.Result = (IntPtr)14;
                else if (b && l) m.Result = (IntPtr)16; else if (b && r) m.Result = (IntPtr)17;
                else if (l) m.Result = (IntPtr)10; else if (r) m.Result = (IntPtr)11;
                else if (t) m.Result = (IntPtr)12; else if (b) m.Result = (IntPtr)15;
                else if (p.Y < Theme.S(72)) m.Result = (IntPtr)2; // HTCAPTION: drag from the top bar
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int on = 1; DwmSetWindowAttribute(Handle, 20, ref on, 4);           // dark mode
            int round = 2; DwmSetWindowAttribute(Handle, 33, ref round, 4);     // Windows 11 rounded corners
            int border = ColorTranslator.ToWin32(Color.FromArgb(28, 28, 28)); DwmSetWindowAttribute(Handle, 34, ref border, 4);
            Genie.DisableSystemAnimation(this);
        }

        // ---------- genie: into the taskbar button and back out ----------
        bool animating;

        void MinimizeGenie()
        {
            animating = true;
            try
            {
                Genie.LocateTarget(this).Wait(250);
                using (var snap = Genie.Snapshot(this))
                {
                    Opacity = 0;
                    Genie.Play(snap, Bounds, true);
                }
                WindowState = FormWindowState.Minimized;
                Opacity = 1;
            }
            finally { animating = false; }
        }

        void RestoreGenie(ref Message m)
        {
            animating = true;
            try
            {
                Opacity = 0;
                base.WndProc(ref m);   // the real restore, invisible
                Update();
                Genie.LocateTarget(this).Wait(250);
                using (var snap = Genie.Snapshot(this)) Genie.Play(snap, Bounds, false);
                Opacity = 1;
                Activate();
            }
            finally { animating = false; }
        }

        void OpenGenie()
        {
            animating = true;
            try
            {
                Update();
                Genie.LocateTarget(this).Wait(400);
                using (var snap = Genie.Snapshot(this)) Genie.Play(snap, Bounds, false);
            }
            catch { }
            finally { Opacity = 1; animating = false; Activate(); }
        }

        // ---------- layout ----------
        int ListLabelY => quality.Bottom + Theme.S(44);
        Rectangle CountRect => new Rectangle(M, ListLabelY - Theme.S(2), Theme.S(320), Theme.S(22));

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            int W = ClientSize.Width, H = ClientSize.Height, m = M;
            int bw = Theme.S(40), bh = Theme.S(40), top = Theme.S(16);
            btnClose.SetBounds(W - bw - Theme.S(10), top, bw, bh);
            btnMin.SetBounds(btnClose.Left - bw, top, bw, bh);
            int mw = btnMenu.Measure();
            btnMenu.SetBounds(btnMin.Left - mw - Theme.S(12), top, mw, bh);

            int goW = btnGo.Measure(), goH = Theme.S(46);
            url.SetBounds(m, Theme.S(118), W - m * 2 - goW - Theme.S(28), Theme.S(60));
            btnGo.SetBounds(W - m - goW, url.Bottom - goH, goW, goH);

            int oy = url.Bottom + Theme.S(22), oh = Theme.S(30);
            mode.SetBounds(m, oy, mode.Measure(), oh);
            quality.SetBounds(mode.Right + Theme.S(44), oy, quality.Measure(), oh);

            int cw = linkClear.Measure();
            linkClear.SetBounds(W - m - cw, ListLabelY - Theme.S(2), cw, Theme.S(22));

            int qTop = ListLabelY + Theme.S(30), foot = Theme.S(66);
            queue.SetBounds(m - Theme.S(12), qTop, W - m * 2 + Theme.S(24), H - qTop - foot);

            int fy = H - Theme.S(44), lh = Theme.S(22);
            int ow = linkOpen.Measure();
            linkOpen.SetBounds(W - m - ow, fy, ow, lh);
            linkFolder.SetBounds(m, fy, Math.Max(0, Math.Min(linkFolder.Measure(), linkOpen.Left - Theme.S(28) - m)), lh);

            PlaceMenu();
            RebuildBackdrop();
            Invalidate(true);
        }

        void PlaceMenu()
        {
            int w = Math.Min(Theme.S(360), ClientSize.Width);
            int x = ClientSize.Width - (int)(w * Theme.EaseOut(side.OpenT));
            side.SetBounds(x, 0, w, ClientSize.Height);
            Backdrop.Cover = side.Visible ? side.Bounds : Rectangle.Empty; Backdrop.CoverColor = side.BackColor;
            btnMenu.Invalidate(); btnMin.Invalidate(); btnClose.Invalidate();
            btnMenu.SetGlyph(side.IsOpen ? "" : "");
            btnMenu.BringToFront(); btnMin.BringToFront(); btnClose.BringToFront();
        }

        Size backdropSize;
        void RebuildBackdrop()
        {
            if (ClientSize == backdropSize || ClientSize.Width < 10) return;
            backdropSize = ClientSize;
            var old = Backdrop.Image;
            Backdrop.Image = Theme.Backdrop(ClientSize);
            old?.Dispose();
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            if (Backdrop.Image != null) e.Graphics.DrawImage(Backdrop.Image, e.ClipRectangle, e.ClipRectangle, GraphicsUnit.Pixel);
            else e.Graphics.Clear(Theme.Bg);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = Theme.Crisp(e.Graphics);
            int m = M;
            var brand = Theme.Bold(13);
            Theme.Tracked(g, "ULVARK", brand, m, Theme.S(26), Theme.Text, 0.42f);

            Theme.Tracked(g, "PASTE A LINK  ·  YOUTUBE AND 1,000+ OTHER SITES", Theme.Display(8), m, Theme.S(100), Theme.Faint, 0.16f);

            using (var b = new SolidBrush(Theme.Line))
                g.FillRectangle(b, mode.Right + Theme.S(22), mode.Top + Theme.S(6), Math.Max(1, Theme.S(1)), mode.Height - Theme.S(16));

            if (status.Length > 0)
            {
                var sr = new Rectangle(quality.Right + Theme.S(24), mode.Top, ClientSize.Width - quality.Right - Theme.S(24) - m, mode.Height - Theme.S(6));
                TextRenderer.DrawText(g, status, Theme.Sans(9), sr, status.StartsWith("Error") ? Theme.Red : Theme.Sub,
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            }
            if (setupPct >= 0)
                using (var b = new SolidBrush(Theme.Sub))
                    g.FillRectangle(b, url.Left, url.Bottom, (int)(url.Width * setupPct), Math.Max(1, Theme.S(1)));

            var lf = Theme.Display(8.5f);
            Theme.Tracked(g, "DOWNLOADS", lf, m, ListLabelY, Theme.Faint, 0.16f);
            int act = queue.Jobs.Count(j => j.Active), pend = queue.Jobs.Count(j => j.State == JobState.Queued);
            if (act + pend > 0)
            {
                string cnt = act + " ACTIVE" + (pend > 0 ? "  ·  " + pend + " WAITING" : "");
                Theme.Tracked(g, cnt, lf, m + Theme.TrackedW("DOWNLOADS", lf, 0.16f) + Theme.S(18), ListLabelY, Theme.Sub, 0.16f);
            }
        }

        // ---------- menu ----------
        void ToggleMenu() { if (side.IsOpen) CloseMenu(); else { side.Open(); PlaceMenu(); } }
        void CloseMenu() { if (side.IsOpen) { side.Close(); PlaceMenu(); } }

        // ---------- startup ----------
        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            SendMessage(url.Box.Handle, 0x1501, (IntPtr)1, "https://"); // EM_SETCUEBANNER
            OpenGenie();
            url.Box.Focus();
            if (Environment.GetEnvironmentVariable("ULVARK_OPEN_MENU") == "1") ToggleMenu(); // screenshots and UI tests
            PrepareEngine();
        }

        async void PrepareEngine()
        {
            foreach (var u in startUrls) AddUrl(u);
            if (Engine.Ready) { engineReady = true; AfterReady(); return; }
            setupCts = new CancellationTokenSource();
            SetStatus("Setting up yt-dlp and ffmpeg, first run only…", 0);
            try
            {
                await Engine.Install((t, p) => BeginInvoke((Action)(() => { if (!engineReady) SetStatus(t + "  " + (int)(p * 100) + "%", p); })), setupCts.Token);
                engineReady = true;
                SetStatus("", -1);
                AfterReady();
            }
            catch (Exception ex)
            {
                if (IsDisposed) return;
                SetStatus("Error setting up: " + ex.Message, -1);
            }
        }

        async void AfterReady()
        {
            Pump();
            var up = await Engine.AutoUpdate();
            if (up != null && !IsDisposed) { updateNote = up; SetStatus(up, -1); }
        }

        async void ForceUpdate()
        {
            if (!engineReady) return;
            updateNote = "Checking…"; side.Invalidate();
            var r = await Engine.AutoUpdate(true);
            updateNote = r ?? "Could not check"; side.Invalidate();
        }

        void SetStatus(string t, double pct) { status = t; setupPct = pct; Invalidate(); }

        // ---------- clipboard and drag & drop ----------
        protected override void OnActivated(EventArgs e)
        {
            base.OnActivated(e);
            try
            {
                if (!Clipboard.ContainsText()) return;
                string c = Clipboard.GetText().Trim();
                if (c == lastClip) return;
                lastClip = c;
                if (url.Box.TextLength == 0 && IsUrl(c) && !queue.Jobs.Any(j => j.Url == c)) { url.Box.Text = c; url.Box.SelectAll(); url.Box.Focus(); }
            }
            catch { }
        }
        protected override void OnDragEnter(DragEventArgs e)
        {
            e.Effect = e.Data.GetDataPresent(DataFormats.UnicodeText) || e.Data.GetDataPresent(DataFormats.Text) ? DragDropEffects.Copy : DragDropEffects.None;
            base.OnDragEnter(e);
        }
        protected override void OnDragDrop(DragEventArgs e)
        {
            var t = (e.Data.GetData(DataFormats.UnicodeText) ?? e.Data.GetData(DataFormats.Text)) as string;
            if (t != null) { url.Box.Text = t.Trim(); Enqueue(); }
            base.OnDragDrop(e);
        }

        static bool IsUrl(string s) => Uri.TryCreate(s, UriKind.Absolute, out var u) && (u.Scheme == "http" || u.Scheme == "https");

        // ---------- queue ----------
        void Enqueue()
        {
            var parts = url.Box.Text.Split(new[] { '\r', '\n', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            int added = parts.Count(AddUrl);
            if (added > 0) { url.Box.Clear(); lastClip = ""; }
            else if (parts.Length > 0) SetStatus("That is not a link", -1);
            Pump();
            url.Box.Focus();
        }

        bool AddUrl(string u)
        {
            u = u.Trim();
            if (!IsUrl(u)) return false;
            queue.Jobs.Add(new Job { Url = u, Audio = Settings.Audio, MaxHeight = Settings.Audio ? 0 : Settings.MaxHeight });
            if (status == "That is not a link") SetStatus("", -1);
            Redraw();
            return true;
        }

        void Pump()
        {
            if (IsDisposed) return;
            if (engineReady)
            {
                int running = queue.Jobs.Count(j => j.Active);
                foreach (var j in queue.Jobs.Where(j => j.State == JobState.Queued).ToList())
                {
                    if (running >= Parallel) break;
                    Runner.Start(j);
                    running++;
                }
            }
            Redraw();
        }

        void Redraw() { queue.Invalidate(); UpdateTitle(); Invalidate(); }

        void UpdateTitle()
        {
            var act = queue.Jobs.Where(j => j.Active).ToList();
            Text = act.Count == 0 ? AppName : $"{AppName} · {act.Average(j => j.State == JobState.Processing ? 100 : j.Percent):0}%";
        }

        void UpdateFooter()
        {
            linkFolder.Text = Settings.Audio ? Settings.AudioDir : Settings.VideoDir;
            PerformLayout();
        }

        void ShowMenu(Job j, Point at)
        {
            menu.Items.Clear();
            menu.Font = Theme.Sans(9.5f);
            if (j.State == JobState.Done && j.File != null)
            {
                menu.Items.Add("Open", null, (s, e) => OpenJob(j));
                menu.Items.Add("Show in folder", null, (s, e) => OpenFolder(j.Folder, j.File));
            }
            if (!j.Finished) menu.Items.Add("Cancel", null, (s, e) => { Runner.Cancel(j); Pump(); });
            else
            {
                menu.Items.Add("Download again", null, (s, e) =>
                {
                    queue.Jobs[queue.Jobs.IndexOf(j)] = new Job { Url = j.Url, Audio = j.Audio, MaxHeight = j.MaxHeight, Force = true, Title = j.Title };
                    Pump();
                });
                menu.Items.Add("Remove from list", null, (s, e) => { queue.Jobs.Remove(j); queue.Clamp(); Redraw(); });
            }
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Copy link", null, (s, e) => { try { Clipboard.SetText(j.Url); lastClip = j.Url; } catch { } });
            if (j.State == JobState.Failed && j.Error != null)
                menu.Items.Add("Copy error", null, (s, e) => { try { Clipboard.SetText(j.Error); } catch { } });
            menu.Show(queue, at);
        }

        void OpenJob(Job j)
        {
            if (j.State == JobState.Done && j.File != null && File.Exists(j.File)) Shell(j.File);
            else if (j.State == JobState.Done) OpenFolder(j.Folder, null);
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

        void ChooseFolder(bool audio)
        {
            string cur = audio ? Settings.AudioDir : Settings.VideoDir;
            try { Directory.CreateDirectory(cur); } catch { }
            string p = FolderPicker.Pick(Handle, audio ? "Folder for audio" : "Folder for videos", cur);
            if (p == null) return;
            if (audio) Settings.AudioDir = p; else Settings.VideoDir = p;
            Settings.Save(); UpdateFooter(); side.Invalidate();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Escape && side.IsOpen) { CloseMenu(); e.Handled = true; }
            if (e.Control && e.KeyCode == Keys.L) { url.Box.Focus(); url.Box.SelectAll(); e.Handled = true; }
            base.OnKeyDown(e);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // anything half-done resumes next time (--continue)
            setupCts?.Cancel();
            foreach (var j in queue.Jobs.Where(j => j.Active).ToList()) Runner.Cancel(j);
            Settings.Save();
            base.OnFormClosing(e);
        }
    }
}

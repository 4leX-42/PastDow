using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace VidGrab
{
    sealed class MainForm : Form
    {
        const int Parallel = 2;

        readonly GlyphButton btnMin = new GlyphButton(""), btnClose = new GlyphButton("");
        readonly UrlBox url = new UrlBox();
        readonly TextButton btnGo = new TextButton("descargar");
        readonly Choice mode = new Choice("vídeo", "audio");
        readonly Choice quality = new Choice("máxima", "2160p", "1440p", "1080p", "720p");
        static readonly int[] Heights = { 0, 2160, 1440, 1080, 720 };
        readonly QueueView queue = new QueueView();
        readonly TextLink linkClear = new TextLink("limpiar");
        readonly TextLink linkFolder = new TextLink("") { Mono = true };
        readonly TextLink linkCookies = new TextLink("");
        readonly TextLink linkOpen = new TextLink("abrir carpeta");
        readonly System.Windows.Forms.Timer tick = new System.Windows.Forms.Timer { Interval = 66 };
        readonly ContextMenuStrip menu = new ContextMenuStrip { Renderer = new DarkRenderer(), ShowImageMargin = false };
        readonly string[] startUrls;

        string status = "";          // linea de estado (motor, avisos)
        double setupPct = -1;        // progreso de la instalacion del motor; -1 = no hay
        bool engineReady;
        string lastClip = "";
        CancellationTokenSource setupCts;

        static readonly string[] Browsers = { "none", "firefox", "brave", "chrome", "edge" };

        int M => Theme.S(32);

        public MainForm(string[] args)
        {
            startUrls = args;
            Text = "VidGrab";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Theme.Bg;
            ClientSize = new Size(Theme.S(720), Theme.S(600));
            MinimumSize = new Size(Theme.S(560), Theme.S(440));
            DoubleBuffered = true;
            KeyPreview = true;
            AllowDrop = true;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            url.Box.KeyDown += (s, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; Enqueue(); } };
            btnGo.Click += (s, e) => Enqueue();
            btnMin.Click += (s, e) => WindowState = FormWindowState.Minimized;
            btnClose.Click += (s, e) => Close();

            mode.Selected = Settings.Audio ? 1 : 0;
            quality.Selected = Math.Max(0, Array.IndexOf(Heights, Settings.MaxHeight));
            quality.Enabled = !Settings.Audio;
            mode.Changed += (s, e) => { Settings.Audio = mode.Selected == 1; quality.Enabled = !Settings.Audio; UpdateFooter(); Settings.Save(); };
            quality.Changed += (s, e) => { Settings.MaxHeight = Heights[quality.Selected]; Settings.Save(); };

            queue.Empty = new[] { "Nada en la cola.", "Pega un enlace arriba, o arrástralo a la ventana. Puedes pegar varios a la vez." };
            queue.ItemDoubleClick += OpenJob;
            queue.ItemClick += (j, e) => { if (e.Button == MouseButtons.Right) ShowMenu(j, e.Location); };

            linkClear.Click += (s, e) => { queue.Jobs.RemoveAll(j => j.Finished); queue.Clamp(); Redraw(); };
            linkFolder.Click += (s, e) => ChooseFolder();
            linkOpen.Click += (s, e) => OpenFolder(Settings.Audio ? Settings.AudioDir : Settings.VideoDir, null);
            linkCookies.Click += (s, e) =>
            {
                Settings.Cookies = Browsers[(Array.IndexOf(Browsers, Settings.Cookies) + 1) % Browsers.Length];
                Settings.Save(); UpdateFooter();
            };

            Controls.AddRange(new Control[] { btnMin, btnClose, url, btnGo, mode, quality, queue, linkClear, linkFolder, linkCookies, linkOpen });
            UpdateFooter();

            Runner.Finished += j => { try { BeginInvoke((Action)Pump); } catch { } };
            tick.Tick += (s, e) => { if (queue.Jobs.Any(j => j.Active)) { queue.Invalidate(); UpdateTitle(); Invalidate(CountRect); } };
            tick.Start();
        }

        // ---------- ventana sin bordes ----------
        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.Style |= 0x00020000;              // WS_MINIMIZEBOX: minimizar al pulsar en la barra de tareas
                cp.ClassStyle |= 0x00020000;         // CS_DROPSHADOW
                return cp;
            }
        }

        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int v, int size);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, string l);

        protected override void WndProc(ref Message m)
        {
            const int WM_NCHITTEST = 0x84;
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
                else if (p.Y < Theme.S(56)) m.Result = (IntPtr)2; // HTCAPTION: arrastrar desde arriba
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int on = 1; DwmSetWindowAttribute(Handle, 20, ref on, 4);           // modo oscuro
            int round = 2; DwmSetWindowAttribute(Handle, 33, ref round, 4);     // esquinas redondeadas de W11
            int border = ColorTranslator.ToWin32(Theme.Line); DwmSetWindowAttribute(Handle, 34, ref border, 4);
        }

        // ---------- disposicion ----------
        int ListLabelY => quality.Bottom + Theme.S(40);
        Rectangle CountRect => new Rectangle(M, ListLabelY - Theme.S(2), Theme.S(300), Theme.S(22));

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            int W = ClientSize.Width, H = ClientSize.Height, m = M;
            int bw = Theme.S(44), bh = Theme.S(36);
            btnClose.SetBounds(W - bw - Theme.S(6), Theme.S(6), bw, bh);
            btnMin.SetBounds(btnClose.Left - bw, Theme.S(6), bw, bh);

            int goW = btnGo.Measure();
            url.SetBounds(m, Theme.S(64), W - m * 2 - goW - Theme.S(28), Theme.S(56));
            btnGo.SetBounds(W - m - goW, url.Bottom - Theme.S(34), goW, Theme.S(28));

            int oy = url.Bottom + Theme.S(16), oh = Theme.S(30);
            mode.SetBounds(m, oy, mode.Measure(), oh);
            quality.SetBounds(mode.Right + Theme.S(36), oy, quality.Measure(), oh);

            int cw = linkClear.Measure();
            linkClear.SetBounds(W - m - cw, ListLabelY - Theme.S(1), cw, Theme.S(20));

            int qTop = ListLabelY + Theme.S(30), foot = Theme.S(58);
            queue.SetBounds(m - Theme.S(10), qTop, W - m * 2 + Theme.S(20), H - qTop - foot);

            int fy = H - Theme.S(38), lh = Theme.S(20);
            int ow = linkOpen.Measure(), kw = linkCookies.Measure();
            linkOpen.SetBounds(W - m - ow, fy, ow, lh);
            linkCookies.SetBounds(linkOpen.Left - Theme.S(28) - kw, fy, kw, lh);
            linkFolder.SetBounds(m, fy, Math.Max(0, linkCookies.Left - Theme.S(28) - m), lh);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = Theme.Crisp(e.Graphics);
            int m = M;
            TextRenderer.DrawText(g, "VidGrab", Theme.Strong(11), new Point(m, Theme.S(20)), Theme.Text, TextFormatFlags.NoPadding);

            // separador fino entre modo y calidad
            using (var b = new SolidBrush(Theme.Line))
                g.FillRectangle(b, mode.Right + Theme.S(18), mode.Top + Theme.S(8), Math.Max(1, Theme.S(1)), mode.Height - Theme.S(16));

            // estado a la derecha de las opciones
            if (status.Length > 0)
            {
                var sr = new Rectangle(quality.Right + Theme.S(24), mode.Top, ClientSize.Width - quality.Right - Theme.S(24) - m, mode.Height);
                TextRenderer.DrawText(g, status, Theme.Sans(9), sr, status.StartsWith("Error") ? Theme.Red : Theme.Sub,
                    TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            }
            // instalacion del motor: linea fina bajo el campo
            if (setupPct >= 0)
                using (var b = new SolidBrush(Theme.Sub))
                    g.FillRectangle(b, url.Left, url.Bottom, (int)(url.Width * setupPct), Math.Max(1, Theme.S(1)));

            int act = queue.Jobs.Count(j => j.Active), pend = queue.Jobs.Count(j => j.State == JobState.Queued);
            string lbl = "Descargas";
            TextRenderer.DrawText(g, lbl, Theme.Sans(9), new Point(m, ListLabelY), Theme.Faint, TextFormatFlags.NoPadding);
            if (act + pend > 0)
            {
                string cnt = act + " en curso" + (pend > 0 ? ", " + pend + " esperando" : "");
                TextRenderer.DrawText(g, cnt, Theme.Sans(9), new Point(m + Theme.TextW(lbl, Theme.Sans(9)) + Theme.S(14), ListLabelY), Theme.Sub, TextFormatFlags.NoPadding);
            }
        }

        // ---------- arranque ----------
        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            SendMessage(url.Box.Handle, 0x1501, (IntPtr)1, "Pega un enlace"); // EM_SETCUEBANNER
            url.Box.Focus();
            PrepareEngine();
        }

        async void PrepareEngine()
        {
            foreach (var u in startUrls) AddUrl(u);
            if (Engine.Ready) { engineReady = true; AfterReady(); return; }
            setupCts = new CancellationTokenSource();
            SetStatus("Preparando yt-dlp y ffmpeg, solo esta vez…", 0);
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
                SetStatus("Error preparando el motor: " + ex.Message, -1);
            }
        }

        async void AfterReady()
        {
            Pump();
            var up = await Engine.AutoUpdate();
            if (up != null && !IsDisposed) SetStatus(up, -1);
        }

        void SetStatus(string t, double pct) { status = t; setupPct = pct; Invalidate(); }

        // ---------- portapapeles y arrastrar ----------
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

        // ---------- cola ----------
        void Enqueue()
        {
            var parts = url.Box.Text.Split(new[] { '\r', '\n', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            int added = parts.Count(AddUrl);
            if (added > 0) { url.Box.Clear(); lastClip = ""; }
            else if (parts.Length > 0) SetStatus("Eso no es un enlace", -1);
            Pump();
            url.Box.Focus();
        }

        bool AddUrl(string u)
        {
            u = u.Trim();
            if (!IsUrl(u)) return false;
            queue.Jobs.Add(new Job { Url = u, Audio = Settings.Audio, MaxHeight = Settings.Audio ? 0 : Settings.MaxHeight });
            if (status == "Eso no es un enlace") SetStatus("", -1);
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
            Text = act.Count == 0 ? "VidGrab" : $"VidGrab · {act.Average(j => j.State == JobState.Processing ? 100 : j.Percent):0}%";
        }

        void UpdateFooter()
        {
            linkFolder.Text = Settings.Audio ? Settings.AudioDir : Settings.VideoDir;
            linkCookies.Text = Settings.Cookies == "none" ? "sin cookies" : "cookies de " + Settings.Cookies;
            PerformLayout();
        }

        void ShowMenu(Job j, Point at)
        {
            menu.Items.Clear();
            menu.Font = Theme.Sans(9.5f);
            if (j.State == JobState.Done && j.File != null)
            {
                menu.Items.Add("Abrir", null, (s, e) => OpenJob(j));
                menu.Items.Add("Mostrar en la carpeta", null, (s, e) => OpenFolder(j.Folder, j.File));
            }
            if (!j.Finished) menu.Items.Add("Cancelar", null, (s, e) => { Runner.Cancel(j); Pump(); });
            else
            {
                menu.Items.Add("Volver a descargar", null, (s, e) =>
                {
                    queue.Jobs[queue.Jobs.IndexOf(j)] = new Job { Url = j.Url, Audio = j.Audio, MaxHeight = j.MaxHeight, Force = true, Title = j.Title };
                    Pump();
                });
                menu.Items.Add("Quitar de la lista", null, (s, e) => { queue.Jobs.Remove(j); queue.Clamp(); Redraw(); });
            }
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Copiar enlace", null, (s, e) => { try { Clipboard.SetText(j.Url); lastClip = j.Url; } catch { } });
            if (j.State == JobState.Failed && j.Error != null)
                menu.Items.Add("Copiar error", null, (s, e) => { try { Clipboard.SetText(j.Error); } catch { } });
            menu.Show(queue, at);
        }

        void OpenJob(Job j)
        {
            if (j.State == JobState.Done && j.File != null && File.Exists(j.File))
                try { Process.Start(new ProcessStartInfo(j.File) { UseShellExecute = true }); } catch { }
            else if (j.State == JobState.Done) OpenFolder(j.Folder, null);
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

        void ChooseFolder()
        {
            using (var d = new FolderBrowserDialog { Description = Settings.Audio ? "Carpeta para el audio" : "Carpeta para los vídeos", ShowNewFolderButton = true })
            {
                d.SelectedPath = Settings.Audio ? Settings.AudioDir : Settings.VideoDir;
                if (d.ShowDialog(this) != DialogResult.OK) return;
                if (Settings.Audio) Settings.AudioDir = d.SelectedPath; else Settings.VideoDir = d.SelectedPath;
                Settings.Save(); UpdateFooter();
            }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.Control && e.KeyCode == Keys.L) { url.Box.Focus(); url.Box.SelectAll(); e.Handled = true; }
            base.OnKeyDown(e);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            // lo que quede a medias se reanuda la proxima vez (--continue)
            setupCts?.Cancel();
            foreach (var j in queue.Jobs.Where(j => j.Active).ToList()) Runner.Cancel(j);
            Settings.Save();
            base.OnFormClosing(e);
        }
    }
}

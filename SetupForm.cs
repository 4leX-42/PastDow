using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Ulvark
{
    /// <summary>Selectable row: a ring that fills when chosen, uppercase title, one line underneath.</summary>
    sealed class OptionRow : Canvas
    {
        public string Title, Sub;
        bool selected; double selT;
        public event EventHandler Picked;
        public OptionRow(string title, string sub) { Title = title; Sub = sub; Cursor = Cursors.Hand; }
        public bool Selected { get => selected; set { selected = value; Invalidate(); } }
        protected override void OnMouseClick(MouseEventArgs e) { Picked?.Invoke(this, EventArgs.Empty); base.OnMouseClick(e); }
        public override bool Step(double dt)
        {
            bool b = base.Step(dt);
            double target = selected ? 1 : 0;
            if (selT == target) return b;
            selT = target > selT ? Math.Min(1, selT + dt * 6) : Math.Max(0, selT - dt * 6);
            return true;
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = Theme.Crisp(e.Graphics);
            double s = Theme.EaseOut(selT);
            int hair = Math.Max(1, Theme.S(1));
            // frame: hairline that brightens when selected or hovered
            using (var p = new Pen(Theme.Mix(Theme.Line, Theme.Text, Math.Max(s * 0.85, hoverT * 0.35)), hair))
                g.DrawRectangle(p, 0, 0, Width - hair, Height - hair);
            int r = Theme.S(8), cx = Theme.S(28), cy = Height / 2;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var p = new Pen(Theme.Mix(Theme.Faint, Theme.Text, s), Math.Max(1f, Theme.S(1.3f)))) g.DrawEllipse(p, cx - r, cy - r, r * 2, r * 2);
            int dot = (int)(Theme.S(4) * s);
            if (dot > 0) using (var b = new SolidBrush(Theme.Text)) g.FillEllipse(b, cx - dot, cy - dot, dot * 2, dot * 2);
            g.SmoothingMode = SmoothingMode.None;
            var tf = Theme.Display(11);
            int x = Theme.S(54);
            Theme.Tracked(g, Title, tf, x, cy - Theme.S(20), Theme.Mix(Theme.Sub, Theme.Text, Math.Max(s, hoverT)), 0.16f);
            TextRenderer.DrawText(g, Sub, Theme.Sans(9), new Point(x, cy + Theme.S(4)), Theme.Faint, TextFormatFlags.NoPadding);
        }
    }

    /// <summary>Square toggle with a label.</summary>
    sealed class Toggle : Canvas
    {
        bool on; double onT;
        public Toggle(string text, bool on) { Text = text; this.on = on; onT = on ? 1 : 0; Cursor = Cursors.Hand; }
        public bool On => on;
        protected override void OnMouseClick(MouseEventArgs e) { on = !on; base.OnMouseClick(e); }
        public override bool Step(double dt)
        {
            bool b = base.Step(dt);
            double target = on ? 1 : 0;
            if (onT == target) return b;
            onT = target > onT ? Math.Min(1, onT + dt * 7) : Math.Max(0, onT - dt * 7);
            return true;
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = Theme.Crisp(e.Graphics);
            int box = Theme.S(14), y = (Height - box) / 2;
            using (var p = new Pen(Theme.Mix(Theme.Faint, Theme.Text, Math.Max(onT, hoverT * 0.5)))) g.DrawRectangle(p, 0, y, box, box);
            int inner = (int)((box - Theme.S(6)) * Theme.EaseOut(onT));
            if (inner > 0) using (var b = new SolidBrush(Theme.Text))
                g.FillRectangle(b, (box - inner) / 2 + 1, y + (box - inner) / 2 + 1, inner, inner);
            var f = Theme.Display(8.5f);
            Theme.Tracked(g, Text, f, box + Theme.S(14), (Height - Theme.TextH(f)) / 2, Theme.Mix(Theme.Sub, Theme.Text, hoverT), 0.16f);
        }
    }

    /// <summary>
    /// First-run choice (install or portable) and the uninstall confirmation. Same look as the app: black,
    /// horizon glow, letter-spaced type, one animated clock.
    /// </summary>
    sealed class SetupForm : Form
    {
        public enum Kind { Setup, Uninstall }
        public bool InstallChosen => install.Selected;
        public bool Desktop => desktop.On;

        readonly Kind kind;
        readonly GlyphButton btnClose = new GlyphButton("");
        readonly OptionRow install, portable;
        readonly Toggle desktop = new Toggle("ALSO ADD A DESKTOP SHORTCUT", false);
        readonly SweepButton go;
        readonly TextLink cancel = new TextLink("CANCEL");
        readonly Timer frame = new Timer { Interval = 15 };
        readonly Stopwatch clock = Stopwatch.StartNew();
        double lastT;
        string error = "";
        public Func<string> OnContinue;   // returns an error to show, or null to close with OK

        public SetupForm(Kind kind)
        {
            this.kind = kind;
            Text = "Ulvark";
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Theme.Bg;
            DoubleBuffered = true;
            KeyPreview = true;
            Opacity = 0;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }

            bool update = Installer.IsInstalled;
            install = new OptionRow(update ? "UPDATE" : "INSTALL",
                update ? "Replace the installed copy (" + (Installer.InstalledVersion ?? "older") + ") with " + Installer.Version
                       : "Start menu, Windows search, uninstall from Settings") { Selected = true };
            portable = new OptionRow("PORTABLE", "Run from this file · nothing is installed");
            install.Picked += (s, e) => { install.Selected = true; portable.Selected = false; desktop.Enabled = true; Invalidate(); };
            portable.Picked += (s, e) => { install.Selected = false; portable.Selected = true; desktop.Enabled = false; Invalidate(); };

            go = new SweepButton(kind == Kind.Setup ? "CONTINUE" : "UNINSTALL");
            go.Click += (s, e) => Continue();
            cancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
            btnClose.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };

            if (kind == Kind.Setup)
            {
                ClientSize = new Size(Theme.S(560), Theme.S(470));
                Controls.AddRange(new Control[] { btnClose, install, portable, desktop, go });
            }
            else
            {
                ClientSize = new Size(Theme.S(520), Theme.S(300));
                Controls.AddRange(new Control[] { btnClose, go, cancel });
            }
            Backdrop.Image = Theme.Backdrop(ClientSize);
            frame.Tick += (s, e) => Animate();
            frame.Start();
        }

        void Continue()
        {
            var err = OnContinue?.Invoke();
            if (err != null) { error = err; Invalidate(); return; }
            DialogResult = DialogResult.OK;
            Close();
        }

        void Animate()
        {
            double t = clock.Elapsed.TotalSeconds, dt = Math.Min(0.05, t - lastT);
            lastT = t;
            if (Opacity < 1) Opacity = Math.Min(1, t / 0.28);
            foreach (Control c in Controls)
                if (c is IAnim a && c.Visible && a.Step(dt)) c.Invalidate();
            if (t < 1.2) Invalidate(new Rectangle(0, Theme.S(60), ClientSize.Width, Theme.S(110)));
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            if (install == null) return;
            int W = ClientSize.Width, H = ClientSize.Height, m = Theme.S(40);
            btnClose.SetBounds(W - Theme.S(50), Theme.S(16), Theme.S(40), Theme.S(40));
            int goW = go.Measure(), goH = Theme.S(46);
            go.SetBounds(W - m - goW, H - m - goH, goW, goH);
            if (kind == Kind.Setup)
            {
                int rh = Theme.S(72), y = Theme.S(150);
                install.SetBounds(m, y, W - m * 2, rh);
                portable.SetBounds(m, y + rh + Theme.S(12), W - m * 2, rh);
                desktop.SetBounds(m, portable.Bottom + Theme.S(26), Theme.S(330), Theme.S(24));
            }
            else
            {
                int cw = cancel.Measure();
                cancel.SetBounds(go.Left - Theme.S(32) - cw, go.Top + (goH - Theme.S(22)) / 2, cw, Theme.S(22));
            }
        }

        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int v, int size);
        protected override CreateParams CreateParams { get { var cp = base.CreateParams; cp.Style |= 0x00020000; cp.ClassStyle |= 0x00020000; return cp; } }
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int on = 1; DwmSetWindowAttribute(Handle, 20, ref on, 4);
            int round = 2; DwmSetWindowAttribute(Handle, 33, ref round, 4);
            int border = ColorTranslator.ToWin32(Color.FromArgb(28, 28, 28)); DwmSetWindowAttribute(Handle, 34, ref border, 4);
        }
        protected override void WndProc(ref Message m)
        {
            base.WndProc(ref m);
            if (m.Msg == 0x84 && (int)m.Result == 1)
            {
                int lp = m.LParam.ToInt32();
                var p = PointToClient(new Point((short)(lp & 0xFFFF), (short)(lp >> 16)));
                if (p.Y < Theme.S(72)) m.Result = (IntPtr)2; // drag from the top
            }
        }
        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter) { Continue(); e.Handled = true; }
            if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); }
            base.OnKeyDown(e);
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            if (Backdrop.Image != null) e.Graphics.DrawImage(Backdrop.Image, e.ClipRectangle, e.ClipRectangle, GraphicsUnit.Pixel);
            else e.Graphics.Clear(Theme.Bg);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = Theme.Crisp(e.Graphics);
            int m = Theme.S(40);
            Theme.Tracked(g, "ULVARK", Theme.Bold(13), m, Theme.S(26), Theme.Text, 0.42f);
            // headline rises in
            double a = Theme.EaseOut(lastT / 0.7);
            int dy = (int)(Theme.S(10) * (1 - a));
            string head = kind == Kind.Setup ? "HOW DO YOU WANT TO RUN IT" : "UNINSTALL ULVARK";
            string sub = kind == Kind.Setup ? "You can change your mind later: run the file again"
                                            : "Removes the app, its shortcuts and its engine · your downloads stay where they are";
            Theme.Tracked(g, head, Theme.Display(15), m, Theme.S(82) + dy, Theme.Mix(Theme.Bg, Theme.Text, a), 0.12f);
            TextRenderer.DrawText(g, sub, Theme.Sans(9.5f), new Point(m, Theme.S(114) + dy), Theme.Mix(Theme.Bg, Theme.Sub, a), TextFormatFlags.NoPadding);
            if (error.Length > 0)
                TextRenderer.DrawText(g, error, Theme.Sans(9), new Rectangle(m, go.Top, go.Left - m - Theme.S(20), go.Height), Theme.Red,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
        }
    }
}

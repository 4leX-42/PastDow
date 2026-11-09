using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Ulvark
{
    /// <summary>Something that animates. Step() advances it by dt seconds and says whether it needs repainting.</summary>
    interface IAnim { bool Step(double dt); }

    /// <summary>The window backdrop; child controls paint their own slice of it so the glow shows through.</summary>
    static class Backdrop { public static Bitmap Image; public static Rectangle Cover; public static Color CoverColor; }

    /// <summary>Base: double-buffered, paints the backdrop slice, eases a hover value 0..1.</summary>
    class Canvas : Control, IAnim
    {
        public Canvas()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                     ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Bg;
        }
        protected bool hover;
        protected double hoverT;
        protected virtual double HoverSpeed => 7;   // 1/s: ~140 ms to full
        protected override void OnMouseEnter(EventArgs e) { hover = true; base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; base.OnMouseLeave(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }

        public virtual bool Step(double dt)
        {
            double target = hover && Enabled ? 1 : 0;
            if (hoverT == target) return false;
            hoverT = target > hoverT ? Math.Min(1, hoverT + dt * HoverSpeed) : Math.Max(0, hoverT - dt * HoverSpeed);
            return true;
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            var img = Backdrop.Image;
            if (img != null && Parent is Form)
                e.Graphics.DrawImage(img, ClientRectangle, new Rectangle(Left, Top, Width, Height), GraphicsUnit.Pixel);
            else e.Graphics.Clear(BackColor);
            // the side menu sits under the window buttons: paint its colour where it covers us
            var cover = Rectangle.Intersect(Backdrop.Cover, Bounds);
            if (!cover.IsEmpty && Parent is Form)
            {
                cover.Offset(-Left, -Top);
                using (var b = new SolidBrush(Backdrop.CoverColor)) e.Graphics.FillRectangle(b, cover);
            }
        }

        protected static void Rule(Graphics g, Color c, int x, int y, int w, int h = 0)
        {
            using (var b = new SolidBrush(c)) g.FillRectangle(b, x, y, w, h > 0 ? h : Math.Max(1, Theme.S(1)));
        }
    }

    /// <summary>
    /// Outlined uppercase button (SpaceX): on hover white fills it from the left and the label inverts where the
    /// fill passes; the arrow nudges right.
    /// </summary>
    sealed class SweepButton : Canvas
    {
        readonly Font font = Theme.Bold(9.5f);
        public SweepButton(string text) { Text = text; Cursor = Cursors.Hand; }
        public int Measure() => Theme.TrackedW(Text, font, 0.18f) + Theme.S(28 + 22 + 24);
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = Theme.Crisp(e.Graphics);
            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            Color line = Enabled ? Theme.Text : Theme.Faint;
            double t = Theme.EaseOut(hoverT);
            int fillW = (int)(Width * t);
            if (fillW > 0) using (var b = new SolidBrush(Theme.Text)) g.FillRectangle(b, 0, 0, fillW, Height);
            using (var p = new Pen(line)) g.DrawRectangle(p, r);

            int tw = Theme.TrackedW(Text, font, 0.18f), th = Theme.TextH(font);
            int arrowW = Theme.S(14), gap = Theme.S(10);
            int x = (Width - tw - gap - arrowW) / 2, y = (Height - th) / 2;
            int ax = x + tw + gap + (int)(Theme.S(4) * t);
            void Label(Color c)
            {
                Theme.Tracked(g, Text, font, x, y, c, 0.18f);
                using (var p = new Pen(c, Math.Max(1f, Theme.S(1.4f))))
                {
                    int cy = Height / 2;
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.DrawLine(p, ax, cy, ax + arrowW, cy);
                    g.DrawLine(p, ax + arrowW - Theme.S(5), cy - Theme.S(5), ax + arrowW, cy);
                    g.DrawLine(p, ax + arrowW - Theme.S(5), cy + Theme.S(5), ax + arrowW, cy);
                    g.SmoothingMode = SmoothingMode.None;
                }
            }
            Label(line);
            if (fillW > 0)
            {
                var old = g.Clip;
                g.SetClip(new Rectangle(0, 0, fillW, Height));
                Label(Theme.Bg);
                g.Clip = old;
            }
        }
    }

    /// <summary>Uppercase options; a line slides under the chosen one.</summary>
    sealed class Choice : Canvas
    {
        public readonly string[] Items;
        int sel, hot = -1;
        double lineX = -1, lineW;
        public event EventHandler Changed;
        readonly Font font = Theme.Display(9);
        const float Em = 0.14f;
        public Choice(params string[] items) { Items = items; Cursor = Cursors.Hand; }
        public int Selected { get => sel; set { if (value != sel) { sel = value; Invalidate(); } } }

        int Gap => Theme.S(22);
        int[] Starts()
        {
            var x = new int[Items.Length + 1];
            for (int i = 0; i < Items.Length; i++) x[i + 1] = x[i] + Theme.TrackedW(Items[i], font, Em) + Gap;
            return x;
        }
        public int Measure() => Starts()[Items.Length] - Gap + Theme.S(2);
        int Hit(int px)
        {
            var x = Starts();
            for (int i = 0; i < Items.Length; i++) if (px >= x[i] - Gap / 2 && px < x[i + 1] - Gap / 2) return i;
            return -1;
        }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            int h = Hit(e.X);
            if (h != hot) { hot = h; Invalidate(); }
            base.OnMouseMove(e);
        }
        protected override void OnMouseLeave(EventArgs e) { hot = -1; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseClick(MouseEventArgs e)
        {
            int i = Hit(e.X);
            if (Enabled && i >= 0 && i != sel) { sel = i; Changed?.Invoke(this, EventArgs.Empty); }
            base.OnMouseClick(e);
        }
        public override bool Step(double dt)
        {
            base.Step(dt);
            var x = Starts();
            double tx = x[sel], tw = Theme.TrackedW(Items[sel], font, Em);
            if (lineX < 0) { lineX = tx; lineW = tw; return true; }
            if (Math.Abs(lineX - tx) < 0.5 && Math.Abs(lineW - tw) < 0.5) { if (lineX != tx || lineW != tw) { lineX = tx; lineW = tw; return true; } return false; }
            double k = 1 - Math.Exp(-dt * 16);
            lineX += (tx - lineX) * k; lineW += (tw - lineW) * k;
            return true;
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = Theme.Crisp(e.Graphics);
            var x = Starts();
            int h = Theme.TextH(font), y = (Height - h) / 2 - Theme.S(2);
            for (int i = 0; i < Items.Length; i++)
            {
                Color c = !Enabled ? Theme.Faint : i == sel || i == hot ? Theme.Text : Theme.Sub;
                Theme.Tracked(g, Items[i], font, x[i], y, c, Em);
            }
            if (Enabled && lineX >= 0) Rule(g, Theme.Text, (int)lineX, y + h + Theme.S(6), (int)lineW);
        }
    }

    /// <summary>Big link field: no box, a hairline underneath and a white line that draws in from the left on focus.</summary>
    sealed class UrlBox : Canvas
    {
        public readonly TextBox Box = new TextBox();
        double focusT;
        public UrlBox()
        {
            Box.BorderStyle = BorderStyle.None;
            Box.BackColor = Theme.Bg;
            Box.ForeColor = Theme.Text;
            Box.Font = Theme.Light(17);
            Controls.Add(Box);
            Cursor = Cursors.IBeam;
        }
        protected override void OnClick(EventArgs e) { Box.Focus(); base.OnClick(e); }
        protected override void OnLayout(LayoutEventArgs e)
        {
            Box.SetBounds(0, Height - Box.PreferredHeight - Theme.S(12), Width, Box.PreferredHeight);
            base.OnLayout(e);
        }
        public override bool Step(double dt)
        {
            double target = Box.Focused ? 1 : 0;
            if (focusT == target) return false;
            focusT = target > focusT ? Math.Min(1, focusT + dt * 3.2) : Math.Max(0, focusT - dt * 4);
            return true;
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            int y = Height - Math.Max(1, Theme.S(1));
            Rule(e.Graphics, Theme.Line, 0, y, Width);
            int w = (int)(Width * Theme.EaseOut(focusT));
            if (w > 0) Rule(e.Graphics, Theme.Text, 0, y, w);
        }
    }

    /// <summary>Glyph button (minimise, close, menu).</summary>
    sealed class GlyphButton : Canvas
    {
        string glyph;
        public string Label;   // optional uppercase text left of the glyph
        public GlyphButton(string glyph) { this.glyph = glyph; Cursor = Cursors.Hand; }
        public void SetGlyph(string g) { glyph = g; Invalidate(); }
        public int Measure() => Label == null ? Theme.S(40) : Theme.TrackedW(Label, Theme.Display(8.5f), 0.18f) + Theme.S(40);
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = Theme.Crisp(e.Graphics);
            var c = Theme.Mix(Theme.Sub, Theme.Text, hoverT);
            int gw = Theme.S(28);
            TextRenderer.DrawText(g, glyph, Theme.Icon(Label == null ? 7.5f : 9f), new Rectangle(Width - gw - Theme.S(6), 0, gw, Height), c,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
            if (Label != null)
            {
                var f = Theme.Display(8.5f);
                Theme.Tracked(g, Label, f, 0, (Height - Theme.TextH(f)) / 2, c, 0.18f);
            }
        }
    }

    /// <summary>Uppercase text link.</summary>
    sealed class TextLink : Canvas
    {
        public bool Mono;
        public TextLink(string text) { Text = text; Cursor = Cursors.Hand; }
        Font Font_ => Mono ? Theme.Mono(8.5f) : Theme.Display(8.5f);
        public int Measure() => (Mono ? Theme.TextW(Text, Font_) : Theme.TrackedW(Text, Font_, 0.14f)) + Theme.S(2);
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = Theme.Crisp(e.Graphics);
            var c = Theme.Mix(Mono ? Theme.Faint : Theme.Sub, Theme.Text, hoverT);
            if (Mono)
                TextRenderer.DrawText(g, Text, Font_, ClientRectangle, c,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.PathEllipsis | TextFormatFlags.SingleLine);
            else Theme.Tracked(g, Text, Font_, 0, (Height - Theme.TextH(Font_)) / 2, c, 0.14f);
        }
    }

    /// <summary>
    /// Download list. Rows fade and rise in, a spinner turns while yt-dlp fetches the video info, the percent
    /// eases towards the real value and the row's bottom hairline doubles as the progress bar.
    /// </summary>
    sealed class QueueView : Canvas
    {
        public readonly List<Job> Jobs = new List<Job>();
        public event Action<Job, MouseEventArgs> ItemClick;
        public event Action<Job> ItemDoubleClick;
        public string[] Empty = new string[0];
        int scroll, hotIndex = -1;
        double spin, emptyT;
        int RowH => Theme.S(70);
        const int AppearMs = 420;

        int MaxScroll => Math.Max(0, Jobs.Count * RowH - Height);
        public void Clamp() { scroll = Math.Max(0, Math.Min(scroll, MaxScroll)); }

        public override bool Step(double dt)
        {
            bool dirty = false;
            if (Jobs.Count == 0) { if (emptyT < 1) { emptyT = Math.Min(1, emptyT + dt * 2.5); dirty = true; } }
            else emptyT = 0;
            long now = Environment.TickCount;
            foreach (var j in Jobs)
            {
                if (now - j.Added < AppearMs + 50) dirty = true;
                double target = j.State == JobState.Running ? j.Percent : j.State == JobState.Processing || j.State == JobState.Done ? 100 : 0;
                if (Math.Abs(j.Shown - target) > 0.05) { j.Shown += (target - j.Shown) * (1 - Math.Exp(-dt * 9)); dirty = true; }
                else j.Shown = target;
                if (j.State == JobState.Running && !j.Started || j.State == JobState.Processing) dirty = true;
            }
            if (dirty) spin = (spin + dt * 300) % 360;
            return dirty;
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            scroll -= e.Delta / 120 * RowH;
            Clamp(); Invalidate();
            base.OnMouseWheel(e);
        }
        int IndexAt(Point p) { int i = (p.Y + scroll) / RowH; return i >= 0 && i < Jobs.Count ? i : -1; }
        Job At(Point p) { int i = IndexAt(p); return i < 0 ? null : Jobs[i]; }
        protected override void OnMouseMove(MouseEventArgs e)
        {
            int i = IndexAt(e.Location);
            if (i != hotIndex) { hotIndex = i; Invalidate(); }
            var j = At(e.Location);
            Cursor = j != null && j.State == JobState.Done ? Cursors.Hand : Cursors.Default;
            base.OnMouseMove(e);
        }
        protected override void OnMouseLeave(EventArgs e) { hotIndex = -1; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseClick(MouseEventArgs e) { var j = At(e.Location); if (j != null) ItemClick?.Invoke(j, e); base.OnMouseClick(e); }
        protected override void OnMouseDoubleClick(MouseEventArgs e) { var j = At(e.Location); if (j != null) ItemDoubleClick?.Invoke(j); base.OnMouseDoubleClick(e); }

        void Spinner(Graphics g, int cx, int cy, int r, Color c)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var p = new Pen(Theme.Line, Math.Max(1f, Theme.S(1.5f)))) g.DrawEllipse(p, cx - r, cy - r, r * 2, r * 2);
            using (var p = new Pen(c, Math.Max(1f, Theme.S(1.5f))) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                g.DrawArc(p, cx - r, cy - r, r * 2, r * 2, (float)spin, 90);
            g.SmoothingMode = SmoothingMode.None;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = Theme.Crisp(e.Graphics);
            if (Jobs.Count == 0)
            {
                int ey = Theme.S(6) + (int)(Theme.S(8) * (1 - Theme.EaseOut(emptyT)));
                var c = Theme.Mix(Theme.Bg, Theme.Faint, Theme.EaseOut(emptyT));
                foreach (var line in Empty)
                {
                    TextRenderer.DrawText(g, line, Theme.Sans(9.5f), new Point(Theme.S(12), ey), c, TextFormatFlags.NoPadding);
                    ey += Theme.S(22);
                }
                return;
            }
            Clamp();
            int h = RowH, hair = Math.Max(1, Theme.S(1)), padX = Theme.S(12);
            long now = Environment.TickCount;
            var stFont = Theme.Display(8.5f);
            for (int i = scroll / h; i < Jobs.Count; i++)
            {
                var j = Jobs[i];
                double a = Theme.EaseOut((now - j.Added) / (double)AppearMs);
                int y = i * h - scroll + (int)(Theme.S(14) * (1 - a));
                if (y > Height) break;
                if (i == hotIndex) using (var b = new SolidBrush(Color.FromArgb(18, 255, 255, 255))) g.FillRectangle(b, 0, y, Width, h - hair);

                string st; Color sc;
                bool spinning = j.State == JobState.Running && !j.Started || j.State == JobState.Processing;
                switch (j.State)
                {
                    case JobState.Running: st = j.Started ? ((int)j.Shown) + "%" : "FETCHING"; sc = Theme.Text; break;
                    case JobState.Processing: st = "MERGING"; sc = Theme.Sub; break;
                    case JobState.Done: st = "DONE"; sc = Theme.Text; break;
                    case JobState.Skipped: st = "SKIPPED"; sc = Theme.Faint; break;
                    case JobState.Failed: st = "FAILED"; sc = Theme.Red; break;
                    case JobState.Canceled: st = "CANCELED"; sc = Theme.Faint; break;
                    default: st = "QUEUED"; sc = Theme.Faint; break;
                }
                var f = j.State == JobState.Running && j.Started ? Theme.Display(13) : stFont;
                int stW = Theme.TrackedW(st, f, 0.14f), stH = Theme.TextH(f);
                int stX = Width - padX - stW, stY = y + Theme.S(24) - stH / 2;
                Theme.Tracked(g, st, f, stX, stY, Theme.Mix(Theme.Bg, sc, a), 0.14f);
                if (spinning) Spinner(g, stX - Theme.S(16), y + Theme.S(24), Theme.S(6), Theme.Mix(Theme.Bg, Theme.Text, a));

                bool dim = j.State == JobState.Skipped || j.State == JobState.Canceled || j.State == JobState.Queued;
                int rightGap = stW + padX * 2 + (spinning ? Theme.S(26) : 0);
                TextRenderer.DrawText(g, j.Title ?? j.Url, Theme.Display(11.5f),
                    new Rectangle(padX, y + Theme.S(13), Width - padX - rightGap, Theme.S(24)), Theme.Mix(Theme.Bg, dim ? Theme.Sub : Theme.Text, a),
                    TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
                TextRenderer.DrawText(g, j.Detail, Theme.Mono(8.5f),
                    new Rectangle(padX, y + Theme.S(41), Width - padX * 2, Theme.S(18)),
                    Theme.Mix(Theme.Bg, j.State == JobState.Failed ? Theme.Red : Theme.Faint, a),
                    TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);

                int ly = y + h - hair;
                Rule(g, Theme.Mix(Theme.Bg, Theme.Line, a), 0, ly, (int)(Width * a));
                if (j.State == JobState.Running || j.State == JobState.Processing)
                {
                    int w = (int)(Width * j.Shown / 100.0);
                    if (w > 0)
                    {
                        Rule(g, Theme.Text, 0, ly, w, hair);
                        // soft glow at the head of the bar
                        int gw = Theme.S(60);
                        var gr = new Rectangle(Math.Max(0, w - gw), ly - Theme.S(3), Math.Min(gw, w), Theme.S(7));
                        if (gr.Width > 1)
                            using (var lb = new LinearGradientBrush(new Rectangle(gr.X - 1, gr.Y, gr.Width + 2, gr.Height), Color.FromArgb(0, 255, 255, 255), Color.FromArgb(60, 255, 255, 255), 0f))
                            { lb.WrapMode = WrapMode.TileFlipXY; g.FillRectangle(lb, gr); }
                    }
                }
            }
            if (MaxScroll > 0)
            {
                int total = Jobs.Count * RowH, th = Math.Max(Theme.S(24), Height * Height / total);
                int ty = (int)((long)(Height - th) * scroll / MaxScroll);
                Rule(g, Theme.Line, Width - Theme.S(2), ty, Theme.S(2), th);
            }
        }
    }

    /// <summary>
    /// Side menu (x.ai / SpaceX): slides in from the right, items rise in one after another.
    /// Each item: uppercase label and a value line underneath.
    /// </summary>
    sealed class SideMenu : Canvas
    {
        public sealed class Item { public string Label; public Func<string> Value; public Action Click; }
        public readonly List<Item> Items = new List<Item>();
        public bool IsOpen { get; private set; }
        public double OpenT;          // 0 closed .. 1 open
        long openedAt;
        int hot = -1;
        public event Action Moved;

        public SideMenu() { BackColor = Color.FromArgb(6, 6, 6); Visible = false; Cursor = Cursors.Default; }
        public void Open() { if (IsOpen) return; IsOpen = true; openedAt = Environment.TickCount; Visible = true; BringToFront(); }
        public void Close() { IsOpen = false; }

        int ItemTop(int i) => Theme.S(96) + i * Theme.S(64);
        protected override void OnPaintBackground(PaintEventArgs e) => e.Graphics.Clear(BackColor);

        public override bool Step(double dt)
        {
            double target = IsOpen ? 1 : 0;
            bool moved = false;
            if (OpenT != target)
            {
                OpenT = target > OpenT ? Math.Min(1, OpenT + dt * 3.4) : Math.Max(0, OpenT - dt * 4.5);
                moved = true;
                Moved?.Invoke();
                if (OpenT == 0) Visible = false;
            }
            bool stagger = IsOpen && Environment.TickCount - openedAt < 300 + Items.Count * 60 + 400;
            return moved || stagger || base.Step(dt);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int h = -1;
            for (int i = 0; i < Items.Count; i++) if (e.Y >= ItemTop(i) - Theme.S(8) && e.Y < ItemTop(i) + Theme.S(52)) h = i;
            if (h != hot) { hot = h; Cursor = h >= 0 ? Cursors.Hand : Cursors.Default; Invalidate(); }
            base.OnMouseMove(e);
        }
        protected override void OnMouseLeave(EventArgs e) { hot = -1; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseClick(MouseEventArgs e)
        {
            if (hot >= 0) { Items[hot].Click?.Invoke(); Invalidate(); }
            base.OnMouseClick(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = Theme.Crisp(e.Graphics);
            Rule(g, Theme.Line, 0, 0, Math.Max(1, Theme.S(1)), Height);
            int pad = Theme.S(36);
            long el = Environment.TickCount - openedAt;
            var lf = Theme.Display(11);
            Theme.Tracked(g, "SETTINGS", Theme.Display(8.5f), pad, Theme.S(58), Theme.Faint, 0.14f);
            for (int i = 0; i < Items.Count; i++)
            {
                double a = IsOpen ? Theme.EaseOut((el - 140 - i * 60) / 380.0) : OpenT;
                if (a <= 0) continue;
                int x = pad + (int)(Theme.S(24) * (1 - a)), y = ItemTop(i);
                var it = Items[i];
                Color lc = Theme.Mix(BackColor, i == hot ? Theme.Text : Theme.Mix(Theme.Sub, Theme.Text, 0.5), a);
                Theme.Tracked(g, it.Label, lf, x, y, lc, 0.14f);
                string v = it.Value?.Invoke();
                if (!string.IsNullOrEmpty(v))
                    TextRenderer.DrawText(g, v, Theme.Mono(8.5f), new Rectangle(x, y + Theme.S(24), Width - x - pad, Theme.S(18)),
                        Theme.Mix(BackColor, Theme.Faint, a), TextFormatFlags.NoPadding | TextFormatFlags.PathEllipsis | TextFormatFlags.SingleLine);
                if (i == hot) Rule(g, Theme.Mix(BackColor, Theme.Text, a), x, y + Theme.S(48), Theme.S(24));
            }
            var foot = "POWERED BY YT-DLP";
            Theme.Tracked(g, foot, Theme.Display(7.5f), pad, Height - Theme.S(44), Theme.Mix(BackColor, Theme.Faint, OpenT), 0.14f);
        }
    }

    /// <summary>Flat dark context menu.</summary>
    sealed class DarkRenderer : ToolStripProfessionalRenderer
    {
        public DarkRenderer() : base(new DarkColors()) { RoundedEdges = false; }
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Theme.Text : Theme.Faint;
            base.OnRenderItemText(e);
        }
        sealed class DarkColors : ProfessionalColorTable
        {
            static readonly Color Menu = Color.FromArgb(14, 14, 14), Sel = Color.FromArgb(34, 34, 34);
            public override Color ToolStripDropDownBackground => Menu;
            public override Color ImageMarginGradientBegin => Menu;
            public override Color ImageMarginGradientMiddle => Menu;
            public override Color ImageMarginGradientEnd => Menu;
            public override Color MenuBorder => Theme.Line;
            public override Color MenuItemBorder => Sel;
            public override Color MenuItemSelected => Sel;
            public override Color SeparatorDark => Theme.Line;
            public override Color SeparatorLight => Menu;
        }
    }
}

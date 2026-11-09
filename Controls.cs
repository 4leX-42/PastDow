using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace VidGrab
{
    /// <summary>Base: doble buffer, fondo del tema, estado de raton.</summary>
    class Canvas : Control
    {
        public Canvas()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
                     ControlStyles.ResizeRedraw, true);
            BackColor = Theme.Bg;
        }
        protected bool hover;
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
        protected override void OnTextChanged(EventArgs e) { Invalidate(); base.OnTextChanged(e); }

        protected static void Underline(Graphics g, Color c, int x, int y, int w)
        {
            using (var b = new SolidBrush(c)) g.FillRectangle(b, x, y, w, Math.Max(1, Theme.S(1)));
        }
    }

    /// <summary>Accion como texto: subrayado al pasar el raton. Sin cajas.</summary>
    sealed class TextButton : Canvas
    {
        public Font TextFont = Theme.Strong(10.5f);
        public Color Color = Theme.Text;
        public TextButton(string text) { Text = text; Cursor = Cursors.Hand; }
        public int Measure() => Theme.TextW(Text, TextFont) + Theme.S(2);
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = Theme.Crisp(e.Graphics);
            var c = Enabled ? Color : Theme.Faint;
            int h = TextRenderer.MeasureText("Ag", TextFont, Size.Empty, TextFormatFlags.NoPadding).Height;
            int y = (Height - h) / 2;
            TextRenderer.DrawText(g, Text, TextFont, new Point(0, y), c, TextFormatFlags.NoPadding);
            if (hover && Enabled) Underline(g, c, 0, y + h + Theme.S(2), Theme.TextW(Text, TextFont));
        }
    }

    /// <summary>Eleccion entre palabras: la elegida en blanco y subrayada, el resto en gris.</summary>
    sealed class Choice : Canvas
    {
        public readonly string[] Items;
        int sel, hot = -1;
        public event EventHandler Changed;
        readonly Font font = Theme.Sans(10);
        public Choice(params string[] items) { Items = items; Cursor = Cursors.Hand; }
        public int Selected { get => sel; set { if (value != sel) { sel = value; Invalidate(); } } }

        int Gap => Theme.S(18);
        public int Measure()
        {
            int w = 0;
            foreach (var s in Items) w += Theme.TextW(s, font) + Gap;
            return w - Gap + Theme.S(2);
        }
        int[] Starts()
        {
            var x = new int[Items.Length + 1];
            for (int i = 0; i < Items.Length; i++) x[i + 1] = x[i] + Theme.TextW(Items[i], font) + Gap;
            return x;
        }
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
        protected override void OnMouseLeave(EventArgs e) { hot = -1; base.OnMouseLeave(e); }
        protected override void OnMouseClick(MouseEventArgs e)
        {
            int i = Hit(e.X);
            if (Enabled && i >= 0 && i != sel) { sel = i; Invalidate(); Changed?.Invoke(this, EventArgs.Empty); }
            base.OnMouseClick(e);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = Theme.Crisp(e.Graphics);
            var x = Starts();
            int h = TextRenderer.MeasureText("Ag", font, Size.Empty, TextFormatFlags.NoPadding).Height;
            int y = (Height - h) / 2;
            for (int i = 0; i < Items.Length; i++)
            {
                bool on = i == sel;
                Color c = !Enabled ? Theme.Faint : on || i == hot ? Theme.Text : Theme.Sub;
                TextRenderer.DrawText(g, Items[i], font, new Point(x[i], y), c, TextFormatFlags.NoPadding);
                if (on && Enabled) Underline(g, Theme.Text, x[i], y + h + Theme.S(3), Theme.TextW(Items[i], font));
            }
        }
    }

    /// <summary>Campo de enlace grande, sin caja: solo una linea fina debajo que se aclara al escribir.</summary>
    sealed class UrlBox : Canvas
    {
        public readonly TextBox Box = new TextBox();
        public UrlBox()
        {
            Box.BorderStyle = BorderStyle.None;
            Box.BackColor = Theme.Bg;
            Box.ForeColor = Theme.Text;
            Box.Font = Theme.Sans(14);
            Box.GotFocus += (s, e) => Invalidate();
            Box.LostFocus += (s, e) => Invalidate();
            Controls.Add(Box);
            Cursor = Cursors.IBeam;
        }
        protected override void OnClick(EventArgs e) { Box.Focus(); base.OnClick(e); }
        protected override void OnLayout(LayoutEventArgs e)
        {
            Box.SetBounds(0, Height - Box.PreferredHeight - Theme.S(10), Width, Box.PreferredHeight);
            base.OnLayout(e);
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(Theme.Bg);
            Underline(e.Graphics, Box.Focused ? Theme.Sub : Theme.Line, 0, Height - Math.Max(1, Theme.S(1)), Width);
        }
    }

    /// <summary>Minimizar / cerrar: solo el glifo, se aclara al pasar.</summary>
    sealed class GlyphButton : Canvas
    {
        readonly string glyph;
        public GlyphButton(string glyph) { this.glyph = glyph; }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = Theme.Crisp(e.Graphics);
            TextRenderer.DrawText(g, glyph, Theme.Icon(7.5f), ClientRectangle, hover ? Theme.Text : Theme.Faint,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }

    /// <summary>Texto clicable del pie.</summary>
    sealed class TextLink : Canvas
    {
        public bool Mono;
        public TextLink(string text) { Text = text; Cursor = Cursors.Hand; }
        Font Font_ => Mono ? Theme.Num(8.75f) : Theme.Sans(9);
        public int Measure() => Theme.TextW(Text, Font_) + Theme.S(2);
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = Theme.Crisp(e.Graphics);
            TextRenderer.DrawText(g, Text, Font_, ClientRectangle, hover ? Theme.Text : Theme.Sub,
                TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.PathEllipsis | TextFormatFlags.SingleLine);
        }
    }

    /// <summary>Lista de descargas: filas separadas por una linea fina que hace de barra de progreso.</summary>
    sealed class QueueView : Canvas
    {
        public readonly List<Job> Jobs = new List<Job>();
        public event Action<Job, MouseEventArgs> ItemClick;
        public event Action<Job> ItemDoubleClick;
        public string[] Empty = new string[0];
        int scroll, hotIndex = -1;
        int RowH => Theme.S(64);

        int MaxScroll => Math.Max(0, Jobs.Count * RowH - Height);
        public void Clamp() { scroll = Math.Max(0, Math.Min(scroll, MaxScroll)); }

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
        protected override void OnMouseLeave(EventArgs e) { hotIndex = -1; base.OnMouseLeave(e); }
        protected override void OnMouseClick(MouseEventArgs e) { var j = At(e.Location); if (j != null) ItemClick?.Invoke(j, e); base.OnMouseClick(e); }
        protected override void OnMouseDoubleClick(MouseEventArgs e) { var j = At(e.Location); if (j != null) ItemDoubleClick?.Invoke(j); base.OnMouseDoubleClick(e); }

        static readonly System.Globalization.CultureInfo Es = new System.Globalization.CultureInfo("es-ES");

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = Theme.Crisp(e.Graphics);
            g.Clear(Theme.Bg);
            if (Jobs.Count == 0)
            {
                int ey = Theme.S(4);
                foreach (var line in Empty)
                {
                    TextRenderer.DrawText(g, line, Theme.Sans(9.5f), new Point(0, ey), Theme.Faint, TextFormatFlags.NoPadding);
                    ey += Theme.S(22);
                }
                return;
            }
            Clamp();
            int h = RowH, hair = Math.Max(1, Theme.S(1)), padX = Theme.S(10);
            for (int i = scroll / h; i < Jobs.Count; i++)
            {
                int y = i * h - scroll;
                if (y > Height) break;
                var j = Jobs[i];
                if (i == hotIndex) using (var b = new SolidBrush(Theme.Hover)) g.FillRectangle(b, 0, y, Width, h - hair);

                string st; Color sc;
                switch (j.State)
                {
                    case JobState.Running: st = j.Percent.ToString("0", Es) + "%"; sc = Theme.Text; break;
                    case JobState.Processing: st = "uniendo"; sc = Theme.Sub; break;
                    case JobState.Done: st = "hecho"; sc = Theme.Sub; break;
                    case JobState.Skipped: st = "ya estaba"; sc = Theme.Faint; break;
                    case JobState.Failed: st = "error"; sc = Theme.Red; break;
                    case JobState.Canceled: st = "cancelado"; sc = Theme.Faint; break;
                    default: st = "en cola"; sc = Theme.Faint; break;
                }
                var stFont = Theme.Num(10);
                int stW = Theme.TextW(st, stFont);
                TextRenderer.DrawText(g, st, stFont, new Point(Width - padX - stW, y + Theme.S(14)), sc, TextFormatFlags.NoPadding);

                bool dim = j.State == JobState.Skipped || j.State == JobState.Canceled || j.State == JobState.Queued;
                TextRenderer.DrawText(g, j.Title ?? j.Url, Theme.Sans(10.5f),
                    new Rectangle(padX, y + Theme.S(12), Width - padX * 3 - stW, Theme.S(22)), dim ? Theme.Sub : Theme.Text,
                    TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
                TextRenderer.DrawText(g, j.Detail, Theme.Num(8.75f),
                    new Rectangle(padX, y + Theme.S(36), Width - padX * 2, Theme.S(18)), j.State == JobState.Failed ? Theme.Red : Theme.Faint,
                    TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);

                // la linea separadora es tambien la barra de progreso
                int ly = y + h - hair;
                using (var b = new SolidBrush(Theme.Line)) g.FillRectangle(b, 0, ly, Width, hair);
                if (j.State == JobState.Running || j.State == JobState.Processing)
                {
                    double frac = j.State == JobState.Processing ? 1 : j.Percent / 100.0;
                    using (var b = new SolidBrush(Theme.Text)) g.FillRectangle(b, 0, ly - hair, (int)(Width * frac), hair * 2);
                }
            }
            if (MaxScroll > 0)
            {
                int total = Jobs.Count * RowH, th = Math.Max(Theme.S(24), Height * Height / total);
                int ty = (int)((long)(Height - th) * scroll / MaxScroll);
                using (var b = new SolidBrush(Theme.Line)) g.FillRectangle(b, Width - Theme.S(2), ty, Theme.S(2), th);
            }
        }
    }

    /// <summary>Menu contextual oscuro y plano.</summary>
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
            static readonly Color Menu = Color.FromArgb(24, 24, 24), Sel = Color.FromArgb(38, 38, 38);
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

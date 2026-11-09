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

    /// <summary>Glyph button (close).</summary>
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
}

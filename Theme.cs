using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace PastDow
{
    /// <summary>
    /// Black, off-white and one grey. Display type is Bahnschrift (DIN, ships with Windows 10/11), uppercase and
    /// letter-spaced; hierarchy comes from size, weight and space, not colour. Muted red only for errors.
    /// </summary>
    static class Theme
    {
        public static readonly Color Bg = Color.FromArgb(0, 0, 0);
        public static readonly Color Hover = Color.FromArgb(14, 14, 14);
        public static readonly Color Line = Color.FromArgb(40, 40, 40);
        public static readonly Color Text = Color.FromArgb(240, 240, 238);
        public static readonly Color Sub = Color.FromArgb(184, 184, 180);
        public static readonly Color Faint = Color.FromArgb(120, 120, 118);
        public static readonly Color Red = Color.FromArgb(232, 108, 100);

        /// <summary>Single UI scale: DPI / 96, reduced so the window fits. PASTDOW_SCALE forces it.</summary>
        public static readonly float Scale = InitScale();
        public static int S(float px) => (int)Math.Round(px * Scale);

        static float InitScale()
        {
            if (float.TryParse(Environment.GetEnvironmentVariable("PASTDOW_SCALE"), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var forced) && forced >= 0.5f && forced <= 4f)
                return forced;
            float dpi = 96f;
            try { using (var g = Graphics.FromHwnd(IntPtr.Zero)) dpi = g.DpiX; } catch { }
            var area = Screen.PrimaryScreen.WorkingArea;
            float fit = Math.Min(area.Width / 820f, area.Height / 700f);
            return Math.Max(0.75f, Math.Min(dpi / 96f, fit));
        }

        static readonly Dictionary<(string, float), Font> fonts = new Dictionary<(string, float), Font>();
        static Font F(string family, float pt)
        {
            var key = (family, pt);
            lock (fonts)
            {
                if (!fonts.TryGetValue(key, out var f))
                    fonts[key] = f = new Font(family, Math.Max(pt, 8.5f) * 96f / 72f * Scale, FontStyle.Regular, GraphicsUnit.Pixel);
                return f;
            }
        }

        static string Pick(params string[] names)
        {
            foreach (var n in names)
                try { using (new FontFamily(n)) return n; } catch (ArgumentException) { }
            return names[names.Length - 1];
        }

        static readonly string DisplayFamily = Pick("Bahnschrift", "Segoe UI");
        static readonly string DisplayLightFamily = Pick("Bahnschrift Light", "Bahnschrift", "Segoe UI Light");
        static readonly string DisplayBoldFamily = Pick("Bahnschrift SemiBold", "Bahnschrift", "Segoe UI Semibold");
        static readonly string IconFamily = Pick("Segoe Fluent Icons", "Segoe MDL2 Assets");
        static readonly string MonoFamily = Pick("Cascadia Mono", "Consolas");

        public static Font Display(float size) => F(DisplayFamily, size);
        public static Font Light(float size) => F(DisplayLightFamily, size);
        public static Font Bold(float size) => F(DisplayBoldFamily, size);
        public static Font Sans(float size) => F("Segoe UI", size);
        public static Font Mono(float size) => F(MonoFamily, size);
        public static Font Icon(float size) => F(IconFamily, size);

        public static Graphics Crisp(Graphics g) { g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit; return g; }

        public static int TextW(string s, Font f) => TextRenderer.MeasureText(s, f, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width;
        public static int TextH(Font f) => TextRenderer.MeasureText("AG", f, Size.Empty, TextFormatFlags.NoPadding).Height;

        // ---- letter-spaced uppercase (GDI has no tracking: draw glyph by glyph with cached widths) ----
        // typographic advance widths: TextRenderer pads single glyphs, which made the tracking look too loose
        static readonly Graphics Measurer = Graphics.FromImage(new Bitmap(1, 1));
        static readonly StringFormat Typo = new StringFormat(StringFormat.GenericTypographic) { FormatFlags = StringFormatFlags.MeasureTrailingSpaces };
        static readonly Dictionary<(Font, char), int> widths = new Dictionary<(Font, char), int>();
        static int CharW(Font f, char c)
        {
            if (!widths.TryGetValue((f, c), out var w))
                widths[(f, c)] = w = (int)Math.Round(Measurer.MeasureString(c.ToString(), f, PointF.Empty, Typo).Width);
            return w;
        }
        public static int TrackedW(string s, Font f, float em = 0.14f)
        {
            int t = (int)(f.Size * em), w = 0;
            foreach (char c in s) w += CharW(f, c) + t;
            return Math.Max(0, w - t);
        }
        public static void Tracked(Graphics g, string s, Font f, int x, int y, Color c, float em = 0.14f)
        {
            int t = (int)(f.Size * em);
            foreach (char ch in s)
            {
                if (ch != ' ') TextRenderer.DrawText(g, ch.ToString(), f, new Point(x, y), c, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
                x += CharW(f, ch) + t;
            }
        }

        public static Color Mix(Color a, Color b, double t)
        {
            t = Math.Max(0, Math.Min(1, t));
            return Color.FromArgb((int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }

        public static double EaseOut(double t) { t = Math.Max(0, Math.Min(1, t)); return 1 - Math.Pow(1 - t, 3); }

        /// <summary>
        /// Window background: black with a low horizon glow along the bottom edge (x.ai) and fine grain so the
        /// gradient does not band. Built once per size; every control paints its slice of it.
        /// </summary>
        public static Bitmap Backdrop(Size size, float glow = 1f)
        {
            var bmp = new Bitmap(Math.Max(1, size.Width), Math.Max(1, size.Height));
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Bg);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                int w = size.Width, h = size.Height;
                var ell = new RectangleF(-w * 0.25f, h * 0.78f, w * 1.5f, h * 0.7f);
                using (var path = new GraphicsPath())
                {
                    path.AddEllipse(ell);
                    using (var pb = new PathGradientBrush(path))
                    {
                        pb.CenterPoint = new PointF(w * 0.5f, h * 1.02f);
                        pb.CenterColor = Color.FromArgb((int)(70 * glow), 160, 175, 200);
                        pb.SurroundColors = new[] { Color.FromArgb(0, 0, 0, 0) };
                        g.FillPath(pb, path);
                    }
                }
                // thin bright horizon line fading at both ends
                using (var lb = new LinearGradientBrush(new Rectangle(0, 0, w, 2), Color.Transparent, Color.Transparent, 0f))
                {
                    var cb = new ColorBlend
                    {
                        Colors = new[] { Color.FromArgb(0, 255, 255, 255), Color.FromArgb((int)(90 * glow), 220, 228, 240), Color.FromArgb(0, 255, 255, 255) },
                        Positions = new[] { 0f, 0.5f, 1f }
                    };
                    lb.InterpolationColors = cb;
                    g.FillRectangle(lb, 0, h - S(1), w, S(1));
                }
            }
            // grain
            var rnd = new Random(7);
            var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), System.Drawing.Imaging.ImageLockMode.ReadWrite, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            var px = new byte[data.Stride * data.Height];
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, px, 0, px.Length);
            for (int i = 0; i < px.Length; i += 4)
            {
                int n = rnd.Next(-3, 4);
                for (int k = 0; k < 3; k++) px[i + k] = (byte)Math.Max(0, Math.Min(255, px[i + k] + n * (px[i + k] > 4 ? 1 : 0)));
            }
            System.Runtime.InteropServices.Marshal.Copy(px, 0, data.Scan0, px.Length);
            bmp.UnlockBits(data);
            return bmp;
        }
    }
}

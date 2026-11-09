using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Windows.Forms;

namespace VidGrab
{
    /// <summary>
    /// Monocromo: casi negro, blanco roto y un gris para lo secundario. Sin acento de color; la jerarquia la
    /// dan el tamaño, el peso y el espacio. Rojo apagado solo para errores.
    /// </summary>
    static class Theme
    {
        public static readonly Color Bg = Color.FromArgb(14, 14, 14);
        public static readonly Color Hover = Color.FromArgb(22, 22, 22);
        public static readonly Color Line = Color.FromArgb(40, 40, 40);
        public static readonly Color Text = Color.FromArgb(236, 236, 234);
        public static readonly Color Sub = Color.FromArgb(176, 176, 172);
        public static readonly Color Faint = Color.FromArgb(110, 110, 108);
        public static readonly Color Red = Color.FromArgb(226, 104, 98);

        /// <summary>Escala unica: DPI / 96, limitada para que la ventana quepa. VIDGRAB_SCALE la fuerza.</summary>
        public static readonly float Scale = InitScale();
        public static int S(float px) => (int)Math.Round(px * Scale);

        static float InitScale()
        {
            if (float.TryParse(Environment.GetEnvironmentVariable("VIDGRAB_SCALE"), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var forced) && forced >= 0.5f && forced <= 4f)
                return forced;
            float dpi = 96f;
            try { using (var g = Graphics.FromHwnd(IntPtr.Zero)) dpi = g.DpiX; } catch { }
            var area = Screen.PrimaryScreen.WorkingArea;
            float fit = Math.Min(area.Width / 800f, area.Height / 680f);
            return Math.Max(0.75f, Math.Min(dpi / 96f, fit));
        }

        static readonly Dictionary<(string, float, FontStyle), Font> fonts = new Dictionary<(string, float, FontStyle), Font>();
        static Font F(string family, float pt, FontStyle style = FontStyle.Regular)
        {
            var key = (family, pt, style);
            lock (fonts)
            {
                if (!fonts.TryGetValue(key, out var f))
                    fonts[key] = f = new Font(family, Math.Max(pt, 8.75f) * 96f / 72f * Scale, style, GraphicsUnit.Pixel);
                return f;
            }
        }

        static string Pick(params string[] names)
        {
            foreach (var n in names)
                try { using (new FontFamily(n)) return n; } catch (ArgumentException) { }
            return names[names.Length - 1];
        }

        static readonly string StrongFamily = Pick("Segoe UI Semibold", "Segoe UI");
        static readonly string IconFamily = Pick("Segoe Fluent Icons", "Segoe MDL2 Assets");

        public static Font Sans(float size) => F("Segoe UI", size);
        public static Font Strong(float size) => F(StrongFamily, size);
        public static Font Mono(float size) => F("Consolas", size);
        public static Font Icon(float size) => F(IconFamily, size);

        public static Graphics Crisp(Graphics g) { g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit; return g; }

        public static readonly string MonoFamily = Pick("Cascadia Mono", "Consolas");
        public static Font Num(float size) => F(MonoFamily, size);

        public static int TextW(string s, Font f) => TextRenderer.MeasureText(s, f, Size.Empty, TextFormatFlags.NoPadding).Width;
    }
}

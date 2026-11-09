using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace PastDow
{
    /// <summary>
    /// Small loading card shown while WebView2 starts (instant on later runs, a few seconds on the first one):
    /// the wordmark and a light running along a hairline. Closed as soon as the real window is revealed.
    /// </summary>
    sealed class Splash : Form
    {
        readonly Timer frame = new Timer { Interval = 16 };
        readonly Stopwatch clock = Stopwatch.StartNew();

        public Splash(Rectangle over)
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.Black;
            DoubleBuffered = true;
            Size = new Size(Theme.S(340), Theme.S(120));
            Location = new Point(over.Left + (over.Width - Width) / 2, over.Top + (over.Height - Height) / 2);
            Opacity = 0;
            frame.Tick += (s, e) =>
            {
                double t = clock.Elapsed.TotalSeconds;
                if (Opacity < 1 && t > 0.15) Opacity = Math.Min(1, (t - 0.15) / 0.25);   // no flash if WebView2 is quick
                Invalidate();
            };
            frame.Start();
        }

        protected override bool ShowWithoutActivation => true;

        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int v, int size);
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int round = 2; DwmSetWindowAttribute(Handle, 33, ref round, 4);
            int border = ColorTranslator.ToWin32(Color.FromArgb(28, 28, 28)); DwmSetWindowAttribute(Handle, 34, ref border, 4);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = Theme.Crisp(e.Graphics);
            var f = Theme.Display(13);
            int tw = Theme.TrackedW("PAST&DOW", f, 0.42f);
            Theme.Tracked(g, "PAST&DOW", f, (Width - tw) / 2, Theme.S(38), Theme.Text, 0.42f);
            int lw = Theme.S(150), lx = (Width - lw) / 2, ly = Theme.S(76), hair = Math.Max(1, Theme.S(1));
            using (var b = new SolidBrush(Theme.Line)) g.FillRectangle(b, lx, ly, lw, hair);
            double ph = (clock.Elapsed.TotalSeconds / 1.1) % 1.0;
            int seg = lw / 3, x = lx + (int)((lw + seg) * Theme.EaseOut(ph)) - seg;
            int x0 = Math.Max(lx, x), x1 = Math.Min(lx + lw, x + seg);
            if (x1 > x0)
                using (var b = new LinearGradientBrush(new Rectangle(x - 1, ly, seg + 2, 2), Color.FromArgb(0, 255, 255, 255), Color.White, 0f))
                { b.WrapMode = WrapMode.TileFlipXY; g.FillRectangle(b, x0, ly, x1 - x0, hair); }
        }

        protected override void Dispose(bool disposing) { if (disposing) frame.Dispose(); base.Dispose(disposing); }
    }
}

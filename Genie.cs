using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows.Automation;
using System.Windows.Forms;

namespace Ulvark
{
    /// <summary>
    /// macOS-style genie: the window narrows into its taskbar button when minimised and pours back out when
    /// opened or restored. Drawn on a click-through layered window (UpdateLayeredWindow) above everything, row by
    /// row from a snapshot of the real window; Windows' own minimise animation is switched off.
    /// </summary>
    static class Genie
    {
        // ULVARK_GENIE_SLOW=1 plays it 6x slower (to check the frames)
        static readonly double Duration = Environment.GetEnvironmentVariable("ULVARK_GENIE_SLOW") == "1" ? 2.2 : 0.36;
        static Point? target;

        /// <summary>Taskbar button of this app (UI Automation), else the middle of the taskbar.</summary>
        public static Task LocateTarget(Form f)
        {
            var fallback = Fallback(f);
            if (target == null) target = fallback;
            return Task.Run(() =>
            {
                try
                {
                    var tray = FindWindow("Shell_TrayWnd", null);
                    if (tray == IntPtr.Zero) return;
                    var root = AutomationElement.FromHandle(tray);
                    var buttons = root.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
                    foreach (AutomationElement b in buttons)
                    {
                        string n = b.Current.Name ?? "";
                        if (!n.StartsWith(AppForm.AppName, StringComparison.OrdinalIgnoreCase)) continue;
                        var r = b.Current.BoundingRectangle;
                        if (r.Width > 0) { target = new Point((int)(r.X + r.Width / 2), (int)(r.Y + r.Height / 2)); return; }
                    }
                }
                catch { }
            });
        }

        static Point Fallback(Form f)
        {
            var s = Screen.FromControl(f);
            var b = s.Bounds; var w = s.WorkingArea;
            if (w.Bottom < b.Bottom) return new Point(b.Left + b.Width / 2, (w.Bottom + b.Bottom) / 2);
            if (w.Top > b.Top) return new Point(b.Left + b.Width / 2, (b.Top + w.Top) / 2);
            return new Point(b.Left + b.Width / 2, b.Bottom - 4);
        }

        public static void DisableSystemAnimation(Form f)
        {
            int on = 1;
            DwmSetWindowAttribute(f.Handle, 3, ref on, 4); // DWMWA_TRANSITIONS_FORCEDISABLED
        }

        public static Bitmap Snapshot(Form f)
        {
            var bmp = new Bitmap(f.Width, f.Height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                IntPtr dc = g.GetHdc();
                try { PrintWindow(f.Handle, dc, 2); } finally { g.ReleaseHdc(dc); }
            }
            // GDI leaves alpha at 0: make every pixel opaque before cutting the corners
            var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
            var px = new byte[data.Stride * data.Height];
            Marshal.Copy(data.Scan0, px, 0, px.Length);
            for (int i = 3; i < px.Length; i += 4) px[i] = 255;
            Marshal.Copy(px, 0, data.Scan0, px.Length);
            bmp.UnlockBits(data);
            RoundCorners(bmp, Theme.S(8));
            return bmp;
        }

        /// <summary>
        /// Plays the effect. closing = into the button; otherwise out of it.
        /// One reusable DIB section, rows scaled by hand in a managed buffer, and every frame paced by DwmFlush
        /// so it runs at the monitor's refresh rate (60/120/144 Hz) instead of a sleep.
        /// </summary>
        /// handoff swaps the real window for the overlay with no gap: when closing it runs once the overlay already
        /// shows the first frame (hide the window then); when opening it runs on the last frame (show the window).
        public static void Play(Bitmap snap, Rectangle win, bool closing, Action handoff)
        {
            bool handed = false;
            // an animation must never take the app down: on any failure the window just minimises/appears
            try { PlayCore(snap, win, closing, () => { handed = true; handoff(); }); }
            catch { }
            if (!handed) handoff();
        }

        static void PlayCore(Bitmap snap, Rectangle win, bool closing, Action handoff)
        {
            var t = target ?? new Point(win.Left + win.Width / 2, Screen.PrimaryScreen.Bounds.Bottom);
            int pad = Theme.S(30);
            var area = Rectangle.Union(win, new Rectangle(t.X - pad, t.Y - pad, pad * 2, pad * 2));
            int aw = area.Width, ah = area.Height, sw = snap.Width, sh = snap.Height;

            var src = new int[sw * sh];
            var sd = snap.LockBits(new Rectangle(0, 0, sw, sh), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            for (int y = 0; y < sh; y++) Marshal.Copy(sd.Scan0 + y * sd.Stride, src, y * sw, sw);
            snap.UnlockBits(sd);
            var dst = new int[aw * ah];

            IntPtr screen = GetDC(IntPtr.Zero), mem = CreateCompatibleDC(screen), bits;
            var bmi = new BITMAPINFOHEADER { biSize = 40, biWidth = aw, biHeight = -ah, biPlanes = 1, biBitCount = 32 };
            IntPtr dib = CreateDIBSection(screen, ref bmi, 0, out bits, IntPtr.Zero, 0);
            IntPtr old = SelectObject(mem, dib);
            try
            {
                using (var ov = new Overlay(area))
                {
                    void Frame(double k)
                    {
                        Array.Clear(dst, 0, dst.Length);
                        Render(src, sw, sh, dst, aw, ah, win, t, area.Location, k);
                        Marshal.Copy(dst, 0, bits, dst.Length);
                        ov.Push(screen, mem, aw, ah, (byte)(255 * (1 - Math.Pow(k, 8))));
                    }
                    var h = ov.Handle;                 // create the window without showing it
                    Frame(closing ? 0 : 1);            // first frame is ready before anything becomes visible
                    ov.Show();
                    DwmFlush();
                    if (closing) { handoff(); DwmFlush(); }   // overlay is on screen: now hide the real window
                    var clock = Stopwatch.StartNew();
                    while (true)
                    {
                        double p = Math.Min(1, clock.Elapsed.TotalSeconds / Duration);
                        Frame(closing ? p : 1 - p);
                        if (p >= 1) break;
                        DwmFlush();   // wait for the next vblank
                    }
                    if (!closing) { handoff(); DwmFlush(); DwmFlush(); }   // real window composed before the overlay goes
                }
            }
            finally
            {
                SelectObject(mem, old); DeleteObject(dib); DeleteDC(mem); ReleaseDC(IntPtr.Zero, screen);
            }
        }

        /// <summary>
        /// k = 0: the window as it is; k = 1: gone into the button.
        /// The edge nearest the button pinches in along an S-curve while the body slides towards it, as on macOS.
        /// </summary>
        static void Render(int[] src, int sw, int sh, int[] dst, int aw, int ah, Rectangle win, Point t, Point o, double k)
        {
            double pinch = Ease(Math.Min(1, k / 0.5));
            double slide = Ease(Math.Max(0, (k - 0.18) / 0.82));
            bool down = t.Y >= win.Top + win.Height / 2;
            double top, bottom;
            if (down) { top = Lerp(win.Top, t.Y, slide); bottom = Lerp(win.Bottom, t.Y, Math.Max(slide, pinch * 0.12)); }
            else { top = Lerp(win.Top, t.Y, Math.Max(slide, pinch * 0.12)); bottom = Lerp(win.Bottom, t.Y, slide); }
            double span = bottom - top;
            if (span < 1) return;
            double tipHalf = Theme.S(16);
            double reach = down ? Math.Max(1, t.Y - win.Top) : Math.Max(1, win.Bottom - t.Y);
            int y0 = Math.Max(0, (int)Math.Floor(top) - o.Y), y1 = Math.Min(ah, (int)Math.Ceiling(bottom) - o.Y);
            for (int dy = y0; dy < y1; dy++)
            {
                double y = dy + o.Y + 0.5;
                double v = (y - top) / span;
                if (v < 0 || v >= 1) continue;
                double d = down ? (y - win.Top) / reach : (win.Bottom - y) / reach;
                double c = Smooth(Math.Max(0, Math.Min(1, d))) * pinch;
                double l = Lerp(win.Left, t.X - tipHalf, c) - o.X, r = Lerp(win.Right, t.X + tipHalf, c) - o.X;
                int li = Math.Max(0, (int)l), ri = Math.Min(aw, (int)Math.Ceiling(r));
                if (ri <= li) continue;
                int srow = (int)(v * sh) * sw, drow = dy * aw;
                // 16.16 fixed-point step through the source row
                long step = (long)(sw * 65536.0 / Math.Max(1, r - l)), acc = (long)((li - l) * step);
                for (int x = li; x < ri; x++, acc += step)
                {
                    // li is l rounded down, so the first sample can land just left of the source: clamp both ends
                    int sx = (int)(acc >> 16);
                    if (sx < 0) sx = 0; else if (sx >= sw) sx = sw - 1;
                    dst[drow + x] = src[srow + sx];
                }
            }
        }

        static double Lerp(double a, double b, double t) => a + (b - a) * t;
        static double Smooth(double x) => x * x * (3 - 2 * x);
        static double Ease(double x) => x < 0.5 ? 4 * x * x * x : 1 - Math.Pow(-2 * x + 2, 3) / 2;

        public static void Round(Bitmap b) => RoundCorners(b, Theme.S(8));

        static void RoundCorners(Bitmap b, int r)
        {
            for (int y = 0; y < r; y++)
                for (int x = 0; x < r; x++)
                {
                    double dx = r - x - 0.5, dy = r - y - 0.5;
                    if (dx * dx + dy * dy <= r * r) continue;
                    b.SetPixel(x, y, Color.Transparent); b.SetPixel(b.Width - 1 - x, y, Color.Transparent);
                    b.SetPixel(x, b.Height - 1 - y, Color.Transparent); b.SetPixel(b.Width - 1 - x, b.Height - 1 - y, Color.Transparent);
                }
        }

        /// <summary>Click-through, non-activating, topmost layered window.</summary>
        sealed class Overlay : Form
        {
            readonly Rectangle area;
            public Overlay(Rectangle area)
            {
                this.area = area;
                FormBorderStyle = FormBorderStyle.None;
                ShowInTaskbar = false;
                StartPosition = FormStartPosition.Manual;
                Bounds = area;
                TopMost = true;
            }
            protected override bool ShowWithoutActivation => true;
            protected override CreateParams CreateParams
            {
                get
                {
                    var cp = base.CreateParams;
                    cp.ExStyle |= 0x80000 | 0x20 | 0x80 | 0x08000000 | 0x8; // LAYERED | TRANSPARENT | TOOLWINDOW | NOACTIVATE | TOPMOST
                    return cp;
                }
            }
            public void Push(IntPtr screen, IntPtr mem, int w, int h, byte alpha)
            {
                var size = new SIZE { cx = w, cy = h };
                var src = new POINT();
                var pos = new POINT { x = area.Left, y = area.Top };
                var blend = new BLENDFUNCTION { BlendOp = 0, SourceConstantAlpha = alpha, AlphaFormat = 1 };
                UpdateLayeredWindow(Handle, screen, ref pos, ref size, mem, ref src, 0, ref blend, 2);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        struct BITMAPINFOHEADER
        {
            public int biSize, biWidth, biHeight; public short biPlanes, biBitCount;
            public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
        }
        [DllImport("gdi32.dll")] static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER bmi, uint usage, out IntPtr bits, IntPtr section, uint offset);
        [DllImport("dwmapi.dll")] static extern int DwmFlush();

        [StructLayout(LayoutKind.Sequential)] struct POINT { public int x, y; }
        [StructLayout(LayoutKind.Sequential)] struct SIZE { public int cx, cy; }
        [StructLayout(LayoutKind.Sequential, Pack = 1)] struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
        [DllImport("user32.dll")] static extern bool UpdateLayeredWindow(IntPtr h, IntPtr dst, ref POINT pos, ref SIZE size, IntPtr src, ref POINT srcPos, int key, ref BLENDFUNCTION blend, int flags);
        [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
        [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern bool DeleteDC(IntPtr dc);
        [DllImport("gdi32.dll")] static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
        [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr o);
        [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr FindWindow(string cls, string name);
        [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr h, int attr, ref int v, int size);
    }
}

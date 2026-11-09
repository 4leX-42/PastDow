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
        static readonly double Duration = Environment.GetEnvironmentVariable("ULVARK_GENIE_SLOW") == "1" ? 2.9 : 0.48;
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
                        if (!n.StartsWith(MainForm.AppName, StringComparison.OrdinalIgnoreCase)) continue;
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

        /// <summary>Plays the effect. closing = into the button; otherwise out of it. Blocks ~0.5 s with a message pump.</summary>
        public static void Play(Bitmap snap, Rectangle win, bool closing)
        {
            var t = target ?? new Point(win.Left + win.Width / 2, Screen.PrimaryScreen.Bounds.Bottom);
            int pad = Theme.S(30);
            var area = Rectangle.Union(win, new Rectangle(t.X - pad, t.Y - pad, pad * 2, pad * 2));
            using (var ov = new Overlay(area))
            using (var frame = new Bitmap(area.Width, area.Height, PixelFormat.Format32bppPArgb))
            using (var g = Graphics.FromImage(frame))
            {
                g.InterpolationMode = InterpolationMode.Bilinear;
                g.PixelOffsetMode = PixelOffsetMode.Half;
                g.CompositingMode = CompositingMode.SourceOver;
                var sw = Stopwatch.StartNew();
                ov.Show();
                while (true)
                {
                    double p = Math.Min(1, sw.Elapsed.TotalSeconds / Duration);
                    double k = closing ? p : 1 - p;
                    g.Clear(Color.Transparent);
                    Draw(g, snap, win, t, area.Location, k);
                    ov.Push(frame, (byte)(255 * (1 - Math.Pow(k, 6))));
                    Application.DoEvents();
                    if (p >= 1) break;
                    System.Threading.Thread.Sleep(5);
                }
            }
        }

        /// <summary>
        /// k = 0: the window as it is; k = 1: a point on the taskbar button.
        /// First the bottom edge pinches towards the button (sides curve in), then the body slides down into it.
        /// </summary>
        static void Draw(Graphics g, Bitmap snap, Rectangle win, Point t, Point origin, double k)
        {
            double pinch = Ease(Math.Min(1, k / 0.45));
            double slide = Ease(Math.Max(0, (k - 0.25) / 0.75));
            bool down = t.Y >= win.Top + win.Height / 2;
            double top = Lerp(win.Top, t.Y, down ? slide : 0), bottom = Lerp(win.Bottom, t.Y, down ? Math.Max(slide, pinch * 0.15) : slide);
            if (!down) { top = Lerp(win.Top, t.Y, Math.Max(slide, pinch * 0.15)); bottom = Lerp(win.Bottom, t.Y, slide); }
            double span = Math.Max(1, bottom - top);
            int rows = (int)Math.Ceiling(span);
            double tipHalf = Theme.S(18);
            for (int i = 0; i < rows; i++)
            {
                double y = top + i;
                double v = (y - top) / span;                         // 0..1 down the squeezed body
                // how far this row is towards the button, 0 at the far edge of the window, 1 at the button
                double d = down ? (y - win.Top) / Math.Max(1, t.Y - win.Top) : (win.Bottom - y) / Math.Max(1, win.Bottom - t.Y);
                double c = Smooth(Math.Max(0, Math.Min(1, d))) * pinch;
                double l = Lerp(win.Left, t.X - tipHalf, c), r = Lerp(win.Right, t.X + tipHalf, c);
                int sy = (int)(v * snap.Height);
                if (sy >= snap.Height) sy = snap.Height - 1;
                var dst = new RectangleF((float)(l - origin.X), (float)(y - origin.Y), (float)Math.Max(1, r - l), 1.4f);
                g.DrawImage(snap, dst, new RectangleF(0, sy, snap.Width, 1), GraphicsUnit.Pixel);
            }
        }

        static double Lerp(double a, double b, double t) => a + (b - a) * t;
        static double Smooth(double x) => x * x * (3 - 2 * x);
        static double Ease(double x) => x < 0.5 ? 4 * x * x * x : 1 - Math.Pow(-2 * x + 2, 3) / 2;

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

        /// <summary>Click-through, non-activating, topmost layered window that shows premultiplied frames.</summary>
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
            public void Push(Bitmap frame, byte alpha)
            {
                IntPtr screen = GetDC(IntPtr.Zero), mem = CreateCompatibleDC(screen), hbmp = IntPtr.Zero, old = IntPtr.Zero;
                try
                {
                    hbmp = frame.GetHbitmap(Color.FromArgb(0));
                    old = SelectObject(mem, hbmp);
                    var size = new SIZE { cx = frame.Width, cy = frame.Height };
                    var src = new POINT();
                    var pos = new POINT { x = area.Left, y = area.Top };
                    var blend = new BLENDFUNCTION { BlendOp = 0, SourceConstantAlpha = alpha, AlphaFormat = 1 };
                    UpdateLayeredWindow(Handle, screen, ref pos, ref size, mem, ref src, 0, ref blend, 2);
                }
                finally
                {
                    if (old != IntPtr.Zero) SelectObject(mem, old);
                    if (hbmp != IntPtr.Zero) DeleteObject(hbmp);
                    DeleteDC(mem); ReleaseDC(IntPtr.Zero, screen);
                }
            }
        }

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

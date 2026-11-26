using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Automation;

namespace PastDow
{
    /// <summary>
    /// Does the clicks a browser requires to load an unpacked extension, with Windows UI Automation, in the
    /// browser the user picked: opens a window, types the extensions page in the address bar (browsers refuse
    /// chrome:// pages on the command line), turns on Developer mode, presses Load unpacked, writes the folder in
    /// the folder dialog and accepts it, then checks that the extension shows in the list. Chromium's extensions
    /// page keeps the same element ids in Chrome, Brave and Edge (devMode, loadUnpacked). Any step that does not
    /// happen in time returns false and the caller falls back to the guided steps.
    /// </summary>
    static class ExtensionAutoInstall
    {
        const string ExtensionName = "PastDow Browser Capture";

        public static bool Run(ExtensionSetup.Browser b, string folder, Action<string> step)
        {
            try
            {
                string procName = Path.GetFileNameWithoutExtension(b.Exe);
                var before = Windows(procName).ToList();
                Process.Start(new ProcessStartInfo(b.Exe, (b.Args + " --new-window about:blank").Trim()) { UseShellExecute = false });

                step("Opening " + b.Name);
                IntPtr hwnd = Until(15000, () => Windows(procName).FirstOrDefault(h => !before.Contains(h) && Title(h).Length > 0));
                if (hwnd == IntPtr.Zero) hwnd = Until(3000, () => { var f = GetForegroundWindow(); return IsOf(f, procName) ? f : IntPtr.Zero; });
                if (hwnd == IntPtr.Zero) return false;
                var win = AutomationElement.FromHandle(hwnd);

                // 1. address bar: the first edit box of the browser frame
                var omni = Until(8000, () => win.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit)));
                if (omni == null || !(omni.TryGetCurrentPattern(ValuePattern.Pattern, out object vp))) return false;
                SetForegroundWindow(hwnd);
                omni.SetFocus();
                ((ValuePattern)vp).SetValue(b.Page);
                Thread.Sleep(250);
                // Enter as window messages to that frame: a synthesized key press only reaches the foreground
                // window, and Windows does not let PastDow bring an already running browser to the front
                PostMessage(hwnd, 0x0100 /* WM_KEYDOWN */, (IntPtr)0x0D, (IntPtr)0x001C0001);
                PostMessage(hwnd, 0x0102 /* WM_CHAR */, (IntPtr)0x0D, (IntPtr)0x001C0001);
                PostMessage(hwnd, 0x0101 /* WM_KEYUP */, (IntPtr)0x0D, unchecked((IntPtr)(int)0xC01C0001));

                step("Turning on Developer mode");
                // Chrome puts id="devMode" on the toggle itself; Brave on a wrapper with the toggle inside
                var toggle = Until(15000, () => Pattern<TogglePattern>(ById(win, "devMode"), TogglePattern.Pattern));
                if (toggle == null) return false;
                if (toggle.Current.ToggleState != ToggleState.On) toggle.Toggle();

                step("Loading the extension");
                // the button only appears once Developer mode is on
                var invoke = Until(8000, () => { var e = ById(win, "loadUnpacked"); return e != null && !e.Current.IsOffscreen ? Pattern<InvokePattern>(e, InvokePattern.Pattern) : null; });
                if (invoke == null) return false;
                int[] pids = Process.GetProcessesByName(procName).Select(p => p.Id).ToArray();
                var dialogsBefore = Dialogs(pids);
                // Invoke blocks until the modal folder dialog closes in some browsers: press it from another thread
                new Thread(() => { try { invoke.Invoke(); } catch { } }) { IsBackground = true }.Start();

                IntPtr dlg = Until(10000, () => Dialogs(Process.GetProcessesByName(procName).Select(p => p.Id).ToArray()).FirstOrDefault(d => !dialogsBefore.Contains(d)));
                if (dlg == IntPtr.Zero) return false;
                IntPtr edit = Until(4000, () => Child(dlg, "Edit", 1152));
                IntPtr ok = Child(dlg, "Button", 1);
                if (edit == IntPtr.Zero || ok == IntPtr.Zero) return false;
                SendMessage(edit, 0x000C /* WM_SETTEXT */, IntPtr.Zero, folder);
                Thread.Sleep(150);
                SendMessage(ok, 0x00F5 /* BM_CLICK */, IntPtr.Zero, IntPtr.Zero);

                step("Checking");
                var listed = Until(10000, () =>
                {
                    foreach (AutomationElement e in win.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, "name")))
                        if (e.Current.Name == ExtensionName) return e;
                    return null;
                });
                return listed != null;
            }
            catch { return false; }
        }

        /// <summary>The pattern on the element itself or, failing that, on its first descendant that has it.</summary>
        static T Pattern<T>(AutomationElement e, AutomationPattern pattern) where T : BasePattern
        {
            if (e == null) return null;
            if (e.TryGetCurrentPattern(pattern, out object p)) return (T)p;
            foreach (AutomationElement child in e.FindAll(TreeScope.Descendants, Condition.TrueCondition))
                if (child.TryGetCurrentPattern(pattern, out p)) return (T)p;
            return null;
        }

        static AutomationElement ById(AutomationElement root, string id)
        {
            try { return root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, id)); }
            catch { return null; }
        }

        static T Until<T>(int ms, Func<T> probe)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms)
            {
                try { var v = probe(); if (v != null && !v.Equals(default(T))) return v; } catch { }
                Thread.Sleep(200);
            }
            return default(T);
        }

        // ---------- Win32 ----------
        delegate bool EnumProc(IntPtr h, IntPtr l);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumProc p, IntPtr l);
        [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr w, EnumProc p, IntPtr l);
        [DllImport("user32.dll")] static extern int GetWindowThreadProcessId(IntPtr h, out int pid);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll")] static extern int GetDlgCtrlID(IntPtr h);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr SendMessage(IntPtr h, int m, IntPtr w, string l);
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr h, int m, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] static extern bool PostMessage(IntPtr h, int m, IntPtr w, IntPtr l);

        static string Cls(IntPtr h) { var s = new StringBuilder(128); GetClassName(h, s, 128); return s.ToString(); }
        static string Title(IntPtr h) { var s = new StringBuilder(256); GetWindowText(h, s, 256); return s.ToString(); }

        static bool IsOf(IntPtr h, string procName)
        {
            if (h == IntPtr.Zero) return false;
            GetWindowThreadProcessId(h, out int pid);
            try { using (var p = Process.GetProcessById(pid)) return string.Equals(p.ProcessName, procName, StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        /// <summary>Visible top-level browser frames of that executable.</summary>
        static System.Collections.Generic.List<IntPtr> Windows(string procName)
        {
            var pids = Process.GetProcessesByName(procName).Select(p => p.Id).ToArray();
            var list = new System.Collections.Generic.List<IntPtr>();
            EnumWindows((h, l) =>
            {
                GetWindowThreadProcessId(h, out int pid);
                if (Array.IndexOf(pids, pid) >= 0 && IsWindowVisible(h) && Cls(h) == "Chrome_WidgetWin_1") list.Add(h);
                return true;
            }, IntPtr.Zero);
            return list;
        }

        static System.Collections.Generic.List<IntPtr> Dialogs(int[] pids)
        {
            var list = new System.Collections.Generic.List<IntPtr>();
            EnumWindows((h, l) =>
            {
                GetWindowThreadProcessId(h, out int pid);
                if (Array.IndexOf(pids, pid) >= 0 && IsWindowVisible(h) && Cls(h) == "#32770") list.Add(h);
                return true;
            }, IntPtr.Zero);
            return list;
        }

        static IntPtr Child(IntPtr parent, string cls, int id)
        {
            IntPtr found = IntPtr.Zero;
            EnumChildWindows(parent, (h, l) => { if (Cls(h) == cls && GetDlgCtrlID(h) == id) { found = h; return false; } return true; }, IntPtr.Zero);
            return found;
        }
    }
}

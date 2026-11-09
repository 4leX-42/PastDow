using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.Win32;

namespace PastDow
{
    /// <summary>
    /// Per-user install, no admin: the exe goes to %LOCALAPPDATA%\Programs\PastDow, a Start menu shortcut makes
    /// it show up in Windows search, and an HKCU uninstall entry lists it in Settings > Apps.
    /// </summary>
    static class Installer
    {
        public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "PastDow");
        public static readonly string Exe = Path.Combine(Dir, "PastDow.exe");
        static readonly string StartLnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "PastDow.lnk");
        static readonly string DesktopLnk = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "PastDow.lnk");
        const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\PastDow";
        static string PortableList => Path.Combine(Settings.Home, "portable.txt");

        public static string Self => Assembly.GetExecutingAssembly().Location;
        public static string Version => Assembly.GetExecutingAssembly().GetName().Version.ToString(3);

        public static bool RunningInstalled => string.Equals(Path.GetFullPath(Self), Path.GetFullPath(Exe), StringComparison.OrdinalIgnoreCase);
        public static bool IsInstalled => File.Exists(Exe);

        public static string InstalledVersion
        {
            get { try { return FileVersionInfo.GetVersionInfo(Exe).FileVersion; } catch { return null; } }
        }

        /// <summary>Portable was chosen for this exact file before: do not ask again.</summary>
        public static bool PortableChosen
        {
            get
            {
                try { return File.Exists(PortableList) && File.ReadAllLines(PortableList).Any(l => string.Equals(l, Self, StringComparison.OrdinalIgnoreCase)); }
                catch { return false; }
            }
        }

        public static void MarkPortable()
        {
            try { Directory.CreateDirectory(Settings.Home); File.AppendAllLines(PortableList, new[] { Self }); } catch { }
        }

        public static void Install(bool desktop)
        {
            Directory.CreateDirectory(Dir);
            // an older copy may be running: ask it to close first
            foreach (var p in Process.GetProcessesByName("PastDow"))
                try
                {
                    if (p.Id != Process.GetCurrentProcess().Id && string.Equals(p.MainModule.FileName, Exe, StringComparison.OrdinalIgnoreCase))
                    { p.CloseMainWindow(); if (!p.WaitForExit(4000)) throw new IOException("Close the running PastDow and try again"); }
                }
                catch (IOException) { throw; }
                catch { }
            File.Copy(Self, Exe, true);
            RemoveUlvark();
            Shortcut(StartLnk);
            if (desktop) Shortcut(DesktopLnk); else TryDelete(DesktopLnk);

            using (var k = Registry.CurrentUser.CreateSubKey(UninstallKey))
            {
                k.SetValue("DisplayName", "PastDow");
                k.SetValue("DisplayVersion", Version);
                k.SetValue("Publisher", "4leX-42");
                k.SetValue("DisplayIcon", Exe + ",0");
                k.SetValue("InstallLocation", Dir);
                k.SetValue("URLInfoAbout", "https://github.com/4leX-42/PastDow");
                k.SetValue("UninstallString", "\"" + Exe + "\" --uninstall");
                k.SetValue("EstimatedSize", (int)(new FileInfo(Exe).Length / 1024), RegistryValueKind.DWord);
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            }
        }

        public static void Launch(string[] args)
        {
            Process.Start(new ProcessStartInfo(Exe, string.Join(" ", args.Select(Runner.Quote))) { UseShellExecute = false, WorkingDirectory = Dir });
        }

        /// <summary>Removes the app, its shortcuts, its uninstall entry and its data (engine, settings). Downloads stay.</summary>
        public static void Uninstall()
        {
            TryDelete(StartLnk);
            TryDelete(DesktopLnk);
            try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); } catch { }
            // the running exe cannot delete itself: a hidden cmd waits for it to exit, then removes both folders
            string cmd = "/c ping 127.0.0.1 -n 3 >nul & rmdir /s /q \"" + Dir + "\" & rmdir /s /q \"" + Settings.Home + "\"";
            Process.Start(new ProcessStartInfo("cmd.exe", cmd) { CreateNoWindow = true, UseShellExecute = false, WorkingDirectory = Path.GetTempPath() });
        }

        static void TryDelete(string f) { try { if (File.Exists(f)) File.Delete(f); } catch { } }

        /// <summary>The app used to be called Ulvark: remove that install (files, shortcuts, Settings entry) when PastDow installs.</summary>
        static void RemoveUlvark()
        {
            try
            {
                foreach (var p in Process.GetProcessesByName("Ulvark")) { try { p.CloseMainWindow(); p.WaitForExit(3000); } catch { } }
                string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Ulvark");
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
                TryDelete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Ulvark.lnk"));
                TryDelete(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Ulvark.lnk"));
                Registry.CurrentUser.DeleteSubKeyTree(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\Ulvark", false);
            }
            catch { }
        }

        /// <summary>.lnk through WScript.Shell, late-bound (no interop assembly needed).</summary>
        static void Shortcut(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var t = Type.GetTypeFromProgID("WScript.Shell");
            object sh = Activator.CreateInstance(t);
            try
            {
                object lnk = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, sh, new object[] { path });
                var lt = lnk.GetType();
                lt.InvokeMember("TargetPath", BindingFlags.SetProperty, null, lnk, new object[] { Exe });
                lt.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, lnk, new object[] { Dir });
                lt.InvokeMember("IconLocation", BindingFlags.SetProperty, null, lnk, new object[] { Exe + ",0" });
                lt.InvokeMember("Description", BindingFlags.SetProperty, null, lnk, new object[] { "Download video and audio at the best quality" });
                lt.InvokeMember("Save", BindingFlags.InvokeMethod, null, lnk, null);
                System.Runtime.InteropServices.Marshal.ReleaseComObject(lnk);
            }
            finally { System.Runtime.InteropServices.Marshal.ReleaseComObject(sh); }
        }
    }
}

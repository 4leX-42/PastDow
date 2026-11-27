using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Microsoft.Win32;

namespace PastDow
{
    /// <summary>
    /// "Install the browser extension" from the menu. Browsers do not let a program add an extension by itself
    /// (only the Web Store or company policies can), so PastDow does everything around it: it unpacks the extension
    /// that travels inside the exe to %LOCALAPPDATA%\PastDow\extension with a pairing.json holding the key (so the
    /// extension pairs itself), opens that browser's extensions page and puts the folder path on the clipboard.
    /// The user only turns on Developer mode, presses "Load unpacked" and pastes the path.
    /// </summary>
    static class ExtensionSetup
    {
        public sealed class Browser { public string Id, Name, Exe, Page, Args = ""; }

        public static string Folder => Path.Combine(Settings.Home, "extension");

        static readonly string Local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        static readonly string Pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        static readonly string Pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

        static readonly (string id, string name, string exe, string page, string[] paths)[] Known =
        {
            ("chrome", "Chrome", "chrome.exe", "chrome://extensions", new[] { @"Google\Chrome\Application\chrome.exe" }),
            ("brave", "Brave", "brave.exe", "brave://extensions", new[] { @"BraveSoftware\Brave-Browser\Application\brave.exe" }),
            ("edge", "Edge", "msedge.exe", "edge://extensions", new[] { @"Microsoft\Edge\Application\msedge.exe" }),
        };

        /// <summary>Installed Chromium browsers that can load the extension, in a fixed order.</summary>
        public static List<Browser> Found()
        {
            var list = new List<Browser>();
            foreach (var k in Known)
            {
                string exe = AppPath(k.exe) ?? k.paths.SelectMany(p => new[] { Path.Combine(Local, p), Path.Combine(Pf, p), Path.Combine(Pf86, p) }).FirstOrDefault(File.Exists);
                if (exe != null) list.Add(new Browser { Id = k.id, Name = k.name, Exe = exe, Page = k.page });
            }
            return list;
        }

        static string AppPath(string exe)
        {
            foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
                try
                {
                    using (var key = hive.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\App Paths\" + exe))
                        if (key?.GetValue(null) is string p && File.Exists(p.Trim('"'))) return p.Trim('"');
                }
                catch { }
            return null;
        }

        /// <summary>Writes the extension files (always the copy inside this exe, so it matches the app) and pairing.json.</summary>
        public static void Unpack()
        {
            string dir = Folder;
            Directory.CreateDirectory(dir);
            var asm = Assembly.GetExecutingAssembly();
            foreach (var name in asm.GetManifestResourceNames().Where(n => n.StartsWith("ext/", StringComparison.Ordinal)))
            {
                string rel = name.Substring(4).Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
                string target = Path.GetFullPath(Path.Combine(dir, rel));
                if (!target.StartsWith(Path.GetFullPath(dir) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                using (var src = asm.GetManifestResourceStream(name))
                using (var dst = File.Create(target)) src.CopyTo(dst);
            }
            RefreshPairing();
        }

        /// <summary>Refresh only the key in an existing installation, atomically.</summary>
        public static void RefreshPairing()
        {
            string dir = Folder;
            if (!Directory.Exists(dir)) return;
            string target = Path.Combine(dir, "pairing.json");
            string temp = Path.Combine(dir, "pairing-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                File.WriteAllText(temp, "{\"key\":\"" + Settings.BrowserKey + "\"}", new UTF8Encoding(false));
                if (File.Exists(target)) File.Replace(temp, target, null);
                else File.Move(temp, target);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }

        /// <summary>
        /// Unpacks and installs in that browser. Returns "done" when the extension is loaded by itself,
        /// "guided" when the user has to finish (folder path then on the clipboard), or null when the browser is unknown.
        /// Runs off the UI thread: the automation waits for the browser.
        /// </summary>
        public static string Install(string id, Action<string> step)
        {
            var b = Found().FirstOrDefault(x => x.Id == id);
            if (b == null) return null;
            Unpack();
            return ExtensionAutoInstall.Run(b, Folder, step) ? "done" : "guided";
        }
    }
}

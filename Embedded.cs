using System;
using System.IO;
using System.Reflection;

namespace Ulvark
{
    /// <summary>
    /// Keeps Ulvark a single exe: the WebView2 managed assemblies load from embedded resources, and the native
    /// WebView2Loader.dll is written once to %LOCALAPPDATA%\Ulvark\wv2.
    /// </summary>
    static class Embedded
    {
        public static readonly string NativeDir = Path.Combine(Settings.Home, "wv2");

        public static void Hook()
        {
            AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
            {
                string name = new AssemblyName(e.Name).Name;
                string res = name == "Microsoft.Web.WebView2.Core" ? "wv2.core.dll" : name == "Microsoft.Web.WebView2.WinForms" ? "wv2.winforms.dll" : null;
                if (res == null) return null;
                using (var st = Assembly.GetExecutingAssembly().GetManifestResourceStream(res))
                using (var ms = new MemoryStream()) { st.CopyTo(ms); return Assembly.Load(ms.ToArray()); }
            };
        }

        public static void ExtractLoader()
        {
            Directory.CreateDirectory(NativeDir);
            string path = Path.Combine(NativeDir, "WebView2Loader.dll");
            using (var st = Assembly.GetExecutingAssembly().GetManifestResourceStream("wv2.loader.dll"))
            {
                if (File.Exists(path) && new FileInfo(path).Length == st.Length) return;
                using (var f = File.Create(path)) st.CopyTo(f);
            }
        }

        public static string Text(string res)
        {
            using (var st = Assembly.GetExecutingAssembly().GetManifestResourceStream(res))
            using (var r = new StreamReader(st)) return r.ReadToEnd();
        }
    }
}

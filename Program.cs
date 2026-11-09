using System;
using System.Linq;
using System.Windows.Forms;

namespace PastDow
{
    static class Program
    {
        /// <summary>
        /// PastDow.exe [URL ...]: links passed on the command line are queued at start.
        /// PastDow.exe --uninstall: what Settings > Apps runs.
        /// First run from a downloaded file asks: install (default) or portable.
        /// </summary>
        [STAThread]
        static void Main(string[] args)
        {
            Embedded.Hook();   // before any WebView2 type is touched
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Settings.Load();

            if (args.Contains("--uninstall"))
            {
                using (var f = new SetupForm(SetupForm.Kind.Uninstall))
                {
                    f.OnContinue = report => { Installer.Uninstall(); System.Threading.Thread.Sleep(400); return null; };
                    f.ShowDialog();
                }
                return;
            }

            // a copy installed by winget is already managed by winget: no install/portable question
            bool fromWinget = Installer.Self.IndexOf(@"\WinGet\", StringComparison.OrdinalIgnoreCase) >= 0;
            if (!Installer.RunningInstalled && !Installer.PortableChosen && !fromWinget && Environment.GetEnvironmentVariable("PASTDOW_NO_SETUP") != "1")
            {
                using (var f = new SetupForm(SetupForm.Kind.Setup))
                {
                    bool chosenInstall = false, desktop = false;
                    f.OnContinue = report =>
                    {
                        chosenInstall = (bool)f.Invoke((Func<bool>)(() => f.InstallChosen));
                        desktop = (bool)f.Invoke((Func<bool>)(() => f.Desktop));
                        if (!chosenInstall) { Installer.MarkPortable(); return null; }
                        try { Installer.Install(desktop); }
                        catch (Exception ex) { return ex.Message; }
                        // start the installed copy and stay on screen until it is visible: no blank gap
                        report("OPENING PASTDOW");
                        using (var ready = new System.Threading.EventWaitHandle(false, System.Threading.EventResetMode.ManualReset, AppForm.ReadyEvent))
                        {
                            Installer.Launch(args.Concat(new[] { "--from-setup" }).ToArray());
                            ready.WaitOne(20000);
                        }
                        return null;
                    };
                    if (f.ShowDialog() != DialogResult.OK) return;
                    if (chosenInstall) return;
                }
            }

            Run(args);
        }

        // separate method so Main is compiled before the WebView2 assemblies are needed
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        static void Run(string[] args) => Application.Run(new AppForm(args));
    }
}

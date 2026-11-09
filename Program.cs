using System;
using System.Linq;
using System.Windows.Forms;

namespace Ulvark
{
    static class Program
    {
        /// <summary>
        /// Ulvark.exe [URL ...]: links passed on the command line are queued at start.
        /// Ulvark.exe --uninstall: what Settings > Apps runs.
        /// First run from a downloaded file asks: install (default) or portable.
        /// </summary>
        [STAThread]
        static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Settings.Load();

            if (args.Contains("--uninstall"))
            {
                using (var f = new SetupForm(SetupForm.Kind.Uninstall))
                {
                    f.OnContinue = () => { Installer.Uninstall(); return null; };
                    f.ShowDialog();
                }
                return;
            }

            if (!Installer.RunningInstalled && !Installer.PortableChosen && Environment.GetEnvironmentVariable("ULVARK_NO_SETUP") != "1")
            {
                using (var f = new SetupForm(SetupForm.Kind.Setup))
                {
                    f.OnContinue = () =>
                    {
                        if (!f.InstallChosen) { Installer.MarkPortable(); return null; }
                        try { Installer.Install(f.Desktop); return null; }
                        catch (Exception ex) { return ex.Message; }
                    };
                    if (f.ShowDialog() != DialogResult.OK) return;
                    if (f.InstallChosen) { Installer.Launch(args); return; }
                }
            }

            Application.Run(new MainForm(args));
        }
    }
}

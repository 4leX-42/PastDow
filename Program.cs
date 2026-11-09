using System;
using System.Windows.Forms;

namespace Ulvark
{
    static class Program
    {
        /// <summary>Ulvark.exe [URL ...]: links passed on the command line are queued at start.</summary>
        [STAThread]
        static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Settings.Load();
            Application.Run(new MainForm(args));
        }
    }
}

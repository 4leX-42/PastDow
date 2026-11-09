using System;
using System.Windows.Forms;

namespace VidGrab
{
    static class Program
    {
        /// <summary>VidGrab.exe [URL ...]: las URL pasadas se ponen en cola al arrancar.</summary>
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

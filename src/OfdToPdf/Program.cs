using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace OfdToPdf
{
    internal static class Program
    {
        [DllImport("kernel32.dll")]
        private static extern bool AttachConsole(int processId);

        /// <summary>
        /// No args   -> GUI.
        /// With args -> CLI (or files dropped onto the exe):
        ///              OfdToPdf.exe a.ofd b.ofd [-o outdir] [--overwrite]
        /// Exit code 0 = all converted, 1 = at least one failed.
        /// </summary>
        [STAThread]
        private static int Main(string[] args)
        {
            if (args.Length == 0)
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.Run(new MainForm());
                return 0;
            }

            AttachConsole(-1); // print to the parent console when launched from a terminal

            string outDir = null;
            bool overwrite = false;
            var files = new List<string>();
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                if ((a == "-o" || a == "--output") && i + 1 < args.Length) outDir = args[++i];
                else if (a == "--overwrite") overwrite = true;
                else files.Add(a);
            }

            bool failed = false;
            foreach (string f in files)
            {
                var r = Converter.Convert(f, outDir, overwrite);
                if (r.Success) Console.WriteLine("[OK]     " + f + " -> " + r.OutputPath);
                else { Console.WriteLine("[FAILED] " + f + " : " + r.Error); failed = true; }
            }
            return failed ? 1 : 0;
        }
    }
}

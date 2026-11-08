using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace OfdToPdf
{
    internal static class Program
    {
        [DllImport("kernel32.dll")]
        private static extern bool AttachConsole(int processId);

        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        private const string Usage =
@"OfdToPdf - แปลงไฟล์ OFD เป็น PDF

  OfdToPdf.exe                         เปิดหน้าต่างโปรแกรม
  OfdToPdf.exe <ไฟล์หรือโฟลเดอร์>...    แปลงไฟล์ (โฟลเดอร์ = ทุกไฟล์ .ofd รวมโฟลเดอร์ย่อย)

ตัวเลือก:
  -o, --output <โฟลเดอร์>   เก็บ PDF ไว้ที่โฟลเดอร์นี้ (ค่าเริ่มต้น: ข้างไฟล์ต้นฉบับ)
  --overwrite              เขียนทับ PDF ที่มีอยู่แล้ว (ค่าเริ่มต้น: สร้างชื่อใหม่ file (1).pdf)
  --skip-existing          ข้ามไฟล์ที่มี PDF อยู่แล้ว
  --cli                    ทำงานแบบ command line เสมอ แม้ไม่ได้เปิดจาก terminal
  -h, --help               แสดงวิธีใช้

Exit code: 0 = สำเร็จทั้งหมด, 1 = มีไฟล์ล้มเหลวหรือได้หน้าไม่ครบ, 2 = ใช้ตัวเลือกผิด";

        /// <summary>
        /// No args: GUI. With files: command line when started from a terminal (or with --cli);
        /// otherwise (files dropped onto the exe, "Send to") the GUI opens and converts them, so
        /// the user always sees the result.
        /// </summary>
        [STAThread]
        private static int Main(string[] args)
        {
            bool console = AttachConsole(-1);

            string outDir = null, snapshot = null;
            var mode = ConflictMode.Rename;
            bool forceCli = false;
            var inputs = new List<string>();
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                if ((a == "-o" || a == "--output") && i + 1 < args.Length) outDir = args[++i];
                else if (a == "--overwrite") mode = ConflictMode.Overwrite;
                else if (a == "--skip-existing") mode = ConflictMode.Skip;
                else if (a == "--cli") forceCli = true;
                else if (a == "--ui-snapshot" && i + 1 < args.Length) snapshot = args[++i]; // CI only
                else if (a == "-h" || a == "--help" || a == "/?") { Console.WriteLine(Usage); return 0; }
                else if (a.StartsWith("-") && !File.Exists(a) && !Directory.Exists(a))
                {
                    Console.WriteLine("ตัวเลือกไม่ถูกต้อง: " + a + Environment.NewLine + Environment.NewLine + Usage);
                    return 2;
                }
                else inputs.Add(a);
            }

            if (inputs.Count > 0 && (forceCli || console) && snapshot == null)
                return RunCli(inputs, outDir, mode);

            return RunGui(inputs, snapshot);
        }

        private static int RunGui(List<string> inputs, string snapshot)
        {
            try { SetProcessDPIAware(); } catch (Exception) { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) => ReportCrash(e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => ReportCrash(e.ExceptionObject as Exception);

            var form = new MainForm(inputs, autoStart: inputs.Count > 0, snapshotPath: snapshot);
            Application.Run(form);
            return 0;
        }

        private static int RunCli(List<string> inputs, string outDir, ConflictMode mode)
        {
            var files = ExpandInputs(inputs).ToList();
            if (files.Count == 0)
            {
                Console.WriteLine("ไม่พบไฟล์ .ofd");
                return 1;
            }

            var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int failed = 0;
            foreach (string f in files)
            {
                var r = Converter.Convert(f, outDir, mode, claimed);
                switch (r.Kind)
                {
                    case OutcomeKind.Converted:
                        if (r.Truncated)
                        {
                            Console.WriteLine("[PARTIAL] " + f + " -> " + r.OutputPath);
                            failed++; // an incomplete PDF is not a success for scripts
                        }
                        else Console.WriteLine("[OK]      " + f + " -> " + r.OutputPath);
                        break;
                    case OutcomeKind.Skipped:
                        Console.WriteLine("[SKIPPED] " + f + " (มี " + r.OutputPath + " อยู่แล้ว)");
                        break;
                    default:
                        Console.WriteLine("[FAILED]  " + f + " : " + r.Error);
                        failed++;
                        break;
                }
                foreach (var kv in r.SubstitutedFonts)
                    Console.WriteLine("          แทนฟอนต์: " + kv.Key + " -> " + kv.Value);
                if (r.MissingFonts.Count > 0)
                    Console.WriteLine("          ไม่มีฟอนต์ (ตัวอักษรอาจเพี้ยน): " + string.Join(", ", r.MissingFonts));
                foreach (string w in r.Warnings)
                    Console.WriteLine("          หมายเหตุ: " + w);
            }
            Console.WriteLine(string.Format("เสร็จ: {0} ไฟล์, ล้มเหลว/ไม่ครบ {1}", files.Count, failed));
            return failed > 0 ? 1 : 0;
        }

        /// <summary>Files as given, folders expanded to every .ofd inside (recursively), no duplicates.</summary>
        public static IEnumerable<string> ExpandInputs(IEnumerable<string> inputs)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string p in inputs)
            {
                IEnumerable<string> found;
                if (Directory.Exists(p))
                {
                    try { found = Directory.EnumerateFiles(p, "*.ofd", SearchOption.AllDirectories).ToList(); }
                    catch (Exception) { continue; } // unreadable folder
                }
                else
                {
                    found = new[] { p };
                }
                foreach (string f in found)
                {
                    string full;
                    try { full = Path.GetFullPath(f); } catch (Exception) { continue; }
                    if (seen.Add(full)) yield return full;
                }
            }
        }

        private static int crashShown;

        private static void ReportCrash(Exception ex)
        {
            Log.Write("unhandled: " + ex);
            if (Interlocked.Exchange(ref crashShown, 1) == 1) return;
            MessageBox.Show(
                "เกิดข้อผิดพลาดที่ไม่คาดคิด:\n\n" + (ex == null ? "(ไม่ทราบสาเหตุ)" : ex.Message) +
                "\n\nรายละเอียดถูกบันทึกไว้ที่:\n" + AppPaths.LogFile,
                "OFD → PDF Converter", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Interlocked.Exchange(ref crashShown, 0);
        }
    }
}

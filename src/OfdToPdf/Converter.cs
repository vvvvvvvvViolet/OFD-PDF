using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using Spire.Pdf;
using Spire.Pdf.Conversion;

namespace OfdToPdf
{
    public enum OutcomeKind { Converted, Skipped, Failed }

    public sealed class ConvertOutcome
    {
        public OutcomeKind Kind { get; set; }
        public bool Success { get { return Kind == OutcomeKind.Converted; } }
        public string OutputPath { get; set; }
        public string Error { get; set; }
        public OfdInfo Info { get; set; }
        /// <summary>Fonts the OFD needs that are neither embedded nor installed (text may look wrong).</summary>
        public List<string> MissingFonts { get; set; } = new List<string>();
        /// <summary>Missing fonts that were automatically replaced: original -> replacement.</summary>
        public Dictionary<string, string> SubstitutedFonts { get; set; } = new Dictionary<string, string>();
        /// <summary>Pages in the written PDF (-1 if it could not be counted).</summary>
        public int PdfPages { get; set; } = -1;
        /// <summary>The PDF has fewer pages than the OFD: the free engine dropped the rest.</summary>
        public bool Truncated { get; set; }
        /// <summary>Things the user should check in the result (page limit, signatures, ...).</summary>
        public List<string> Warnings { get; set; } = new List<string>();
    }

    public static class Converter
    {
        /// <summary>Spire is not documented as thread-safe: every engine call goes through this lock.</summary>
        private static readonly object EngineLock = new object();

        static Converter()
        {
            RefreshFonts();
        }

        /// <summary>
        /// Re-read installed fonts and the fonts folders, so fonts added while the program is open
        /// are used by the next conversion.
        /// </summary>
        public static void RefreshFonts()
        {
            FontCheck.Refresh();
            lock (EngineLock)
            try
            {
                PdfDocument.ClearCustomFonts();
                foreach (string dir in AppPaths.ExistingFontDirs())
                    PdfDocument.LoadCustomFontFolder(dir);
            }
            catch (Exception ex)
            {
                // Font folders are best-effort; conversion still works with system fonts.
                Log.Write("font folder load failed: " + ex.Message);
            }
        }

        /// <summary>
        /// Destination PDF path for an input file: <paramref name="outputDir"/> if given,
        /// otherwise the input's own folder.
        /// </summary>
        public static string GetOutputPath(string input, string outputDir)
        {
            string dir = string.IsNullOrWhiteSpace(outputDir)
                ? Path.GetDirectoryName(Path.GetFullPath(input))
                : outputDir;
            return Path.Combine(dir, Path.GetFileNameWithoutExtension(input) + ".pdf");
        }

        /// <param name="claimed">
        /// Output paths already written in this batch. Two inputs with the same name never write
        /// to the same PDF, even in <see cref="ConflictMode.Overwrite"/>.
        /// </param>
        public static ConvertOutcome Convert(string input, string outputDir, ConflictMode mode, ISet<string> claimed = null)
        {
            var outcome = Run(input, outputDir, mode, claimed);
            Log.Write(string.Format("{0,-9} {1} -> {2}{3}", outcome.Truncated ? "Partial" : outcome.Kind.ToString(), input, outcome.OutputPath,
                outcome.Error != null ? "  error: " + outcome.Error : ""));
            return outcome;
        }

        private static ConvertOutcome Run(string input, string outputDir, ConflictMode mode, ISet<string> claimed)
        {
            var outcome = new ConvertOutcome();
            string temp = null, tempPdf = null;
            try
            {
                if (string.IsNullOrEmpty(input) || !File.Exists(input))
                    return Fail(outcome, "ไม่พบไฟล์");
                if (!string.Equals(Path.GetExtension(input), ".ofd", StringComparison.OrdinalIgnoreCase))
                    return Fail(outcome, "ไม่ใช่ไฟล์ .ofd");

                outcome.Info = OfdInfo.Inspect(input);
                if (!outcome.Info.IsValid)
                    return Fail(outcome, outcome.Info.Error);

                string output = ReserveOutput(GetOutputPath(input, outputDir), mode, claimed);
                if (output == null)
                {
                    outcome.Kind = OutcomeKind.Skipped;
                    outcome.OutputPath = GetOutputPath(input, outputDir);
                    return outcome;
                }
                outcome.OutputPath = output;
                Directory.CreateDirectory(Path.GetDirectoryName(output));

                // Render into a temporary file next to the target, then swap it in, so a failed
                // conversion never leaves a half-written PDF or destroys an existing one.
                tempPdf = Path.Combine(Path.GetDirectoryName(output),
                    "~" + Path.GetFileNameWithoutExtension(output) + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp.pdf");

                var plan = FontCheck.Analyze(input);
                if (plan.Substitutions.Count > 0)
                {
                    temp = FontCheck.WriteSubstitutedCopy(input, plan.Substitutions);
                    try
                    {
                        ToPdf(temp, tempPdf);
                    }
                    catch (Exception ex)
                    {
                        // The patched copy should never be worse, but fall back to the original.
                        Log.Write("font substitution failed, retrying original: " + ex.Message);
                        plan.Unresolved.AddRange(plan.Substitutions.Keys);
                        plan.Substitutions.Clear();
                        ToPdf(input, tempPdf);
                    }
                }
                else
                {
                    ToPdf(input, tempPdf);
                }

                Commit(tempPdf, output);
                tempPdf = null;

                outcome.Kind = OutcomeKind.Converted;
                outcome.MissingFonts = plan.Unresolved;
                outcome.SubstitutedFonts = plan.Substitutions;
                outcome.PdfPages = CountPdfPages(output);
                if (outcome.PdfPages >= 0 && outcome.PdfPages < outcome.Info.PageCount)
                {
                    outcome.Truncated = true;
                    outcome.Warnings.Add(string.Format(
                        "PDF มีแค่ {0} จาก {1} หน้า — Free Spire.PDF แปลงไฟล์ OFD ได้เฉพาะ {2} หน้าแรก",
                        outcome.PdfPages, outcome.Info.PageCount, OfdInfo.FreePageLimit));
                }
                if (outcome.Info.HasSignatures)
                    outcome.Warnings.Add("ต้นฉบับมีลายเซ็นดิจิทัล — PDF เป็นสำเนาสำหรับอ่าน ลายเซ็นไม่ได้ถูกตรวจหรือคงไว้");
                return outcome;
            }
            catch (IOException ex) when (IsLocked(ex))
            {
                return Fail(outcome, "ไฟล์ PDF ปลายทางถูกเปิดอยู่ในโปรแกรมอื่น — ปิดไฟล์แล้วลองใหม่");
            }
            catch (UnauthorizedAccessException)
            {
                return Fail(outcome, "ไม่มีสิทธิ์เขียนไฟล์ในโฟลเดอร์ปลายทาง");
            }
            catch (Exception ex)
            {
                Log.Write("conversion error: " + ex);
                return Fail(outcome, ex.Message);
            }
            finally
            {
                if (temp != null) TryDelete(temp);
                if (tempPdf != null) TryDelete(tempPdf);
            }
        }

        /// <summary>
        /// Picks the PDF path to write, honouring <paramref name="mode"/> and paths already used in
        /// this batch. Returns null when the file should be skipped.
        /// </summary>
        private static string ReserveOutput(string wanted, ConflictMode mode, ISet<string> claimed)
        {
            var batch = claimed ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            lock (batch)
            {
                string path = wanted;
                bool usedInBatch = batch.Contains(path);
                if (File.Exists(path) || usedInBatch)
                {
                    if (mode == ConflictMode.Skip && !usedInBatch) return null;
                    if (mode == ConflictMode.Rename || usedInBatch) path = UniquePath(path, batch);
                }
                batch.Add(path);
                return path;
            }
        }

        private static void Commit(string tempPdf, string output)
        {
            if (!File.Exists(output))
            {
                File.Move(tempPdf, output);
                return;
            }
            try
            {
                File.Replace(tempPdf, output, null);
            }
            catch (PlatformNotSupportedException)
            {
                // Some network shares cannot replace in place.
                File.Copy(tempPdf, output, true);
                File.Delete(tempPdf);
            }
        }

        private static void ToPdf(string input, string output)
        {
            lock (EngineLock)
            {
                var converter = new OfdConverter(input);
                try
                {
                    converter.ToPdf(output);
                }
                finally
                {
                    converter.Dispose();
                }
            }
        }

        /// <summary>
        /// Renders one page of a PDF for the in-app preview. Returns null if it cannot be rendered.
        /// </summary>
        public static Image RenderPdfPage(string pdf, int pageIndex, int dpi, out int pageCount)
        {
            pageCount = 0;
            try
            {
                lock (EngineLock)
                {
                    var doc = new PdfDocument();
                    try
                    {
                        doc.LoadFromFile(pdf);
                        pageCount = doc.Pages.Count;
                        if (pageCount == 0) return null;
                        pageIndex = Math.Max(0, Math.Min(pageIndex, pageCount - 1));
                        object rendered = doc.SaveAsImage(pageIndex, dpi, dpi);
                        var image = rendered as Image;
                        if (image != null) return image;
                        var stream = rendered as Stream;
                        return stream == null ? null : Image.FromStream(stream);
                    }
                    finally
                    {
                        doc.Close();
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Write("preview failed: " + ex.Message);
                return null;
            }
        }

        private static int CountPdfPages(string pdf)
        {
            try
            {
                lock (EngineLock)
                {
                    var doc = new PdfDocument();
                    try
                    {
                        doc.LoadFromFile(pdf);
                        return doc.Pages.Count;
                    }
                    finally
                    {
                        doc.Close();
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Write("page count failed: " + ex.Message);
                return -1;
            }
        }

        private static bool IsLocked(IOException ex)
        {
            int code = ex.HResult & 0xFFFF;
            return code == 32 || code == 33; // ERROR_SHARING_VIOLATION, ERROR_LOCK_VIOLATION
        }

        private static ConvertOutcome Fail(ConvertOutcome outcome, string message)
        {
            outcome.Kind = OutcomeKind.Failed;
            outcome.Error = message;
            return outcome;
        }

        private static void TryDelete(string path)
        {
            try { File.Delete(path); } catch (Exception) { }
        }

        private static string UniquePath(string path, ISet<string> batch)
        {
            string dir = Path.GetDirectoryName(path);
            string name = Path.GetFileNameWithoutExtension(path);
            string ext = Path.GetExtension(path);
            for (int i = 1; ; i++)
            {
                string candidate = Path.Combine(dir, name + " (" + i + ")" + ext);
                if (!File.Exists(candidate) && !batch.Contains(candidate)) return candidate;
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using Spire.Pdf;
using Spire.Pdf.Conversion;

namespace OfdToPdf
{
    public sealed class ConvertOutcome
    {
        public bool Success { get; set; }
        public string OutputPath { get; set; }
        public string Error { get; set; }
        /// <summary>Fonts the OFD needs that are neither embedded nor installed (text may look wrong).</summary>
        public List<string> MissingFonts { get; set; } = new List<string>();
    }

    public static class Converter
    {
        static Converter()
        {
            // Let the engine resolve fonts from the "fonts" folder next to the exe as well as
            // the system fonts, so CJK/Thai/other scripts render instead of falling back to boxes.
            try
            {
                if (Directory.Exists(FontCheck.CustomFontDir))
                    PdfDocument.SetCustomFontsFolders(FontCheck.CustomFontDir);
            }
            catch (Exception)
            {
                // Font folder is best-effort; conversion still works with system fonts.
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

        public static ConvertOutcome Convert(string input, string outputDir, bool overwrite)
        {
            try
            {
                if (string.IsNullOrEmpty(input) || !File.Exists(input))
                    return Fail("Input file not found");
                if (!string.Equals(Path.GetExtension(input), ".ofd", StringComparison.OrdinalIgnoreCase))
                    return Fail("Not an .ofd file");

                string output = GetOutputPath(input, outputDir);
                if (!overwrite && File.Exists(output))
                    output = UniquePath(output);

                Directory.CreateDirectory(Path.GetDirectoryName(output));

                var missing = FontCheck.FindMissingFonts(input);

                var converter = new OfdConverter(input);
                try
                {
                    converter.ToPdf(output);
                }
                finally
                {
                    converter.Dispose();
                }

                return new ConvertOutcome { Success = true, OutputPath = output, MissingFonts = missing };
            }
            catch (Exception ex)
            {
                return Fail(ex.Message);
            }
        }

        private static ConvertOutcome Fail(string message)
        {
            return new ConvertOutcome { Success = false, Error = message };
        }

        private static string UniquePath(string path)
        {
            string dir = Path.GetDirectoryName(path);
            string name = Path.GetFileNameWithoutExtension(path);
            string ext = Path.GetExtension(path);
            for (int i = 1; ; i++)
            {
                string candidate = Path.Combine(dir, name + " (" + i + ")" + ext);
                if (!File.Exists(candidate)) return candidate;
            }
        }
    }
}

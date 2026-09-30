using System;
using System.IO;
using Spire.Pdf.Conversion;

namespace OfdToPdf
{
    public sealed class ConvertOutcome
    {
        public bool Success { get; set; }
        public string OutputPath { get; set; }
        public string Error { get; set; }
    }

    public static class Converter
    {
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

                var converter = new OfdConverter(input);
                converter.ToPdf(output);

                return new ConvertOutcome { Success = true, OutputPath = output };
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

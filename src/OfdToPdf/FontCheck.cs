using System;
using System.Collections.Generic;
using System.Drawing.Text;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml;

namespace OfdToPdf
{
    /// <summary>
    /// Finds fonts an OFD file uses but does not embed, and reports which of them are not
    /// available on this machine. A missing font is the usual cause of garbled/boxed text:
    /// the renderer substitutes a font that lacks the glyphs (e.g. 楷体/仿宋 on non-Chinese Windows).
    /// </summary>
    public static class FontCheck
    {
        /// <summary>Folder next to the exe where users can drop extra .ttf/.ttc/.otf fonts.</summary>
        public static readonly string CustomFontDir =
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "fonts");

        // Chinese font names used in OFD files -> Windows family names.
        private static readonly Dictionary<string, string> Aliases = new Dictionary<string, string>
        {
            { "宋体", "simsun" }, { "新宋体", "nsimsun" }, { "黑体", "simhei" },
            { "楷体", "kaiti" }, { "仿宋", "fangsong" }, { "微软雅黑", "microsoftyahei" },
            { "等线", "dengxian" }, { "隶书", "lisu" }, { "幼圆", "youyuan" },
        };

        private static HashSet<string> available;

        public static List<string> FindMissingFonts(string ofdPath)
        {
            var missing = new List<string>();
            var have = Available();
            foreach (string font in NonEmbeddedFonts(ofdPath))
                if (!have.Contains(Normalize(font)) && !missing.Contains(font))
                    missing.Add(font);
            return missing;
        }

        private static IEnumerable<string> NonEmbeddedFonts(string ofdPath)
        {
            var result = new HashSet<string>();
            try
            {
                using (var zip = ZipFile.OpenRead(ofdPath))
                {
                    foreach (var entry in zip.Entries.Where(e => e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)))
                    {
                        var doc = new XmlDocument();
                        using (var s = entry.Open())
                        {
                            try { doc.Load(s); } catch (XmlException) { continue; }
                        }
                        foreach (XmlElement el in doc.GetElementsByTagName("*").OfType<XmlElement>())
                        {
                            if (el.LocalName != "Font") continue;
                            bool embedded = el.ChildNodes.OfType<XmlElement>().Any(c => c.LocalName == "FontFile");
                            string name = el.GetAttribute("FontName");
                            if (string.IsNullOrEmpty(name)) name = el.GetAttribute("FamilyName");
                            if (!embedded && !string.IsNullOrEmpty(name)) result.Add(name);
                        }
                    }
                }
            }
            catch (Exception)
            {
                // Not a readable OFD package; the converter will report the real error.
            }
            return result;
        }

        private static HashSet<string> Available()
        {
            if (available != null) return available;
            var set = new HashSet<string>();
            using (var installed = new InstalledFontCollection())
                foreach (var f in installed.Families) AddFamily(set, f);

            if (Directory.Exists(CustomFontDir))
            {
                using (var custom = new PrivateFontCollection())
                {
                    foreach (string file in Directory.EnumerateFiles(CustomFontDir))
                    {
                        try { custom.AddFontFile(file); } catch (Exception) { }
                    }
                    foreach (var f in custom.Families) AddFamily(set, f);
                }
            }
            return available = set;
        }

        private static void AddFamily(HashSet<string> set, System.Drawing.FontFamily f)
        {
            set.Add(Normalize(f.Name));
            try { set.Add(Normalize(f.GetName(0x0409))); } catch (Exception) { } // English name
            try { set.Add(Normalize(f.GetName(0x0804))); } catch (Exception) { } // Simplified Chinese name
        }

        private static string Normalize(string name)
        {
            string n = name.Replace("_GB2312", "").Replace(" ", "").Trim().ToLowerInvariant();
            string alias;
            return Aliases.TryGetValue(n, out alias) ? alias : n;
        }
    }
}

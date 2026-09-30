using System;
using System.Collections.Generic;
using System.Drawing.Text;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml;

namespace OfdToPdf
{
    /// <summary>What <see cref="FontCheck.Analyze"/> found for one OFD file.</summary>
    public sealed class FontPlan
    {
        /// <summary>Missing font -> installed replacement that covers the same script.</summary>
        public Dictionary<string, string> Substitutions { get; } = new Dictionary<string, string>();

        /// <summary>Missing fonts with no suitable replacement (text may still render wrong).</summary>
        public List<string> Unresolved { get; } = new List<string>();
    }

    /// <summary>
    /// Handles fonts an OFD file uses but does not embed. If such a font is not installed, the
    /// renderer falls back to a font without the needed glyphs and CJK/Thai text comes out as
    /// boxes or with broken spacing (typical: 黑体/楷体/仿宋 on non-Chinese Windows).
    /// We rewrite a temporary copy of the OFD to point at an installed font of the same script.
    /// </summary>
    public static class FontCheck
    {
        /// <summary>Folder next to the exe where users can drop extra .ttf/.ttc/.otf fonts.</summary>
        public static readonly string CustomFontDir =
            Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "fonts");

        // Chinese font names used in OFD files -> normalized Windows family names.
        private static readonly Dictionary<string, string> Aliases = new Dictionary<string, string>
        {
            { "宋体", "simsun" }, { "新宋体", "nsimsun" }, { "黑体", "simhei" },
            { "楷体", "kaiti" }, { "仿宋", "fangsong" }, { "微软雅黑", "microsoftyahei" },
            { "等线", "dengxian" }, { "隶书", "lisu" }, { "幼圆", "youyuan" },
            { "华文宋体", "stsong" }, { "华文楷体", "stkaiti" }, { "华文仿宋", "stfangsong" },
            { "华文黑体", "stheiti" }, { "华文细黑", "stxihei" }, { "华文中宋", "stzhongsong" },
        };

        // Replacement candidates, best match first. Microsoft YaHei and SimSun ship with every
        // Windows 10/11 language edition, so the chains always end in something available.
        private static readonly string[] HeiChain = { "Microsoft YaHei", "DengXian", "SimHei", "SimSun" };
        private static readonly string[] KaiChain = { "KaiTi", "STKaiti", "SimSun", "Microsoft YaHei" };
        private static readonly string[] FangChain = { "FangSong", "STFangsong", "SimSun", "Microsoft YaHei" };
        private static readonly string[] SongChain = { "SimSun", "NSimSun", "Microsoft YaHei" };
        private static readonly string[] ThaiChain = { "Leelawadee UI", "Leelawadee", "Tahoma" };

        private static HashSet<string> available;
        private static readonly object Gate = new object();

        public static FontPlan Analyze(string ofdPath)
        {
            var plan = new FontPlan();
            var have = Available();
            foreach (string font in NonEmbeddedFonts(ofdPath))
            {
                if (have.Contains(Normalize(font))) continue;
                string[] chain = ChainFor(font);
                if (chain == null) continue; // Latin fonts: the engine's own fallback is fine.
                string sub = chain.FirstOrDefault(c => have.Contains(Normalize(c)));
                if (sub != null) plan.Substitutions[font] = sub;
                else plan.Unresolved.Add(font);
            }
            return plan;
        }

        /// <summary>
        /// Writes a copy of the OFD whose font declarations use the substitutes. Returns its path;
        /// the caller deletes it. Only the resource XML changes; pages, images and seals are
        /// copied byte for byte.
        /// </summary>
        public static string WriteSubstitutedCopy(string ofdPath, IDictionary<string, string> subs)
        {
            string temp = Path.Combine(Path.GetTempPath(), "OfdToPdf_" + Guid.NewGuid().ToString("N") + ".ofd");
            using (var src = ZipFile.OpenRead(ofdPath))
            using (var dst = ZipFile.Open(temp, ZipArchiveMode.Create))
            {
                foreach (var entry in src.Entries)
                {
                    var outEntry = dst.CreateEntry(entry.FullName, CompressionLevel.Optimal);
                    byte[] data;
                    using (var s = entry.Open())
                    using (var ms = new MemoryStream())
                    {
                        s.CopyTo(ms);
                        data = ms.ToArray();
                    }
                    if (entry.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                        data = RewriteFonts(data, subs) ?? data;
                    using (var o = outEntry.Open())
                        o.Write(data, 0, data.Length);
                }
            }
            return temp;
        }

        private static byte[] RewriteFonts(byte[] xml, IDictionary<string, string> subs)
        {
            var doc = new XmlDocument { PreserveWhitespace = true };
            try { doc.Load(new MemoryStream(xml)); } catch (XmlException) { return null; }

            bool changed = false;
            foreach (XmlElement el in FontElements(doc))
            {
                foreach (string attr in new[] { "FontName", "FamilyName" })
                {
                    string sub;
                    if (el.HasAttribute(attr) && subs.TryGetValue(el.GetAttribute(attr), out sub))
                    {
                        el.SetAttribute(attr, sub);
                        changed = true;
                    }
                }
            }
            if (!changed) return null;

            var ms = new MemoryStream();
            using (var w = XmlWriter.Create(ms, new XmlWriterSettings { Encoding = new UTF8Encoding(false) }))
                doc.Save(w);
            return ms.ToArray();
        }

        private static IEnumerable<XmlElement> FontElements(XmlDocument doc)
        {
            return doc.GetElementsByTagName("*").OfType<XmlElement>().Where(e => e.LocalName == "Font").ToList();
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
                        foreach (XmlElement el in FontElements(doc))
                        {
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

        private static string[] ChainFor(string font)
        {
            string n = Normalize(font);
            bool cjk = font.Any(c => c >= '㐀' && c <= '鿿') || Aliases.ContainsValue(n);
            if (cjk)
            {
                if (n.Contains("kai") || font.Contains("楷")) return KaiChain;
                if (n.Contains("fang") || font.Contains("仿")) return FangChain;
                if (n.Contains("hei") || n.Contains("yahei") || n.Contains("dengxian") || n.Contains("youyuan")
                    || font.Contains("黑") || font.Contains("圆")) return HeiChain;
                return SongChain;
            }
            bool thai = font.Any(c => c >= '฀' && c <= '๿')
                || new[] { "sarabun", "angsana", "cordia", "browallia", "thsarabun", "niramit" }.Any(k => n.Contains(k));
            return thai ? ThaiChain : null;
        }

        private static HashSet<string> Available()
        {
            lock (Gate)
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
        }

        private static void AddFamily(HashSet<string> set, System.Drawing.FontFamily f)
        {
            set.Add(Normalize(f.Name));
            try { set.Add(Normalize(f.GetName(0x0409))); } catch (Exception) { } // English name
            try { set.Add(Normalize(f.GetName(0x0804))); } catch (Exception) { } // Simplified Chinese name
        }

        /// <summary>"Times New Roman Bold" / "楷体_GB2312" -> "timesnewroman" / "kaiti".</summary>
        private static string Normalize(string name)
        {
            string n = name.Replace("_GB2312", "").Replace(" ", "").Replace("-", "").Replace(",", "")
                .Trim().ToLowerInvariant();
            string alias;
            if (Aliases.TryGetValue(n, out alias)) return alias;
            foreach (string style in new[] { "bolditalic", "bold", "italic", "oblique", "regular" })
                if (n.Length > style.Length && n.EndsWith(style)) return n.Substring(0, n.Length - style.Length);
            return n;
        }
    }
}

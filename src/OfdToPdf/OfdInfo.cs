using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml;

namespace OfdToPdf
{
    /// <summary>Quick facts about an OFD package, read without rendering it.</summary>
    public sealed class OfdInfo
    {
        /// <summary>Free Spire.PDF only handles this many pages per document.</summary>
        public const int FreePageLimit = 10;

        public int PageCount { get; private set; }
        public bool HasSignatures { get; private set; }
        /// <summary>Set when the file cannot be read as an OFD package.</summary>
        public string Error { get; private set; }

        public bool IsValid { get { return Error == null; } }
        public bool OverPageLimit { get { return PageCount > FreePageLimit; } }

        public static OfdInfo Inspect(string path)
        {
            var info = new OfdInfo();
            try
            {
                using (var zip = ZipFile.OpenRead(path))
                {
                    var root = SafeXml.Load(zip.GetEntry("OFD.xml"));
                    if (root == null)
                    {
                        info.Error = "ไม่ใช่ไฟล์ OFD ที่ถูกต้อง (ไม่พบ OFD.xml)";
                        return info;
                    }

                    string docRoot = null;
                    foreach (XmlElement el in SafeXml.Elements(root))
                    {
                        if (el.LocalName == "DocRoot" && docRoot == null) docRoot = el.InnerText.Trim();
                        if (el.LocalName == "Signatures" && el.InnerText.Trim().Length > 0) info.HasSignatures = true;
                    }

                    var doc = docRoot == null ? null : SafeXml.Load(zip.GetEntry(docRoot.TrimStart('/')));
                    if (doc == null)
                    {
                        info.Error = "ไม่ใช่ไฟล์ OFD ที่ถูกต้อง (ไม่พบ Document.xml)";
                        return info;
                    }
                    info.PageCount = SafeXml.Elements(doc).Count(e => e.LocalName == "Page" && e.HasAttribute("BaseLoc"));
                }
            }
            catch (InvalidDataException)
            {
                info.Error = "ไฟล์เสียหรือไม่ใช่ไฟล์ OFD (อ่านเป็น zip ไม่ได้)";
            }
            catch (Exception ex)
            {
                info.Error = ex.Message;
            }
            return info;
        }
    }

    /// <summary>
    /// XML loading for untrusted OFD packages: no DTDs or external entities, and a size cap so
    /// a crafted file cannot exhaust memory.
    /// </summary>
    internal static class SafeXml
    {
        /// <summary>Largest XML part we will parse (uncompressed).</summary>
        public const long MaxPartBytes = 64L * 1024 * 1024;

        public static XmlDocument Load(ZipArchiveEntry entry)
        {
            if (entry == null || entry.Length > MaxPartBytes) return null;
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaxPartBytes,
            };
            var doc = new XmlDocument { XmlResolver = null, PreserveWhitespace = true };
            try
            {
                using (var s = entry.Open())
                using (var r = XmlReader.Create(s, settings))
                    doc.Load(r);
            }
            catch (XmlException)
            {
                return null;
            }
            return doc;
        }

        /// <summary>Same as <see cref="Load(ZipArchiveEntry)"/> for XML already read into memory.</summary>
        public static XmlDocument Load(byte[] xml)
        {
            if (xml.LongLength > MaxPartBytes) return null;
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
            var doc = new XmlDocument { XmlResolver = null, PreserveWhitespace = true };
            try
            {
                using (var r = XmlReader.Create(new MemoryStream(xml), settings))
                    doc.Load(r);
            }
            catch (XmlException)
            {
                return null;
            }
            return doc;
        }

        public static System.Collections.Generic.IEnumerable<XmlElement> Elements(XmlDocument doc)
        {
            return doc.GetElementsByTagName("*").OfType<XmlElement>();
        }
    }
}

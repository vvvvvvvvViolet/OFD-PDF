"""Assert the converted sample PDF renders every Chinese line with a CJK-capable font."""
import sys
import pymupdf

EXPECTED = ["中汽院凯瑞检测认证", "中国国家强制性产品认证证书", "产品标准和技术要求"]

doc = pymupdf.open(sys.argv[1])
page = doc[0]
spans = [s for b in page.get_text("dict")["blocks"] for l in b.get("lines", []) for s in l["spans"]]
for s in spans:
    print(f"{s['font']!r:40} {s['text']}")

failed = False
for want in EXPECTED:
    hit = [s for s in spans if want in s["text"].replace(" ", "")]
    if not hit:
        print(f"FAIL: text {want!r} not found in PDF")
        failed = True
        continue
    # Count glyphs the font actually has; a fallback font without CJK glyphs draws .notdef boxes.
    font = hit[0]["font"]
    xref = next((f[0] for f in page.get_fonts() if f[3].endswith(font.split("+")[-1])), None)
    buf = doc.extract_font(xref)[3] if xref else b""
    missing = []
    if buf:
        fnt = pymupdf.Font(fontbuffer=buf)
        missing = [ch for ch in want if not fnt.has_glyph(ord(ch))]
    status = "FAIL" if missing else "ok"
    print(f"{status}: {want!r} font={font!r} missing_glyphs={missing}")
    failed |= bool(missing)

page.get_pixmap(dpi=80).save(sys.argv[1] + ".png")
sys.exit(1 if failed else 0)

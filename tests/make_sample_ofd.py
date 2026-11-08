"""Build a small OFD whose text uses Chinese fonts that are NOT embedded.

Used by CI to check that missing fonts are substituted instead of rendering as boxes.
Usage: python make_sample_ofd.py out.ofd [--pages N] [--signed]
  --pages N   repeat the page N times (to probe the free edition's page limit)
  --signed    declare a (empty) signature list, like signed certificates do
"""
import argparse
import zipfile

NS = 'xmlns:ofd="http://www.ofdspec.org/2016"'

# (font id, font name, text) - 黑体/楷体 are usually missing on non-Chinese Windows,
# 方正小标宋简体 is a commercial font that is practically never installed.
LINES = [
    (1, "黑体", "中汽院凯瑞检测认证（重庆）有限公司"),
    (2, "楷体", "中国国家强制性产品认证证书"),
    (3, "方正小标宋简体", "产品标准和技术要求"),
    (4, "Times New Roman Bold", "CAERI TESTING AND CERTIFICATION CO., LTD"),
]

ap = argparse.ArgumentParser()
ap.add_argument("out")
ap.add_argument("--pages", type=int, default=1)
ap.add_argument("--signed", action="store_true")
args = ap.parse_args()

signatures = "<ofd:Signatures>Doc_0/Signs/Signatures.xml</ofd:Signatures>" if args.signed else ""
files = {
    "OFD.xml": f'<?xml version="1.0" encoding="UTF-8"?><ofd:OFD {NS} Version="1.0" DocType="OFD">'
               '<ofd:DocBody><ofd:DocInfo><ofd:DocID>ci-sample</ofd:DocID></ofd:DocInfo>'
               f'<ofd:DocRoot>Doc_0/Document.xml</ofd:DocRoot>{signatures}</ofd:DocBody></ofd:OFD>',
    "Doc_0/Document.xml": f'<?xml version="1.0" encoding="UTF-8"?><ofd:Document {NS}><ofd:CommonData>'
               '<ofd:PageArea><ofd:PhysicalBox>0 0 210 297</ofd:PhysicalBox></ofd:PageArea>'
               '<ofd:PublicRes>PublicRes.xml</ofd:PublicRes><ofd:MaxUnitID>10000</ofd:MaxUnitID></ofd:CommonData>'
               '<ofd:Pages>'
               + "".join(f'<ofd:Page ID="{1000 + p}" BaseLoc="Pages/Page_{p}/Content.xml"/>' for p in range(args.pages))
               + '</ofd:Pages></ofd:Document>',
    "Doc_0/PublicRes.xml": f'<?xml version="1.0" encoding="UTF-8"?><ofd:Res {NS} BaseLoc="Res"><ofd:Fonts>'
               + "".join(f'<ofd:Font FontName="{name}" ID="{fid}"/>' for fid, name, _ in LINES)
               + '</ofd:Fonts></ofd:Res>',
}
if args.signed:
    files["Doc_0/Signs/Signatures.xml"] = f'<?xml version="1.0" encoding="UTF-8"?><ofd:Signatures {NS}/>'

for p in range(args.pages):
    objs = []
    for i, (fid, _, text) in enumerate(LINES):
        y = 30 + i * 20
        objs.append(f'<ofd:TextObject ID="{3000 + p * 10 + i}" Boundary="15 {y} 180 10" Font="{fid}" Size="6">'
                    f'<ofd:FillColor Value="0 0 0"/><ofd:TextCode X="0" Y="6">{text}</ofd:TextCode></ofd:TextObject>')
    objs.append(f'<ofd:TextObject ID="{3000 + p * 10 + 9}" Boundary="15 120 180 10" Font="4" Size="6">'
                f'<ofd:FillColor Value="0 0 0"/><ofd:TextCode X="0" Y="6">PAGE {p + 1}</ofd:TextCode></ofd:TextObject>')
    files[f"Doc_0/Pages/Page_{p}/Content.xml"] = (
        f'<?xml version="1.0" encoding="UTF-8"?><ofd:Page {NS}><ofd:Content><ofd:Layer ID="{2000 + p}">'
        + "".join(objs) + '</ofd:Layer></ofd:Content></ofd:Page>')

with zipfile.ZipFile(args.out, "w", zipfile.ZIP_DEFLATED) as z:
    for name, content in files.items():
        z.writestr(name, content.encode("utf-8"))

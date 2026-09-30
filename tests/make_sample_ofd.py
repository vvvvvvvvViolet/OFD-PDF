"""Build a small OFD whose text uses Chinese fonts that are NOT embedded.

Used by CI to check that missing fonts are substituted instead of rendering as boxes.
Usage: python make_sample_ofd.py out.ofd
"""
import sys
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

files = {
    "OFD.xml": f'<?xml version="1.0" encoding="UTF-8"?><ofd:OFD {NS} Version="1.0" DocType="OFD">'
               '<ofd:DocBody><ofd:DocInfo><ofd:DocID>ci-sample</ofd:DocID></ofd:DocInfo>'
               '<ofd:DocRoot>Doc_0/Document.xml</ofd:DocRoot></ofd:DocBody></ofd:OFD>',
    "Doc_0/Document.xml": f'<?xml version="1.0" encoding="UTF-8"?><ofd:Document {NS}><ofd:CommonData>'
               '<ofd:PageArea><ofd:PhysicalBox>0 0 210 297</ofd:PhysicalBox></ofd:PageArea>'
               '<ofd:PublicRes>PublicRes.xml</ofd:PublicRes><ofd:MaxUnitID>100</ofd:MaxUnitID></ofd:CommonData>'
               '<ofd:Pages><ofd:Page ID="10" BaseLoc="Pages/Page_0/Content.xml"/></ofd:Pages></ofd:Document>',
    "Doc_0/PublicRes.xml": f'<?xml version="1.0" encoding="UTF-8"?><ofd:Res {NS} BaseLoc="Res"><ofd:Fonts>'
               + "".join(f'<ofd:Font FontName="{name}" ID="{fid}"/>' for fid, name, _ in LINES)
               + '</ofd:Fonts></ofd:Res>',
}

objs = []
for i, (fid, _, text) in enumerate(LINES):
    y = 30 + i * 20
    objs.append(f'<ofd:TextObject ID="{20 + i}" Boundary="15 {y} 180 10" Font="{fid}" Size="6">'
                f'<ofd:FillColor Value="0 0 0"/><ofd:TextCode X="0" Y="6">{text}</ofd:TextCode></ofd:TextObject>')
files["Doc_0/Pages/Page_0/Content.xml"] = (
    f'<?xml version="1.0" encoding="UTF-8"?><ofd:Page {NS}><ofd:Content><ofd:Layer ID="11">'
    + "".join(objs) + '</ofd:Layer></ofd:Content></ofd:Page>')

with zipfile.ZipFile(sys.argv[1], "w", zipfile.ZIP_DEFLATED) as z:
    for name, content in files.items():
        z.writestr(name, content.encode("utf-8"))

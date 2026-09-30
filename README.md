# OFD → PDF Converter

โปรแกรม Windows (`OfdToPdf.exe` ไฟล์เดียว) สำหรับแปลงไฟล์ OFD เป็น PDF


## ความสามารถ
- GUI: เพิ่มไฟล์ / เพิ่มทั้งโฟลเดอร์ (รวมโฟลเดอร์ย่อย) / ลากวาง / ลบรายการ
- เลือกโฟลเดอร์ปลายทาง หรือบันทึกข้าง ๆ ไฟล์ต้นฉบับ, เลือกเขียนทับหรือสร้างชื่อใหม่
- แสดงสถานะรายไฟล์ + progress bar + ยกเลิกกลางคัน
- CLI / ลากไฟล์ไปวางบน exe:

```
OfdToPdf.exe a.ofd b.ofd [-o C:\out] [--overwrite]
```
exit code `0` = สำเร็จทั้งหมด, `1` = มีไฟล์ล้มเหลว

## วิธีได้ไฟล์ exe
1. **GitHub Actions**: ทุก push จะ build ให้ → แท็บ *Actions* → artifact `OfdToPdf-windows`
   (push tag `v1.0.0` จะแนบ exe ไว้ใน Release อัตโนมัติ)
2. **Build เอง** (Windows + .NET SDK):
   ```
   dotnet build src/OfdToPdf/OfdToPdf.csproj -c Release -o out
   ```
   ได้ `out\OfdToPdf.exe` ไฟล์เดียว (Free Spire.PDF ถูกฝังในตัว exe ด้วย Costura.Fody) — ต้องมี .NET Framework 4.8 (มีใน Windows 10/11 อยู่แล้ว)

## ตัวอักษรเพี้ยน / รองรับหลายภาษา
ตัวอักษรใน PDF เพี้ยนหรือเป็นกล่องสี่เหลี่ยม มักเกิดจากไฟล์ OFD ใช้ฟอนต์ที่ไม่ได้ฝังมาในไฟล์
(ส่วนใหญ่เป็นฟอนต์จีน เช่น 楷体 KaiTi, 仿宋 FangSong) และเครื่องไม่มีฟอนต์นั้น
โปรแกรมจะตรวจให้อัตโนมัติ และแสดง "⚠ ไม่มีฟอนต์: …" ในช่องสถานะ วิธีแก้:

1. ติดตั้ง *Chinese (Simplified) Supplemental Fonts* ใน Settings → Apps → Optional features หรือ
2. คัดลอกไฟล์ฟอนต์ (.ttf/.ttc/.otf) ไปไว้ในโฟลเดอร์ `fonts` ข้างไฟล์ exe แล้วเปิดโปรแกรมใหม่

## ข้อจำกัดสำคัญ
ใช้ Free Spire.PDF (รุ่นฟรีอย่างเป็นทางการของ e-iceblue): **ไม่มี watermark** แต่จำกัด **ไม่เกิน 10 หน้าต่อไฟล์**
ถ้าต้องแปลงเอกสารยาวกว่านั้น ต้องซื้อ license ของ Spire.PDF หรือเปลี่ยน engine ใน `src/OfdToPdf/Converter.cs`
(ที่เดียวที่เรียก engine)

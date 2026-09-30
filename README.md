# OFD → PDF Converter

โปรแกรม Windows (`OfdToPdf.exe` ไฟล์เดียว) สำหรับแปลงไฟล์ OFD เป็น PDF
ออกแบบโดยอ้างอิงโปรเจกต์ [taurusxin/Ofd2Pdf](https://github.com/taurusxin/Ofd2Pdf) (MIT) — ใช้ engine เดียวกัน (Spire.PDF `OfdConverter`)

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
   ได้ `out\OfdToPdf.exe` (Spire.PDF ถูกฝังในตัว exe ด้วย Costura.Fody) — ต้องมี .NET Framework 4.8 (มีใน Windows 10/11 อยู่แล้ว)

## ข้อจำกัดสำคัญ
Spire.PDF รุ่นฟรีจำกัดจำนวนหน้า (ประมาณ 10 หน้า) และใส่ watermark ทดลองใช้ — เหมือนโปรเจกต์ต้นแบบ
ถ้าใช้งานจริงกับเอกสารยาว ต้องซื้อ license ของ Spire.PDF หรือเปลี่ยน engine ใน `src/OfdToPdf/Converter.cs`
(ที่เดียวที่เรียก engine)

# ตรวจงาน audit ของ Claude และทดสอบ Drive จริง (2026-11-11)

ตรวจ diff ของงาน audit บนฐาน `7c27c05` ครอบคลุม Core, Google/OAuth, scheduling, Desktop/Tray, installer, build/install scripts, localization และเอกสาร พร้อมตรวจ regression coverage ที่เพิ่มมา การตรวจนี้ไม่ใช่การรับประกันว่าไม่มีบั๊กหลงเหลือ

## แก้เพิ่มเติม

- `ApplicationService`: รายงาน partial restore ใช้ `CreateNew` และตรวจ ancestor reparse point แทนการเขียนทับ ปล่อยข้อผิดพลาดให้ UI รับรู้แทนการกลืนแล้วอ้างว่าบันทึกรายงานสำเร็จ เพิ่ม tests การรักษาไฟล์เดิมและการเขียนรายงานครบ
- `WindowsScheduler`: เมื่อการลบ task ล้มเหลว ไม่ใช้ exit code ของ `/Query` ตัดสินว่า task ไม่มี เพราะ access denied และบริการล้มก็คืน nonzero เช่นกัน ตรวจผ่าน COM และยอมรับเฉพาะ HRESULT `0x80070002` รวม exception mapping ของ .NET (`FileNotFoundException`) เพิ่มการทดสอบจำแนกรหัส และทดสอบลบชื่อ task แบบสุ่มที่ไม่มีจริง
- `build.ps1`: ตรวจ reparse point ตลอด ancestor ของ output ก่อนทำงานและก่อนลบ output เก่า ป้องกันพาธใต้ artifacts ที่ชี้ออกไปนอกพื้นที่ผ่าน Junction ทดสอบด้วย Junction จำลองแล้วปฏิเสธก่อน publish และรักษาไฟล์ปลายทาง

## ผลยืนยัน

- Automated tests: **336/336**, ไม่ข้าม หลังแก้ exception mapping ที่พบระหว่างทดสอบรอบแรก
- Build solution และ Installer project: **0 warnings / 0 errors** ไม่ได้สร้าง release หรือติดตั้ง
- Google Drive จริง: สร้าง repository ใหม่ด้วย DeviceId และ recovery key ใหม่ ใช้ credential เดิมในหน่วยความจำโดยไม่บันทึก config จริง ทดสอบสองชุดแยก แต่ละชุด 34 ไฟล์ รวม 290,096 bytes (ไฟล์เล็ก 32, ไฟล์ 280 KiB หนึ่งไฟล์, ไฟล์ว่างหนึ่งไฟล์) และ Junction 3 จุดรวมวงวน
- ชุดสุดท้าย: cold backup 19.69 s / 118 requests; unchanged backup 9.96 s / 46 requests; restore 55.31 s / 69 requests ไม่ใช้ผลเล็กนี้ประมาณรอบจริงหรืออ้าง speedup
- ตรวจ byte ทุกไฟล์หลัง restore: **34/34 ตรงกัน** ไม่มี alias ของ Junction ในผลกู้คืน ไม่มีชื่อวัตถุซ้ำใน repository จำลอง เก็บทั้งสอง snapshot ไว้ รอบ unchanged ไม่อัปโหลด chunk ซ้ำ มีเฉพาะ manifest ใหม่
- คำเตือน: ข้าม Junction 3 จุดตามที่คาด ไม่มี error ที่ทำให้ทดสอบล้มใน Drive run
- SHA-256 ของ config จริงก่อนและหลังตรงกัน ไม่เรียก backup/restore/prune กับข้อมูลหรือคลังจริง ไม่แก้ schedule ไม่หยุด Worker ไม่ push หรือติดตั้ง

หลักฐานในเครื่อง: `%TEMP%\PCAutobackup-review-1a2cd971e6c9498391eaa56e66023232\final-fixture\candidate-result.json` และ harness `Program.cs` ในโฟลเดอร์แม่ ชุดจำลองยังอยู่เพื่อให้ตรวจซ้ำได้ ไม่เผยแพร่ credential หรือ recovery key ในรายงานนี้

## ข้อจำกัดที่ยังมีผล

การทดสอบ Drive นี้ใช้ BackupEngine/GoogleDriveBackupStore จริง ไม่ได้คลิก UI หรือรัน installer จริง ไม่ได้ทำ fault injection บน Google จริง; timeout, scope, corruption, partial restore และ retry ใช้ regression tests แบบจำลอง การติดตั้งแบบ staging/rollback, chunk sharding, scrub และการทดสอบ sleep/resume ยังเป็นงานแยกตาม handoff ไม่มีการนับวัตถุในคลังจริงในงานนี้

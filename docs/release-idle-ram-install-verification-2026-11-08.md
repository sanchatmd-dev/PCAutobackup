# ตรวจติดตั้ง Release v2026.11.08-idle-ram — 8 พฤศจิกายน 2026

ติดตั้งไฟล์ PCAutobackup-win-x64.exe ของ Release ทับ build ทดสอบที่มีอยู่ในเครื่องสำเร็จ ตัวติดตั้งคืน exit code 0 ใช้ --silent --no-launch แล้วเปิด Tray รุ่นใหม่หลังตรวจเสร็จ ไม่มีงานสำรองหรือกู้คืนกำลังทำอยู่ขณะติดตั้ง

## การตรวจไฟล์

- SHA-256 ของตัวติดตั้ง: `10146926B97DC30E6AD43E0A2ABF65A05A531C29F0AAAB171959398FBA0C94F3` ตรงกับ digest ของ asset บน GitHub ที่ตรวจไว้ตอนเผยแพร่
- เทียบ SHA-256 ของทุก file entry ใน `installer-payload.zip` จาก build เดียวกับตัวติดตั้ง กับไฟล์ที่ติดตั้งจริง: **813 ไฟล์ ตรงครบ 813 ไฟล์ ไม่ตรง 0 ไฟล์** ข้ามเฉพาะ directory entries
- ตรวจทั้งแอป Desktop, Worker, Tray, runtime, เอกสารและไฟล์ประกอบใน payload ไม่ใช่เฉพาะ Core DLL
- Installed Core SHA-256: `F7BB6E24914827B1B222069634E9EEA3BDF134192BD701160F28D0F7B37765C3`
- เปิด installed Worker ด้วย `--help` สำเร็จ โดยไม่เรียกสำรองข้อมูล

ขอบเขตการตรวจคือไฟล์ที่ payload กำหนด ไม่ใช่การพิสูจน์ว่าไม่มีไฟล์อื่นนอก payload อยู่ในโฟลเดอร์ติดตั้ง หลักฐานรายไฟล์อยู่ใน artifacts ที่ไม่รวมใน Git

## การตั้งค่าหลังติดตั้ง

SHA-256 ของ configuration หลักก่อนและหลังตรงกันทุก byte รวมถึงค่า Windows startup Run key และ Task Scheduler XML ยังตรงกับก่อนติดตั้งทั้งหมด Task ยัง Enabled และรอบถัดไปคือ **9 พฤศจิกายน 2026 เวลา 21:00 ตามเวลาไทย** รุ่นนี้ยังใช้ production policy **สำรอง 16 / กู้คืน 16 ก้อน** ไม่มีการแก้ source folders, destination, credentials, recovery key หรือ retention

หลังติดตั้งเปิด Tray รุ่นใหม่ที่ตำแหน่งติดตั้งเดิมเรียบร้อย การตรวจครั้งนี้ไม่ได้สั่งสำรอง/กู้คืนเพิ่มเติมหรือแก้ binaries หลังติดตั้ง ไม่เปลี่ยนตัวติดตั้งหรือ assets บน Release

หลักฐานดิบ: `artifacts/install-release-idle-ram-verification/exit-code.txt`, `file-hashes.json`, `hash-summary.json`, `result.json` และ scheduler XML ก่อน/หลัง (ทั้งหมด ignored) รายงานนี้ไม่รวม credentials, keys, user configuration หรือข้อมูลสำรองจริง
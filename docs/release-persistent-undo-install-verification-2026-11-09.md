# ติดตั้ง Release persistent Undo และตรวจ SHA-256 — 9 พฤศจิกายน 2026

GitHub CI ของ commit `cacf9816cb21f680b9eebd3edd0eb615060f6dcb` ผ่านทั้งสองรอบ:

- [Windows build and tests — push main](https://github.com/sanchatmd-dev/PCAutobackup/actions/runs/42506640307): completed/success
- [Windows build and tests — push tag](https://github.com/sanchatmd-dev/PCAutobackup/actions/runs/42506751272): completed/success

ดาวน์โหลด PCAutobackup-win-x64.exe จาก [Release v2026.11.09-persistent-undo](https://github.com/sanchatmd-dev/PCAutobackup/releases/tag/v2026.11.09-persistent-undo) ใหม่ ขนาด 206,753,521 bytes เทียบ SHA-256 กับ digest ของ asset ที่ GitHub รายงานและไฟล์ build ในเครื่อง ตรงทั้งหมด:

`856CFD528A7174BC4DF6B300DDDEAAEEFED727FA0CD827CEE276A9A7A6D26FFE`

รัน EXE ที่ดาวน์โหลดด้วย `--silent --no-launch` ขณะไม่มีหน้าตั้งค่าหรือ Worker ทำงาน ติดตั้งทับรุ่นก่อนหน้า สำเร็จ exit code 0 จากนั้นเทียบ SHA-256 ของทุก file entry ใน installer-payload.zip กับไฟล์ติดตั้งจริง **816/816** ตรงทั้งหมด mismatch 0 ตรวจว่า archive ตรงกับ embedded resource ใน Installer assembly ของ build เดียวกัน และ EXE จาก build ตรงกับ EXE ที่ดาวน์โหลด การตรวจนี้ครอบคลุมไฟล์ที่คาดหวัง ไม่ได้ยืนยันว่าไม่มีไฟล์ส่วนเกินในโฟลเดอร์ติดตั้ง

Installed Core SHA-256:

`70A9C27081EE21082C149C30097E6A445E10297FD59E6044FEA6827D57C42C5B`

SHA-256 configuration จริง, startup Run key และ Task Scheduler XML ก่อน–หลังตรงเดิมทั้งหมด ScheduleEnabled=true, DailyTime=21:00, DailyTimes=null ใช้ single-time compatibility เดิม รอบถัดไปขณะตรวจคือ 9 พฤศจิกายน 2026 เวลา 21:00 +07:00 Production ApplicationService ยังคงกำหนดสำรองและกู้คืน 16/16 ไม่มีการแก้ source ในงานติดตั้งนี้

เริ่ม Tray จากโฟลเดอร์ติดตั้งจริงหลังตรวจเสร็จ ไม่เปิดหน้าตั้งค่าอัตโนมัติและไม่เริ่มงานสำรอง/กู้คืนเพิ่มเติม ผลทดสอบปุ่มและประวัติจากรอบก่อนอยู่ที่ [รายงานหน้าต่างจริง](persistent-source-ui-verification-2026-11-09.md) งานนี้ทดสอบ EXE ที่เผยแพร่และความถูกต้องของไฟล์ติดตั้ง ไม่ได้วัด RAM หรือทดสอบ transfer ซ้ำ

ผลดิบอยู่ใน ignored artifacts/install-release-persistent-undo ไม่มีการเก็บ credentials หรือ configuration จริงในรายงาน เผยแพร่รายงานนี้ใน repository และเพิ่มผลตรวจในรายละเอียด Release เดิม โดยคงตัวติดตั้งเดิม

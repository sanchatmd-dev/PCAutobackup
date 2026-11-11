# UI และ installer QA — 2026-11-11 (ยังไม่ครบ)

สถานะ: **ยังไม่รับรองพร้อมติดตั้งใช้งานจริง** ต้องทดสอบ clean install/upgrade ใน Windows VM/Sandbox แยกก่อน

## ผ่านในรอบนี้
- เปิด WPF build ล่าสุดด้วย `--config` ที่ชี้ไป temp, DeviceId/recovery key ใหม่, source และ local repository จำลอง ไม่มี OAuth หรือแหล่งข้อมูลจริงใน config ทดสอบ
- คลิกสำรองผ่าน UI สำเร็จ 1 ไฟล์ 22 bytes แสดง 100%, สถานะสำเร็จ และประวัติ 1 snapshot ถูกต้อง
- เปิดหน้า settings/history และปิดหน้าต่างทดสอบได้
- เรียก install.ps1 สำเนาใน temp พร้อม package marker จำลอง: ปฏิเสธเมื่อมี app/Worker ทำงาน ก่อนคัดลอกหรือแก้ registry นี่เป็นการทดสอบ busy guard ของ helper เท่านั้น ไม่ใช่การติดตั้ง EXE สำเร็จ
- ตรวจหลังงาน: SHA-256 config จริงและ installed Core เท่าเดิม, task XML และ HKCU startup เท่าเดิม, Worker จริง PID 12120 ยังทำงาน ไม่มีการหยุด Worker หรือเขียนคลังจริง

## ยังไม่ผ่านการยืนยัน
- Restore ผ่าน UI: เปิดและเลือก snapshot ได้ แต่ computer-use ส่ง input ไปช่อง folder dialog ไม่สำเร็จ (`element 212 is not available in cached app state for dotnet.exe`) ยกเลิก dialog แล้ว ไม่มีการกู้คืนผ่าน UI จนจบ ห้ามนับเป็นผ่านจากผล engine test ก่อนหน้า
- Language persistence, partial-restore dialog, cancellation/close during job, Tray startup/relaunch และ sleep/resume ยังไม่ครบ
- Clean install, upgrade, preservation of autostart-off, schedule refresh, installer mutex/rollback ยังไม่ทดสอบจริงในรอบนี้
- ไม่พบ WindowsSandbox.exe หรือ CLI ของ VMware/VirtualBox/QEMU ใน PATH ตัวติดตั้งจริงยัง hardcode destination, shortcuts และ HKCU Run จึงไม่รันติดตั้งบน host ซึ่งจะผิดเงื่อนไขไม่ทับรุ่นจริง ต้องมี Windows ทดสอบแยก ไม่ถือว่าการจำลอง filesystem อย่างเดียวพิสูจน์การติดตั้งได้

หลักฐาน: `%TEMP%\PCAutobackup-ui-qa-dbeb2e01dd6646ea9001d8932ee43a8b\result.json` และ `before.json` ไม่ commit ไฟล์ config/key/ผลลัพธ์จำลอง ไม่มี push หรือติดตั้งทับ ไม่มีการแก้ source ในรอบนี้

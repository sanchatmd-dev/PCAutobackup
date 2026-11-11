# ชุดทดสอบ Windows Sandbox

ชุดพร้อมเปิดอยู่ที่ `artifacts/WindowsSandbox-QA/Start-QA.wsb` ต้องมี Windows Sandbox ที่เปิดใช้งานแล้ว เครื่องนี้ยังไม่พบตัวเรียก Sandbox จึงยังไม่ได้รันชุดนี้จริง การสร้างแพ็กเกจสำเร็จไม่ใช่ผลผ่านการติดตั้ง

## วิธีใช้

1. เปิด `Start-QA.wsb` บนเครื่องนี้เมื่อ Windows Sandbox พร้อมใช้ ห้ามรัน `Input/sandbox-run.ps1` หรือ `Candidate-Setup.exe` บน host โดยตรง หากย้ายชุดไปเครื่องอื่นให้แก้ HostFolder ทั้งสองแห่งใน `.wsb` เป็นพาธจริง
2. รอจนแอป PC AutoBackup เปิดขึ้น ตรวจ `Results/automated-result.txt` ต้องขึ้น PASS หากขึ้น FAIL ให้อ่าน `sandbox-transcript.txt` และยังไม่รับรอง release
3. ทดสอบ UI ตามรายการด้านล่าง บันทึกผลใน `Results/manual-result.txt` ก่อนปิด Sandbox ข้อมูลภายใน Sandbox จะหายเมื่อปิด ยกเว้นไฟล์ในโฟลเดอร์ Results ที่ map กลับ host

## การแยกข้อมูล

- map เฉพาะ Input แบบอ่านอย่างเดียว และ Results แบบเขียนได้ ไม่ map Project, Documents, config จริง หรือคลังสำรองจริง
- ปิด network และ clipboard; ไม่มี credentials หรือข้อมูล Google จริง การทดสอบ Drive จริงแยกอยู่ในรายงานก่อนหน้า
- runner ตรวจชื่อผู้ใช้ `WDAGUtilityAccount` และพาธ `C:\SandboxInput` ก่อนทำงาน ตัว installer เองไม่มี guard นี้ จึงห้ามเปิด EXE โดยตรงบน host
- ไม่เปิด Windows feature, ไม่แก้ registry/task หรือรุ่นติดตั้งของ host ชุดทดสอบนี้ยังไม่ push และยังไม่ติดตั้งทับจริง

## อัตโนมัติ

ติดตั้ง EXE จาก source candidate ล่าสุดด้วย payload ที่สร้างใหม่ สร้าง source จำลอง 3 ไฟล์ (ภาษาไทย, binary, empty), recovery key และ local repository ใหม่ สำรองสำเร็จ สร้างตารางจำลอง ปิด autostart จากนั้นติดตั้ง candidate ซ้ำ ตรวจ config hash, success marker hash, task XML, autostart ที่ปิดไว้ และกู้คืนเทียบ byte 3/3

นี่เป็น clean install และ same-version update ไม่ใช่การยืนยัน migration จาก release เก่าทุกเวอร์ชัน Fixture ใช้ Core จาก build เดียวกับ payload แต่การกู้คืนแบบคลิก UI ต้องทดสอบแยก

## UI ที่ต้องทำต่อใน Sandbox

- Overview: กดสำรองตอนนี้ รอสำเร็จ ตรวจ progress และประวัติ
- History: เลือก snapshot กู้คืนลงโฟลเดอร์ว่างใหม่ ตรวจไฟล์ 3 ไฟล์; เลือกปลายทางที่ไม่ว่างต้องถูกปฏิเสธ
- Settings: เปลี่ยนไทย/อังกฤษ ปิดแล้วเปิดใหม่ ภาษาต้องคงเดิม และ sources/destination/schedule ต้องคงเดิม
- Tray: ปิด/ย่อหน้าต่าง ตรวจ process settings ออกเมื่อ idle และเปิดกลับได้; ตรวจ autostart on/off
- งานที่กำลังทำ: เพิ่มไฟล์จำลองขนาดเหมาะสม ทดสอบ cancel และปิดหน้าต่างระหว่างงาน การ cancel ไม่ควรรายงานสำเร็จ
- Partial restore: ใช้เฉพาะคลังจำลอง ทำสำเนาคลังก่อนทำให้ chunk เสียหนึ่งไฟล์ แล้วตรวจว่ากู้ไฟล์อื่นได้และแจ้งกู้คืนบางส่วน พร้อมรายงาน ไม่ใช้ข้อมูลจริง
- Installer: ขณะ UI/Worker เปิด ตัวติดตั้งต้องปฏิเสธ; เปิด installer สองตัวพร้อมกันต้องไม่คัดลอกชนกัน

ยังไม่ครอบคลุม reboot/logon, sleep/resume, power failure และการ rollback ระหว่างติดตั้ง ไม่ควรสรุปว่าผ่านทั้งหมดจาก automated-result เพียงไฟล์เดียว

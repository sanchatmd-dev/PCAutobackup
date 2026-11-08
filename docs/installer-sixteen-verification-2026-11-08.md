# ตรวจ Google Drive หลังติดตั้ง EXE: production backup16/restore16

ใช้ Core SHA256 EDD26B928208655A887097387E4C28CC1E7B44AC50A76509F5D45BBB3640245D ที่คัดลอกจากแอปติดตั้งจริง เรียก GoogleDriveBackupStore.CreateAsync public factory และ ApplicationService.CreateProductionEngine ผ่าน reflection ตรวจ policy backup16/restore16, Google slots16 และ optimized=true โดยไม่เปลี่ยนค่าพวกนี้ แทนเฉพาะ HTTP transport ของ isolated session เพื่อวัด request/body

ใช้ repository Google Drive สังเคราะห์ใหม่หนึ่งแห่ง ข้อมูลเดิม 268,468,264 bytes รวม4ไฟล์ สำรองเสร็จและโปรเซสออกก่อนเริ่ม restore ในโปรเซสใหม่และโฟลเดอร์ใหม่ ตรวจ snapshot identity และ SHA-256/ขนาดตรงกันครบ4/4 ไม่อ่านไฟล์ต้นทางจริงผู้ใช้

| งาน | เวลา s | Plaintext MB/s | Mean WS MB | Mean private MB | Peak WS MB | Peak private MB | PUT/serialize สูงสุด | GET/body สูงสุด | Requests |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| backup | 34.282 | 7.831 | 229.49 | 193.55 | 346.68 | 307.29 | 14/11 | 1/1 | 343 |
| restore | 17.583 | 15.269 | 142.41 | 102.09 | 236.98 | 203.65 | 0/0 | 16/14 | 136 |

Transport errors0; HTTP4xx/5xx0; active request/body counters จบ0 ทุก phase รายละเอียด payload raw bytes, status, stream events, memory.csv และ lifecycle อยู่ในไฟล์แยก

RAM วัดแยกแต่ละโปรเซสทุก250ms; MB=1,000,000bytes อาจพลาด peak สั้น ค่า OS peakรวมstartupอยู่ในJSON เป็น RAM ของ harness ที่โหลด installed Core ไม่ใช่ Worker.exe หรือ RAM รวมแอป Trayวัดแยกใน environment-processes-before.json

เวลาไม่รวม startup, โหลด descriptor/key, public Google factory ตรวจ folder/OAuth ตอนเริ่ม, สร้าง engine, hash DLL, บันทึกผลวัด และ hashไฟล์หลังrestore เวลาuploadรวม BackupAsync และ backup-result JSON; restoreรวม ListSnapshotsAsync และ RestoreAsync

นับ GETตั้งแต่ส่งจนEOF/dispose; bodyนับหลังheaders PUTนับตั้งแต่ส่งจนresponse headers และแยกserialize body ค่า16เป็นเพดาน; stripesและจังหวะเครือข่ายอาจทำให้ค่าจริงต่ำกว่า16

ตรวจ configuration hashก่อน/หลังตรงกัน binaryติดตั้ง 780 ไฟล์ตรงกัน และ Task Scheduler XMLตรงกัน เวลา21:00เดิม อ่าน OAuth ในหน่วยความจำเท่านั้น descriptorตัดOAuthก่อนบันทึก MonitorตรวจWorkerจริงทุก250msและจะหยุดเฉพาะsyntheticถ้างานจริงเริ่ม

ข้อจำกัด: หนึ่งรอบบนเครือข่ายจริงสำหรับ fixtureนี้ ไม่อนุมานระดับTB ไม่รับประกันRAM/ความเร็ว ไม่มี production backup/prune/restore, config/schedule writes, install, commit หรือpush ในการทดลองนี้

การติดตั้งก่อนทดสอบ: ใช้ไฟล์ Release PCAutobackup-win-x64.exe SHA-256 580F84105E0AAF06845D71CAE642B1EDA1F3FF1FBBDCB5FB069C2BC3EC7473A6 เรียก --silent และเปิดหน้าตั้งค่าอัตโนมัติสำเร็จ ทับแอปที่ติดตั้งอยู่ (ซึ่งได้อัปเดตเป็น 16/16 ในงานก่อนหน้าแล้ว การทดสอบนี้จึงเป็น reinstall/repair ของรุ่นเดียวกัน ไม่ใช่การย้อนกลับไปติดตั้งรุ่นเก่าแล้ว upgrade) Desktop/Worker/Tray Core ตรงกับ payload ทั้งสามสำเนา configuration SHA-256, startup Run key และ scheduler XML ก่อน/หลังตรงกัน ปิดหน้าตั้งค่าตามปกติแล้ว process ออก Tray ยังทำงาน วัด idle Working Set ได้ 36.28 MB หนึ่งตัวอย่าง ไม่มีการเปลี่ยนตารางเวลา

การวัด Google Drive ใช้ harness ที่โหลดสำเนา Core จากแอปติดตั้งจริง พร้อมเรียก factory production ตามรายงาน ไม่ใช่การวัด installed Worker.exe โดยตรง คลังจำลองและผลดิบเก็บใน artifacts/installer-sixteen-verification/ (ignored) ผลดิบไม่รวมใน Git; เผยแพร่เฉพาะรายงานและสรุปผลใน GitHub Release

# Project context — PC AutoBackup

งานทดลองหลังรุ่น Resume: `BackupEngine` เพิ่ม constructor parameter `maxConcurrentChunks` (1–2, default 1) พร้อม buffer reuse, dedupe ภายในรอบ, ยกเลิกและรอทุกงานก่อนออกเมื่อเกิดข้อผิดพลาด และรักษาสาเหตุผิดพลาดเดิม ส่วน internal `GoogleDriveBackupStore` เพิ่ม `optimizeChunkUploads=false`, `maxConcurrentMutations=1`; เปิดสองตัวเลือกเฉพาะ benchmark เพื่อส่ง chunk ไม่เกิน 4 MiB+32 ใน request เดียวและทำสอง chunk mutations พร้อมกัน ใช้ name-lock แบบ bounded stripes, delete/manifest mutation เป็น exclusive และคง fresh conflict checks ก่อน/หลัง upload ค่าใช้จริงของแอปยังไม่เปิดตัวเลือกนี้ ไม่มีการติดตั้ง/เปลี่ยน production หรือตาราง 21:00 ในงานทดลอง ชุดทดสอบผ่าน 118 tests และ build solution ผ่าน 0 warnings/errors; benchmark ใช้ไฟล์จำลองแยกคลังและเทียบ ABBA พร้อมกู้คืนด้วย Core รุ่นเดิม

เอกสารนี้ชื่อ `Contex.md` ตามที่ผู้ใช้ร้องขอ ใช้ส่งต่อบริบทระหว่างงานพัฒนา อัปเดตเมื่อพฤติกรรมจริงเปลี่ยน

คู่มือสำหรับผู้ติดตั้งใหม่อยู่ที่ `docs/installation-guide-th.md` พร้อมฉบับ HTML แบบไฟล์เดียว `docs/installation-guide-th.html` สำหรับเปิดในเบราว์เซอร์ ส่งต่อ หรือพิมพ์เป็น PDF ครอบคลุม Release รุ่น Resume, OAuth ของผู้ใช้เอง, Drive, หลายโฟลเดอร์, กุญแจ `.pcakey`/client JSON, ทดสอบกู้คืน และเปิดตารางวันละครั้ง 21:00 โดยแยกค่าเริ่มต้นเวลาออกจากการเปิดใช้งาน schedule ครั้งแรกอย่างชัดเจน ฉบับ HTML สร้างจาก Markdown ด้วย PowerShell `ConvertFrom-Markdown`; เมื่อแก้คู่มือให้สร้าง HTML ให้ตรงกัน ไม่ได้เปลี่ยนแอปหรือตารางของผู้ใช้

## เป้าหมายและข้อตกลง

- ผู้ใช้ต้องการสำรองคอม Windows ไปยัง Google Drive และกู้คืนบนคอมใหม่ให้กลับมาทำงานต่อได้ เน้น RAM ต่ำ
- ปลายทางปรับได้: Google Drive (default), External HDD/SSD, local folder
- Google account ที่ OAuth ยืนยันและอนุญาต Drive คือบัญชีปลายทาง ไม่ใช้ช่องรับรหัสผ่าน Google ในแอป
- Default retention 7 วัน ผู้ใช้กำหนดจำนวนวันและเพดาน GB เอง; 0 วัน = latest-only; ไม่กำหนดโควตาเป็นค่าเริ่มต้น
- สำรองวันละครั้งเวลา 21:00 เป็นค่าเริ่มต้น รองรับ 1–24 เวลาต่อวันใน timezone เครื่อง ผู้ใช้ต้องเปิด schedule เองหลังตั้งค่าพร้อม
- Repository เริ่มจากว่างที่ `sanchatmd-dev/PCAutobackup`; ใช้ checkout ใน workspace ปัจจุบัน
- Root = Astra high; ทีมย่อยจำกัดตาม `AGENTS.md` รอบเริ่มต้นใช้ Terra high สำหรับ engine/review และ Sol high สำหรับ Google/native UI

## สถาปัตยกรรมที่เลือก

รองรับภาษาไทย/English ผ่าน `AppConfiguration.Language` (`th` default; config เก่าหรือค่าไม่รู้จักกลับเป็นไทย) และ embedded `Localization/English.json` ใน Core. Desktop ใช้ dynamic resources กับ formatter สำหรับข้อความสถานะ/วันที่ เปลี่ยนภาษาบันทึกผ่าน SaveConfigurationAsync โดยไม่ลงทะเบียนตารางใหม่ Tray อ่านภาษาจาก config เมื่อเปิดเมนู ไม่มี polling เพิ่ม แปล diagnostics เฉพาะ template/prefix ของแอป ไม่แก้ filename/path หรือเนื้อหาสำรอง

เลือก .NET 10 / WPF เพราะเป็น native Windows UI โดยไม่มี Chromium resident กระบวนการ Desktop แยกจาก Worker และ Tray ใช้ Core library ร่วมกัน Scheduler เปิด worker แล้ว worker ออกเมื่อสำรองเสร็จ ส่วน Tray ใช้ Win32 message loop ไม่มี polling ใช้ RAM ต่ำและไม่มี WPF อยู่ใน process นั้น ผู้ใช้ขอ tray/autostart เพิ่มภายหลัง จึงแทนที่เป้าหมายเดิมที่ไม่มี process ค้าง

`AppInstance` ใช้ named mutex/event ต่อ config ให้เปิดหน้าตั้งค่าเดิมเมื่อเปิดซ้ำและยึด abandoned mutex ได้หลัง process ถูก kill การกด X/minimize เมื่อว่างทำให้ WPF ออก เมื่อมีงานอยู่จะซ่อนแล้วทำงานต่อจนเสร็จ เปิดซ้ำก่อนเสร็จเรียกหน้าต่างเดิมกลับมา `ApplicationService.GetLastRunAsync` เปลี่ยน Running เป็น Interrupted เฉพาะเมื่อยึด operation lock ได้จริง จึงไม่สับสนกับ Worker ที่ยังทำงานอยู่ `DesktopRuntime` ตรวจ due/interrupted/failed attempts ที่ใหม่กว่างานสำเร็จเมื่อเปิด UI/Tray แล้วเรียก Worker ถ้าเปิด schedule ไว้ ไม่ retry การยกเลิกโดยผู้ใช้ก่อนรอบถัดไป

`Install.cmd` เรียก PowerShell installer แบบ current-user ไปยัง `%LOCALAPPDATA%\Programs\PCAutobackup` สร้าง Desktop/Start Menu shortcuts พร้อม cloud icon และ HKCU Run สำหรับ Tray เมื่อ logon อัปเดต task path ด้วย Worker `--refresh-schedule` และไม่เขียน config/credentials ตัวติดตั้งรอ WinExe ด้วย Process.WaitForExit ผ่าน Start-Process -Wait ไม่ใช้ LASTEXITCODE ของ PowerShell ซึ่งไม่รอ GUI executable การออก tray หรือ kill ไม่ปิด task และไม่มี watchdog ฝืนเปิดตัวเองทันที

`BackupSchedule` ตรวจรายการเวลา 1–24 ค่าที่ไม่ซ้ำ และหา latest due occurrence ใน timezone เครื่อง รองรับข้ามวันและ DST การตั้งค่า `DailyTimes = null` ใช้ `DailyTime` เก่าเพื่อรักษาเวลาเดิม UI บันทึก `DailyTimes` ที่เรียงแล้วและคง `DailyTime` เป็นค่าแรกเพื่อ compatibility งานอัตโนมัติเทียบ StartedUtc ของงานสำเร็จ ไม่ใช้ CompletedUtc เพื่อไม่ข้ามรอบที่มาถึงระหว่างงานก่อนหน้ากำลังทำอยู่

Windows task มี calendar trigger ต่อเวลา, logon, session unlock และ Power-Troubleshooter resume event (หน่วง 1 นาที) พร้อม StartWhenAvailable, ไม่ปลุกเครื่อง, InteractiveToken ของผู้ใช้เดิม และ retry 5 นาที 3 ครั้ง งานตกค้างหลายรอบรวมเป็นการสำรองปัจจุบันครั้งเดียว ไม่เก็บรหัสผ่าน Windows ใช้ TimeProvider ใน service/engine เพื่อทดสอบเวลาจำลองโดยไม่เปลี่ยนนาฬิกาเครื่อง

`ApplicationService` เป็น facade ให้ UI และ worker ใช้: configuration persistence, recovery-key management, operation locking, Google integration, engine, schedule และสถานะงาน

Config ใช้ Revision เพื่อตรวจ stale writer และ lock ข้าม process ต่อ config; backup โหลดใหม่หลังได้ lock ส่วน local repository ใช้ FileShare.None lease ในคลัง Google ไม่มี distributed lease ข้ามเครื่อง การนำเข้ากุญแจอีกคลังใช้ session-only recovery key หากมี active key อยู่แล้วเพื่อไม่ทำให้งานสำรองเดิมเสีย

`IBackupStore` มี Exists/OpenRead/Put/Delete/List เพื่อใช้ engine เดียวกับ local และ Google Drive Put ต้องไม่เผยแพร่วัตถุที่ยังเขียนไม่ครบ ข้อมูลมี three namespaces: `repository/owner`, `chunks/`, `snapshots/`

`BackupEngine`:

1. ตรวจและถอดรหัสข้อมูลเก่าก่อนแก้ไข ยืนยันเจ้าของเครื่อง
2. เดินต้นทางโดยไม่ตาม reparse points อ่านทีละ 4 MiB และสร้าง keyed hash เพื่อ dedupe
3. เก็บ chunk ที่เข้ารหัสและยืนยัน manifest ใหม่เมื่ออ่าน/เขียนครบทั้งหมด
4. หาก quota ของชุดใหม่ไม่ผ่าน ยกเลิกก่อน commit และรักษาชุดก่อนหน้า
5. หลัง commit ค่อย prune manifest เก่าตามอายุ/โควตา และ GC chunk ที่ไม่มีใครอ้างถึง

ใช้ AES-GCM พร้อม nonce ต่อวัตถุและ authenticated purpose; derive encryption/naming keys จากกุญแจ 32 bytes ด้วย HMAC กุญแจส่งออกใช้ `PCA1-<base64>`; local recovery key และ tokens ใช้ DPAPI CurrentUser ไม่มี escrow หรือ server-side key recovery

Restore ไม่เขียนทับไฟล์เดิม ตรวจ relative path, reserved names, traversal และ reparse points โฟลเดอร์ต้นทางที่กู้คืนมี label + stable hash ของ Source.Id เพื่อไม่สลับชื่อเมื่อ user reorder source

## Google

- HTTP OAuth loopback listener, PKCE/state, system browser, verified email/sub และ Drive scope validation
- `drive.file` จำกัดการเข้าถึงแอป ส่งผ่าน resumable upload; actual cloud tests ต้องมี OAuth client ของผู้ใช้
- Root `PC AutoBackup` + marked device child repository; recoveryเลือกคลังเป็น read-only session selection ไม่เปลี่ยน active backup destination
- Recoveryบนเครื่องใหม่ต้อง account และ OAuth application/project เดิม เพราะยังไม่มี Drive Picker สำหรับขอสิทธิ์จาก project ใหม่
- เครือข่ายผิดพลาด/ข้อมูลซ้ำ/สิทธิ์หมดอายุแสดง error; ไม่แสร้งว่า upload สำเร็จ

## ขอบเขตการกู้คืนจริง

รุ่นนี้ทำ encrypted file backup/restore และ installed-program inventory การคืน Windows/ไดรเวอร์/บัญชี/การติดตั้งโปรแกรมและ application settings ทุกชนิดอัตโนมัติยังไม่รองรับ ต้องไม่สื่อสารว่าเป็น disk clone หรือเหมือนเครื่องเดิม 100% ข้าม OS

ไม่มี VSS, compression, live file watcher, full integrity scrub หรือ multi-writer coordination ข้ามเครื่อง ช่วงปิดหน้าตั้งค่าเหลือ Tray ข้อมูล metadata ขยายตามจำนวนไฟล์/ประวัติและ active manifest มีขนาดจำกัด 256 MiB งานกู้คืนที่ถูก kill ต้องเริ่มใหม่ในโฟลเดอร์ว่าง ไม่รองรับ resume restore บางส่วน

## Validation และรายการก่อนส่งมอบ

- Tests ใช้ synthetic temp directories เท่านั้น รวม round-trip, deletions, retention, quota, corruption, wrong key, path safety, failure/cancel, Google mocked transport, config และ recovery facade
- Build Desktop/Worker/Tray Release ผ่าน และ automated tests 83 ข้อผ่าน รวม retention/quota, หลายเวลาต่อวัน, catch-up, interrupted job, live lock และ single-instance recovery
- Native UI ตรวจหน้าต่าง package จริง และแก้ปัญหาสีตัวอักษรปุ่มที่พบจาก screenshot แล้ว
- Synthetic 128 MiB worker backup/restore ผ่านและ SHA-256 ตรงกัน; peak working set 75.5 MiB รอบแรก / 44.6 MiB รอบไม่เปลี่ยนข้อมูล ไม่มีการรับประกันเพดาน RAM จากชุดทดสอบนี้
- ลงทะเบียนและสั่ง temporary Task Scheduler task สำรอง synthetic data สำเร็จ แล้วลบ task ทดสอบออก ไม่ได้เปิด schedule ของผู้ใช้จริง
- Live Google OAuth/Drive ทดสอบแล้วด้วย Desktop client และบัญชีที่ผู้ใช้เลือกและอนุญาตด้วยตนเอง: สำรองและกู้คืน 4 ไฟล์จำลอง รวม 5,243,014 bytes แล้วเทียบขนาดและ SHA-256 ตรงทุกไฟล์
- Packaged Worker สำรองไฟล์จำลองเดิมไป Google Drive ใช้ 9.23 วินาที peak working set 67.7 MiB และจบ process; ระหว่างการทดลองแรก config เลือกเฉพาะโฟลเดอร์จำลองและปิด schedule
- แก้ progress callback ที่มาช้าทับข้อความกู้คืนสำเร็จ โดยหยุดรับ progress ก่อนข้อความผลลัพธ์และตรวจว่า callback เป็นของงานปัจจุบัน
- ทดสอบ package หลังแก้ด้วยการนำเข้าไฟล์กุญแจที่ส่งออก แล้วกู้คืนจาก Drive อีกครั้งไปโฟลเดอร์ว่าง: จำนวนไฟล์ ขนาด และ SHA-256 ตรงทั้งหมด พร้อมยืนยันว่าข้อความกู้คืนสำเร็จไม่ถูก progress ทับ
- เมื่อ 7 พฤศจิกายน 2026 ผู้ใช้อนุมัติหน้าแอป/นโยบาย/เงื่อนไขบน GitHub Pages และยืนยัน Push to production ใน Google Cloud แล้ว ตรวจหน้าจอพบ In production และผู้ใช้ลงชื่อเข้าใช้อีกครั้งด้วยบัญชีเดิมสำเร็จ ไม่ได้ขยาย OAuth scopes การเปิด Production ไม่เท่ากับผ่าน brand verification และไม่รับประกันว่า token จะไม่มีวันถูกเพิกถอน ดู `docs/google-setup.md`
- ผู้ใช้เลือกโฟลเดอร์ของตนเองสำหรับสำรองไป Google Drive และอนุญาตตาราง 21:00 ทุกวัน ยืนยัน config และ Windows Task Scheduler: Enabled, Ready, Daily trigger 1 วัน, Next Run Time 7 พฤศจิกายน 2026 21:00 +07:00, Interactive user logon; retention 7 วัน, ไม่กำหนดเพดานพื้นที่, application inventory ปิด ไม่บันทึกพาธส่วนตัวหรือ credentials ลง repo
- Worker สำรองต้นทางที่ผู้ใช้อนุญาตหลังเชื่อม Production สำเร็จ 24 ไฟล์ / 31,110,907 bytes ใช้ 106.95 วินาที peak working set 84.1 MiB และ exit code 0 เปิดแอปตรวจผลสำเร็จแล้วปิดเพื่อประหยัด RAM ยังไม่ได้เฝ้ารอบตามเวลาจริง 21:00 และไม่ได้กู้คืน 24 ไฟล์นี้ซ้ำในการตั้งค่ารอบนี้
- กุญแจที่ส่งออกระหว่างการทดลองยังอยู่บน PC ต้องเก็บสำเนานอกเครื่องสำหรับ disaster recovery
- Build package self-contained ใน `artifacts/` (ignored); task path ต้องคงที่หลังลงทะเบียน
- ดูผลล่าสุดและข้อจำกัดการทดสอบเพิ่มเติมใน `docs/validation.md` เมื่อสร้างแล้ว

## การตรวจเพิ่มเติมและอัปเดตตาราง — 7 พฤศจิกายน 2026

- ทดสอบตามคำขอผู้ใช้: กู้คืนข้อมูลจริง 24 ไฟล์จาก Drive ตรง SHA-256 ทุกไฟล์; หลังเพิ่ม/ลบ/เปลี่ยนชื่อ สำรองและกู้คืนครบ 23 ไฟล์ตรงทั้งหมด ไม่บันทึกรายชื่อส่วนตัวลง repo
- ทดสอบ incremental จริง: ไฟล์จำลอง 12 MiB แก้ 1 byte ใน chunk กลาง อัปโหลดใหม่ 1 จาก 3 chunks และกู้คืนก่อน/หลังแก้ไขตรง SHA-256 ทั้งสองชุด คืนต้นฉบับหลังทดสอบแล้ว
- Synthetic tests จำลองชุดอายุ 8 วัน, 7 วันพอดี, 3 วัน และล่าสุด: ลบเฉพาะชุดหมดอายุและ chunk ที่ไม่ถูกอ้างถึง; เก็บ shared chunk และกู้คืนทุกชุดที่เหลือได้ ทดสอบ quota 20,000 bytes ล้างเก่าจนเหลือล่าสุดและ quota 1 byte ปฏิเสธชุดใหม่โดยไม่เสียชุดล่าสุด
- มีหลายโฟลเดอร์อยู่แล้ว ทดสอบชื่อโฟลเดอร์/ไฟล์ซ้ำกัน สลับลำดับและลบไฟล์ในโฟลเดอร์หนึ่ง ไม่กระทบอีกโฟลเดอร์
- Native UI บันทึกสามเวลาลง task จำลองสำเร็จ และ Worker เริ่มอัตโนมัติจริงสองรอบเวลา 19:13 และ 19:14 ขณะปิดแอป สำรองสองโฟลเดอร์จำลองสำเร็จ exit 0 ทั้งคู่
- ทดสอบรอบตกค้างผ่าน Task Scheduler บน config จำลอง ได้ชุดใหม่หนึ่งชุดและเรียกซ้ำแล้วข้าม กู้คืนทั้งสามชุดได้สองโฟลเดอร์ตรง SHA-256 แล้วลบ task จำลองออก
- ลงทะเบียน task ผู้ใช้ใหม่ด้วย trigger หลัง logon/unlock/resume โดยคง 21:00 เพียงเวลาเดียว retention 7 วันและโควตาเดิม ไม่ได้สั่งเครื่องผู้ใช้ Sleep/reboot เพื่อทดสอบฮาร์ดแวร์จริง

## งานต่อยอด

8 พฤศจิกายน 2026: การทดสอบจริงที่ผู้ใช้อนุญาตมี 78 ไฟล์ รวม 28,397,892,025 bytes (ไฟล์ใหญ่ที่สุด 8,312,601,592 bytes). รอบแรกพบ Google HTTP 410 หลังอัปโหลดใหม่ประมาณ 11.30 GB; Worker ล้าง staged chunks และจบ Failed โดยเก็บชุดสำเร็จก่อนหน้าไว้ จึงยังไม่ยืนยัน round-trip ของชุดใหญ่. RAM peak 108,068,864 bytes จาก OS; การวัดรวมช่วง rollback ต้องแยกจากความเร็ว upload. แก้ `GoogleDriveBackupStore` ให้เปิด session ใหม่ได้สูงสุดสองครั้งเมื่อ data/probe ตอบ 404/410: ใช้ uploadId เดิม, ตรวจชื่อ/เจ้าของวัตถุ, ตรวจ SHA-256 หากวัตถุอาจบันทึกครบแล้ว, ใช้ PATCH กับ placeholder ของตนเอง และไม่เขียนทับของ writer อื่น. ไม่ retry การสร้าง session ที่มีผลลัพธ์คลุมเครือโดยพลการ. ผ่าน 92 tests รวมกรณี writer อื่นเปลี่ยน uploadId บน file ID เดียวกัน และ publish ครบ Desktop/Worker/Tray. Live retry สำรองครบ 78 ไฟล์ / 28,397,892,025 bytes ใช้เวลา 6 ชม. 46 นาที จากนั้นกู้คืนลงโฟลเดอร์ใหม่ใน Documents ใช้เวลา 3 ชม. 41 นาที. SHA-256 ไฟล์กู้คืนและต้นฉบับตรง baseline เดิมครบทั้ง 78 ไฟล์; ชุดไฟล์ต้นฉบับและ semantic settings ไม่เปลี่ยน. RAM working set เฉลี่ย/สูงสุด: Backup Worker 48.56/141.96 MB, Core restore helper 64.91/138.62 MB (ฐานสิบ ไม่รวม Tray/UI). ความเร็วเฉลี่ย upload 1.14 MB/s, restore 2.14 MB/s. รอบรายชั่วโมงที่ชนงาน 14 รอบคืน exit 0; ถอน task ทดสอบแล้ว คงงานประจำ 21:00. ผลละเอียดอยู่ใน docs/validation.md; ไม่ใส่ชื่อไฟล์ส่วนตัวหรือ hash records ลง Git.

แก้รอบอัตโนมัติชนงานเดิม: `OperationLock.TryAcquire` คืน busy เฉพาะ Windows sharing/lock violation (32/33) ส่วนข้อผิดพลาดดิสก์และสิทธิ์ยังส่งต่อ ไม่จับ IOException ทั้งหมดเป็น skipped. Scheduled Worker ที่ชน config lock คืน null/exit 0 และเขียนสถานะ `Skipped` พร้อมข้อความ “ข้ามเพราะมีงานกำลังทำอยู่” ลงไฟล์ `.scheduled-skip.json` แยกจาก `.status.json` และ `.success.json`. หน้า Overview แสดงรอบที่ข้ามล่าสุดพร้อมเวลา มีคำแปลอังกฤษ. Manual backup ยังแจ้ง busy เป็นข้อผิดพลาด. Tests ตรวจ config/status/success ไม่เปลี่ยนระหว่างข้าม ไม่มี repository ใหม่ และรอบหลังปล่อย lock สำรอง/กู้คืนตามเดิมได้ รวมทั้ง lock path permission error ไม่ถูกกลืนเป็น busy.

ตัวติดตั้งไฟล์เดียวอยู่ใน `src/PCAutobackup.Installer` สร้างแยกหลัง publish app ผ่าน `scripts/build-installer.ps1` (ไม่อยู่ใน solution test เพราะต้องมี embedded payload ที่สร้างแล้ว) เป็น WPF bootstrapper self-contained/single-file สำหรับ Windows x64 แตก payload ลง temporary GUID directory แล้วเรียก installer เดิมแบบ hidden, สร้าง shortcuts/startup และเปิด Desktop เมื่อสำเร็จ รองรับ `--silent --no-launch` สำหรับ automation; payload ไม่มี personal config/credentials; installer ยัง unsigned ไม่ได้เป็น MSI และยังไม่มี uninstall UI

อัปเดต 7 พฤศจิกายน 2026: cloud icon, Tray, per-user installer, Desktop shortcut และ startup ติดตั้งจริงแล้ว ตารางยังเป็น 21:00/retention 7 วันและ config hash เดิม ทดสอบ kill Worker ระหว่างไฟล์จำลอง 256 MiB แล้วเปิด Tray ให้ retry กู้คืน SHA-256 ตรง; ทดสอบ X ระหว่างงาน 4 GiB ให้ทำต่อและเปิดหน้าต่างเดิมกลับมา; kill ทั้ง Desktop/Tray แล้วเปิดใหม่ปกติ RAM ของ Tray ที่ติดตั้งจริงเมื่อพัก 38.01 MiB working set / 9.96 MiB private ไม่ได้ reboot เครื่องผู้ใช้จริง ผลละเอียดใน `docs/validation.md`

เว็บไซต์ข้อมูลแอปอยู่ใน `site/` และเผยแพร่เฉพาะไดเรกทอรีนี้ด้วย `.github/workflows/pages.yml` ที่ https://sanchatmd-dev.github.io/PCAutobackup/ ไม่เผยแพร่คลังสำรอง การตั้งค่าส่วนตัว หรือ artifacts จากเครื่องผู้ใช้

- VSS/app-aware backup สำหรับฐานข้อมูลและไฟล์ที่ล็อก
- Metadata streaming/index บนดิสก์สำหรับคลังไฟล์จำนวนมหาศาล, compression และ full integrity verify
- Restore mapping Documents/Desktop ให้ผู้ใช้ใหม่ พร้อม adapters สำหรับ settings ที่ตรวจสอบเป็นรายแอป
- Assisted reinstall จากรายการแอปที่รองรับ, signed installer/update mechanism
- Google Picker สำหรับคลังที่สร้างโดย OAuth application อื่น และ fault-injection/live integration บนบัญชีทดสอบ
- ภายหลังจึงพิจารณา multiple destinations / cross-platform file recovery โดยยังรักษาขอบเขต compatibility ให้ชัดเจน

อัปเดต Resume: ใช้ encrypted content-addressed chunks เป็น durable checkpoint โดยไม่สร้าง journal เพิ่ม ยกเลิก rollback ที่ลบ staged chunks เมื่อ backup ล้มเหลว/ยกเลิก; รอบใหม่อ่าน source ใหม่และตรวจ orphan ด้วย decrypt/hash/เทียบ bytes ก่อน reuse. ซ่อม orphan เสียหายได้ แต่ไม่เขียนทับ chunks ที่ committed manifest อ้างถึง. Prune orphan หลัง commit เท่านั้น; quota failure เก็บ checkpoint และ snapshot ล่าสุดไว้. ไม่มี cross-process byte/session resume และไม่มี watchdog. การกดยกเลิกยังไม่บังคับ retry ทันที. Automated suite 97 tests ผ่าน รวม lost acknowledgment, cancellation, quota failure, changed source ที่ length/mtime เดิม และ corrupted orphan.

Live Resume ผ่านบน Google Drive แยกด้วยข้อมูลจำลอง 167,772,204 bytes: kill ระหว่าง upload ใช้สอง chunks เดิมต่อ; จำลอง network loss ใช้ 35 chunks เดิมต่อโดยไม่ Put ซ้ำ. กู้คืน SHA-256 ตรงครบ. Peak working set backup118.06MB/restore135.18MB วัดใน headless Core harness ไม่ใช่ installed Worker; ไม่ได้ดับเครื่องจริง. Production config hash เดิม; ตาราง21:00. ดู docs/validation.md.

ระหว่าง benchmark พบคิวทดลองใช้ Dictionary.First() ซึ่งหลัง Remove/Add จะใช้ช่องเก่า ทำให้รอ task ใหม่ซ้ำและปล่อย task ที่เสร็จแล้วค้างในรายการจนเสียความขนาน แก้ DrainOne เป็น Task.WhenAny แล้วคืน buffer ของ task ที่เสร็จจริง เพิ่ม regression 6 unique chunks ที่บังคับให้ chunk 3 รอ chunk 4 จึงจับการกลับไป serial หลังคู่แรกได้ ผล candidate ก่อนแก้เก็บเป็น diagnostic และไม่ใช้สรุปประสิทธิภาพรุ่นสุดท้าย รุ่นแก้คิวยืนยันผ่าน Google Drive จริงว่ามี data PUT พร้อมกัน 2 คำขอ โดยไม่เปลี่ยน production.

ผล benchmark รุ่นแก้คิว: ABBA ข้อมูลจำลอง268,468,264bytes ใช้เวลาเฉลี่ย243.96→118.31s เร็วขึ้น2.06เท่า HTTP600→344 และ peak working setสูงสุด101.80→114.66MB กู้คืนด้วยCoreเดิม SHA-256ตรงครบทุกชุด การตั้งค่า ไฟล์ติดตั้ง780ไฟล์ และschedulerXMLเดิมทุกbyte คง21:00;ยังไม่deploy ดู docs/performance-2026-11-08.md.

Restore experiment: เพิ่ม maxConcurrentRestoreChunks=1 เป็น parameterท้ายconstructor รองรับ1–2; bounded Queue prefetch, ordered writes, cancel/drain preserve firstfailure. Release125testsผ่าน build0warnings/errors. Google Drive ABBA synthetic268468264bytes mean112.822→59.981s ลด46.84%; sampled peakWSรายรอบ baseline136.90/130.12MB vs candidate194.51/157.32MB. SHAครบ4ไฟล์ทุกรอบ; actualfull-bodyconcurrency1/2/2/1. production config/780binaries/schedulerXMLเดิมทุกbyte คง21:00 ไม่มีdeploy ดู docs/restore-performance-2026-11-08.md.

ทดลอง4ก้อน: ขยาย maxConcurrentChunks/maxConcurrentRestoreChunks/maxConcurrentMutations เป็น1–4 ค่าเริ่มต้นทั้งหมดยัง1 เฉพาะharnessเลือก2หรือ4 ไม่เปิดสองงานสำรองกับกู้คืนพร้อมกัน Testsใหม่ตรวจ sustained4, orderedrestore12chunks, failure/cancel drain4, same-name serialization และexclusivegateคืนpartialslots; Release137testsผ่าน solutionbuild0warnings/errors. ผลเปรียบเทียบใช้Coreเดียวกัน ABBA2/4/4/2 พร้อมอ่านชุด4ก้อนด้วยCoreเก่า2ก้อนเป็นcompatibilitycheck ไม่มีdeploy.

ผล4ก้อน: สำรอง119.522→69.333s กู้คืน75.085→40.611s เมื่อเทียบ2ก้อน ABBAชุดเดียวกัน RAM sampled peakสูงสุด4ก้อน160.33MB/245.39MB ตามลำดับ SHAครบและCoreเก่าอ่านได้ production/scheduleเดิม ยังไม่deploy ดู docs/four-chunk-performance-2026-11-08.md.

ทดลอง8ก้อน: constructor concurrency ทั้งbackup/restore/Drive รองรับ1–8 ค่าเริ่มต้นยัง1 เฉพาะharnessเปิด4/8 Testsเพิ่ม sustained24chunks, orderedrestoreSHA, failure/cancel drain8, same-name/exclusivegate/partialslots และปฏิเสธ9 แก้testfakeDriveค้นชื่อให้ตรงทั้งpredicateเพื่อไม่สับสนparallel-1กับparallel-10 Release147testsผ่าน solutionbuild0warnings/errors ไม่มีproductiondeployment.

ผล8ก้อน: ABBA4/8/8/4 ข้อมูลจำลอง268468264bytes meanbackup68.230→43.189s restore40.949→25.096s; sampledpeakWSสูงสุด8ก้อน264.88/275.80MB ตามลำดับ SHAครบและold4Coreอ่านnew8ได้. Config/780binaries/schedulerXMLเดิม คง21:00 ยังไม่deploy ดู docs/eight-chunk-performance-2026-11-08.md.

ทดลอง16ก้อน: constructor concurrencyทั้งbackup/restore/Drive รองรับ1–16 ค่าเริ่มต้นยัง1 เฉพาะharnessเลือก8/16 ใช้boundedbuffersและ64name-stripesเดิม Testsเพิ่มsustained48chunks, restoreSHA/out-of-order, failure/cancel drain16, same-name/exclusivegate/partialslots และปฏิเสธ17 Release157testsผ่าน solutionbuild0warnings/errors ไม่เปลี่ยนproduction/ตาราง.

ผล 16 ก้อน: ABBA 8/16/16/8 ใช้ข้อมูลจำลอง 268468264 bytes และ Core เดียวกัน เวลาเฉลี่ยสำรอง 44.517→31.712 s กู้คืน 26.280→17.750 s; sampled peak working set สูงสุด 16 ก้อน 366.78/325.19 MB ตามลำดับ SHA-256 ตรงทั้ง 4 ไฟล์ทุกชุด และ Core เดิม 8 ก้อนอ่าน snapshot ใหม่ได้ Configuration, 780 installed binaries และ scheduler XML ตรงก่อน/หลัง คง 21:00 ไม่มี deploy ดู docs/sixteen-chunk-performance-2026-11-08.md

ทดลอง 32 ก้อน: constructor concurrency ของ backup/restore/Drive รองรับ 1–32 ค่าเริ่มต้นยัง 1 ใช้ bounded buffers และ 64 name stripes เดิม เพิ่ม tests sustained 96 chunks, restore SHA-256/out-of-order, failure/cancel drain 32, same-name/exclusive gate/partial slots และปฏิเสธ 33 โดยคง tests 16 ก้อน Release ผ่าน 167 tests และ solution build ไม่มี warning/error เปิด 16/32 เฉพาะ harness ไม่เปลี่ยน production หรือตารางเวลา

ผล 32 ก้อน: ABBA 16/32/32/16 ใช้ข้อมูลจำลอง 268468264 bytes และ Core เดียวกัน เวลาเฉลี่ยสำรอง 32.283→30.276 s กู้คืน 18.235→15.680 s; sampled peak working set สูงสุด 16/32 ก้อนสำหรับสำรอง 346.32/449.25 MB และกู้คืน 407.86/405.66 MB SHA-256 ตรงทั้ง 4 ไฟล์ทุกชุด และ Core เดิม 16 ก้อนอ่าน snapshot ใหม่ได้ คำขอ PUT พร้อมกันสูงสุด 25 สำหรับค่า 32; GET ถึง 32 Configuration, 780 installed binaries และ scheduler XML ตรงก่อน/หลัง คง 21:00 ไม่มี deploy ดู docs/thirtytwo-chunk-performance-2026-11-08.md ผลความเร็วเริ่มเพิ่มน้อยลงและยังต้องพิจารณาความแปรปรวนของเครือข่าย/GC

ตัดสินใจเปิด production 16/16 ตามคำขอผู้ใช้: ApplicationService ใช้ backup concurrency 16 และ restore prefetch 16; GoogleDriveBackupStore.CreateAsync เปิด optimized chunk uploads และ mutation concurrency 16 Low-level constructor ยังเริ่ม 1 และรองรับถึง 32 เพื่อทดสอบ ค่า 32 ยังไม่ใช้ในแอปตามปกติ รายงาน benchmark ก่อนหน้านี้เป็นสถานะ ณ เวลาทดลองก่อนเปิด production การอัปเดตในเครื่องต้องคง configuration, กุญแจ, OAuth และตาราง 21:00 เดิม

ติดตั้ง production 16/16 ในเครื่องสำเร็จที่ %LOCALAPPDATA%/Programs/PCAutobackup เมื่อ 8 พฤศจิกายน 2026: Release 169 tests ผ่าน, build/publish ไม่มีข้อผิดพลาด, smoke จาก published Core ตรวจ policy 16/16 และ local synthetic SHA-256 ครบ 4/4 Desktop/Worker/Tray Core ทั้งสามสำเนาตรง SHA-256 EDD26B928208655A887097387E4C28CC1E7B44AC50A76509F5D45BBB3640245D Configuration hash ไม่เปลี่ยน ตารางเปิดอยู่และ next run 21:00 เดิม Worker --help เปิดได้ Tray ใหม่ทำงาน idle วัด Working Set 38.13 MB สำเนารุ่นเดิมสำหรับย้อนกลับอยู่ใน artifacts/production-sixteen-20261108/rollback ไม่มีการ commit/push หรือเปลี่ยน GitHub Release ในขั้นนี้

Release v2026.11.08-parallel16: เตรียมเผยแพร่ source และตัวติดตั้งไฟล์เดียว PCAutobackup-win-x64.exe พร้อม SHA256SUMS.txt และคู่มือ HTML รุ่น 16/16 ตารางเริ่มต้น 21:00 และตารางผู้ใช้เดิมคงเดิม ตัวเลือก 32 ยังใช้เฉพาะการทดลอง

ตรวจตัวติดตั้ง Release 16/16 ทับในเครื่อง: เปิดแอปหลังติดตั้งได้ config/startup/scheduler XML เดิมครบ คง21:00 ทดสอบ installed Core กับข้อมูลจำลอง Google Drive268468264bytes สำรอง34.282s กู้คืน17.583s SHA4/4 peakWS346.68/236.98MB errors0 ดู docs/installer-sixteen-verification-2026-11-08.md เป็น reinstallรุ่น16/16เดิม และใช้Coreharnessไม่ใช่Workerprocessโดยตรง

ตรวจรอบจริง21:00วันที่8พ.ย.2026สำเร็จ21:10:25 Taskresult0 installedCoreproduction16/16 snapshot46files22.26GB ใช้3405chunksเดิมและเพิ่ม1937chunks8.10GBciphertext peakWorkerWS530.89MB(sample526.65MB) private556.59MB ค่าตั้งเดิมยกเว้นOAuthrefresh/Revision รอบถัดไป9พ.ย.21:00 ดูdocs/scheduled21-verification-2026-11-08.md ไม่ได้traceHTTPconcurrencyหรือrestoreไฟล์ผู้ใช้

กู้คืนชุดจริงรอบ21:00โดยผู้ใช้กดWPFทีละขั้น ไปโฟลเดอร์ใหม่ในDocuments สำเร็จ46files22.26GB SHA256ตรงsourceก่อน/หลัง46/46 และchunkHMACตรงsnapshot ต้นฉบับคงเดิม ช่วงเขียนไฟล์ที่สังเกตได้13m54s≈26.67MB/s ไม่ใช่RestoreAsync stopwatch; DesktopprocesslifetimepeakWS383MB sampleช่วงเขียน370.34MB private439.73MB config/SchedulerXMLเดิมคง16/16เวลา21:00 ดูdocs/restore21-verification-2026-11-08.md ไม่มีcommit/pushในงานนี้

เพิ่ม progress telemetry: BackupProgress.TotalBytes nullable, backup pre-scan metadata streaming ไม่เก็บรายชื่อไฟล์ทั้งหมด, รายงานหลังchunk durableรวมdedup และrestoreหลังwritechunk UIแสดง%/processedMB/s/ETAด้วยStopwatch, cap99จนoperationสำเร็จ, latest-only8Hz queue1 ไม่มีresidenttimer คงproduction16/16และscheduleเดิม

Progress featureติดตั้งในเครื่องแล้ว Core91ECA1B972ECFD4CA13FAAFD37928C6890CFF3D7BBC1B4C58FBA0DD9BF727E3A Release178testsผ่าน buildไม่มีwarning/error SHA synthetic2/2 มีin-file31updatesต่อphase config/SchedulerXMLเดิม คง16/16และ21:00 ไม่มีcommit/pushหรืออัปเดตGitHubRelease

เตรียม Release v2026.11.08-progress: ตัวติดตั้งไฟล์เดียวแสดงเปอร์เซ็นต์ ความเร็วประมวลผล MB/s และ ETA ภาษาไทย/อังกฤษ คงproduction16/16และตาราง21:00 พร้อมแนบคู่มือHTMLและSHA256SUMS.txt

ทดสอบReleaseprogressEXEติดตั้งทับexit0และactualWPF localrandom2GiB3files สำรอง/กู้คืนสำเร็จ SHA3/3 generation/source/restoredตรง UIระหว่างงานเห็น%/MBs/ETAและจบ100%ETA0 ค่าปัดเศษสอดคล้อง RAMactualDesktop sampledpeakWS260.14/566.29MB, processpeak626.27ตอนrestore(ใช้processเดียวกัน) config/startup/SchedulerXMLเดิม16/16เวลา21:00 ดูdocs/progress-installer-verification-2026-11-08.md ไม่ได้commit/pushในงานนี้

ตรวจactualWPF GoogleDriveprogress512MiB3filesผ่าน SHA3/3 เห็น77%13.74MB/sETA10และจบ100%ETA0 คืนจากDriveสำเร็จด้วย เปิดUIหลังพักRAMลดแต่privateไม่กลับbaseline(ก่อน71.57MB หลังbackup77s248.32/restore85s157.30) ปิดUIprocessออกเหลือmainTrayprivate8.63MBเท่าก่อน WS1.38MB ไม่บังคับGC/trim คงconfig/taskXML16/16เวลา21:00 ดูdocs/drive-progress-idle-verification-2026-11-08.md

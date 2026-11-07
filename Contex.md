# Project context — PC AutoBackup

เอกสารนี้ชื่อ `Contex.md` ตามที่ผู้ใช้ร้องขอ ใช้ส่งต่อบริบทระหว่างงานพัฒนา อัปเดตเมื่อพฤติกรรมจริงเปลี่ยน

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

ตัวติดตั้งไฟล์เดียวอยู่ใน `src/PCAutobackup.Installer` สร้างแยกหลัง publish app ผ่าน `scripts/build-installer.ps1` (ไม่อยู่ใน solution test เพราะต้องมี embedded payload ที่สร้างแล้ว) เป็น WPF bootstrapper self-contained/single-file สำหรับ Windows x64 แตก payload ลง temporary GUID directory แล้วเรียก installer เดิมแบบ hidden, สร้าง shortcuts/startup และเปิด Desktop เมื่อสำเร็จ รองรับ `--silent --no-launch` สำหรับ automation; payload ไม่มี personal config/credentials; installer ยัง unsigned ไม่ได้เป็น MSI และยังไม่มี uninstall UI

อัปเดต 7 พฤศจิกายน 2026: cloud icon, Tray, per-user installer, Desktop shortcut และ startup ติดตั้งจริงแล้ว ตารางยังเป็น 21:00/retention 7 วันและ config hash เดิม ทดสอบ kill Worker ระหว่างไฟล์จำลอง 256 MiB แล้วเปิด Tray ให้ retry กู้คืน SHA-256 ตรง; ทดสอบ X ระหว่างงาน 4 GiB ให้ทำต่อและเปิดหน้าต่างเดิมกลับมา; kill ทั้ง Desktop/Tray แล้วเปิดใหม่ปกติ RAM ของ Tray ที่ติดตั้งจริงเมื่อพัก 38.01 MiB working set / 9.96 MiB private ไม่ได้ reboot เครื่องผู้ใช้จริง ผลละเอียดใน `docs/validation.md`

เว็บไซต์ข้อมูลแอปอยู่ใน `site/` และเผยแพร่เฉพาะไดเรกทอรีนี้ด้วย `.github/workflows/pages.yml` ที่ https://sanchatmd-dev.github.io/PCAutobackup/ ไม่เผยแพร่คลังสำรอง การตั้งค่าส่วนตัว หรือ artifacts จากเครื่องผู้ใช้

- VSS/app-aware backup สำหรับฐานข้อมูลและไฟล์ที่ล็อก
- Metadata streaming/index บนดิสก์สำหรับคลังไฟล์จำนวนมหาศาล, compression และ full integrity verify
- Restore mapping Documents/Desktop ให้ผู้ใช้ใหม่ พร้อม adapters สำหรับ settings ที่ตรวจสอบเป็นรายแอป
- Assisted reinstall จากรายการแอปที่รองรับ, signed installer/update mechanism
- Google Picker สำหรับคลังที่สร้างโดย OAuth application อื่น และ fault-injection/live integration บนบัญชีทดสอบ
- ภายหลังจึงพิจารณา multiple destinations / cross-platform file recovery โดยยังรักษาขอบเขต compatibility ให้ชัดเจน

## Audit ทั้งโปรเจกต์และแก้บั๊กที่ยืนยันแล้ว (2026-11-11)

จุดอ้างอิง main @ 7c27c05 (Release `v2026.11.11-small-files`) การแก้ทั้งหมดอยู่ใน working tree ยังไม่ commit ไม่ push ไม่ติดตั้ง และไม่ได้ออก Release ใหม่ ใช้เฉพาะข้อมูลจำลองใน temp และ Drive จำลอง (mock) ไม่ได้เรียก Google จริง ไม่ได้รัน Worker/Desktop/Tray/ตัวติดตั้ง ตรวจหลังงาน: hash ของ `configuration.json`, `.status.json`, `.success.json`, `.scheduled-skip.json` ของเครื่องตรงกับก่อนเริ่ม, Tray PID เดิม, task 21:00 สถานะ Ready

ผลตรวจก่อนแก้: `dotnet test PCAutobackup.sln -c Release` ผ่าน 256 จาก 257 ข้อที่ล้มคือ `ProgressTelemetryTests.DuplicateFileReportsProgressWhileSourceIsStillOpen` ซึ่งแข่งเวลาระหว่างการเขียน 4 MiB แบบ write-through กับการอ่านไฟล์ sparse ไม่ใช่บั๊กของแอป หลังแก้: build 0 warning/0 error และผ่าน 331/331 เครื่องนี้ไม่มี .NET SDK ระดับระบบ ใช้ SDK 10.0.403 ที่ `%LOCALAPPDATA%\PCAutobackup\dotnet-sdk`

วิธีทำ: Root (Claude Fable 5.2 high) แบ่ง 7 lane ขนานกันใน worktree แยก แล้วตรวจแย้งสองรอบด้วย agent อิสระ รอบแรกกวาด Core ซ้ำ 7 มุมมอง (26 ข้อยืนยัน, 10 ข้อทำซ้ำด้วย test) รอบสองตรวจ diff รวม (37 ข้อยืนยัน ไม่มี High/Critical, 4 ข้อถูกหักล้าง) การตรวจแย้งทำซ้ำข้อเสี่ยงข้อมูลด้วย test ที่ล้มก่อนแก้

### บั๊กที่ทำซ้ำได้และแก้แล้ว (เรียงตามความเสี่ยงต่อข้อมูล)

- Critical: ต้นทางที่มีสองชื่อเท่ากันเมื่อไม่สนตัวพิมพ์ (โฟลเดอร์ case-sensitive, อักษรนอกตาราง upcase ของ NTFS เช่น U+10D0/U+1C90) หรือชื่อที่มี surrogate ไม่ครบคู่ ทำให้ commit manifest ที่ `ReadManifest` ปฏิเสธ ผล: รอบสำรองรายงานสำเร็จ แต่ชุดนั้นกู้ไม่ได้ รายการกู้คืนและรอบสำรองถัดไปล้มถาวร และเมื่อ retention = 0 prune ลบชุดเก่าที่ยังดีในรอบเดียวกัน แก้: ตรวจชื่อระหว่างเดินต้นทาง (`ReserveManifestPath`, `RequireOpenableName`, `RequireRestorableName`) และใช้ `ValidateManifest` ชุดเดียวกันทั้งตอนอ่านและก่อน commit
- High: prune ลบ chunk ของ snapshot ที่รายการ `snapshots/` ตอนเริ่มรอบมองไม่เห็น (รายการ Drive ล่าช้า หรือมีผู้เขียนอีกราย) แก้: หยุดล้างเมื่อพบ manifest รหัสถูกต้องที่ไม่อยู่ในรายการตอนเริ่ม
- High: ซ่อม orphan chunk ที่เสียแล้วไม่อ่านกลับ `LocalBackupStore.PutAsync(overwrite=false)` ถือว่าสำเร็จเมื่อชื่อเดิมยังอยู่ (ไฟล์ค้างรอลบบน exFAT/แชร์) จึง commit ชุดที่กู้ไม่ได้ แก้: อ่าน chunk กลับและเทียบก่อนนับว่าซ่อมแล้ว
- High: การกู้คืนหยุดทั้งชุดที่ไฟล์แรกที่เสีย แก้: ข้ามเฉพาะไฟล์ที่ chunk หายหรือตรวจไม่ผ่าน รายงานใน `RestoreResult.Warnings` และไฟล์ `PCAutobackup-unrestored-files.txt` ล้มเหลวทั้งงานเฉพาะเมื่อกู้ไม่ได้เลย ข้อผิดพลาดเครือข่าย/ดิสก์/ยกเลิกยังหยุดทั้งงาน
- High (Drive): การอ่าน body ของ `alt=media` ค้างได้ไม่มีกำหนดเพราะ `HttpClient.Timeout` จบที่ header ทำให้ Worker ถือ lock ค้าง แก้: idle timeout 3 นาทีต่อการอ่าน
- High (Drive): โฟลเดอร์คลังในถังขยะถูกมองเป็นคลังว่าง แก้: ขอ field `trashed` และหยุดก่อนอ่าน/เขียน ยังไม่ได้ยืนยันกับ Drive จริง
- Medium: manifest ที่อ่านไม่ได้หนึ่งชุดทำให้รายการกู้คืนทั้งหมดล้ม แก้: แสดงเป็นแถวเสียหาย ส่วนรอบสำรองยัง fail closed โดยข้อความระบุรหัสชุด
- Medium: กู้คืน/อ่านรายการจากคลังที่เปิด `lease.lock` เพื่อเขียนไม่ได้ล้มเหลว แก้: `AcquireLease(readOnly: true)` สำหรับ list/restore เท่านั้น
- Medium: `WriteJsonAtomicAsync` ล้มเมื่อมีผู้อ่านเปิดไฟล์ปลายทางอยู่ (Tray/Desktop อ่านสถานะ) ทำให้รอบสำรองล้มหรือ success marker หาย แก้: ลอง `File.Move` ซ้ำ 20 ครั้ง ครั้งละ 50 ms
- Medium: success marker ที่ลงเวลาในอนาคต (นาฬิกาเดินเกินแล้วแก้กลับ) กดงานอัตโนมัติไว้จนเวลาจริงตามทัน; งานที่ผู้ใช้ยกเลิกถูกเริ่มใหม่ทันทีที่มี trigger ถัดไป แก้ใน `BackupSchedule.ShouldRun` สถานะ `Cancelled` ใช้เฉพาะเมื่อ token ของผู้เรียกถูกยกเลิก
- Medium: snapshot ที่ขาด chunk หยุดการล้างทุกรอบและตัวมันเองไม่มีวันหมดอายุ แก้: ตรวจ chunk เฉพาะชุดที่จะเก็บไว้; การลบ chunk ไปต่อเมื่อบางรายการลบไม่ได้ แต่หยุดเมื่อเป็นความล้มเหลวของการเชื่อมต่อ; `RetentionDays` ค่าใหญ่มากไม่ทำให้การล้างล้ม
- Medium: Desktop รัน engine บน UI thread ทำให้หน้าต่างค้างระหว่างสแกน แก้: `Task.Run` ใน `ApplicationService` เหมือนเส้นทางของ Worker
- Medium: ปิดการตั้งเวลาไม่ได้เมื่อ task ถูกลบจากนอกแอป แก้: `WindowsScheduler.RemoveAsync` ถือว่า task ที่ไม่มีแล้วคือผลที่ต้องการ
- Low: ข้อความ error ของชื่อที่ลงท้ายด้วยจุด/ช่องว่างชี้ผิดไฟล์; งบ retry ของ data PUT เหลือครึ่ง; token ที่ scope ไม่ครบถูกใช้กับคำขอถัดไป; `invalid_grant` ไม่บอกให้เชื่อมใหม่; OAuth loopback ล้มเมื่อมีการเชื่อมต่อเฉยหรือถูกตัด (และยังไม่เคยมี test ของ callback); `ExternalVolume.ResolvePath` กับพาธที่ไม่ normalize; Tray ล้มเมื่อ config เป็น `null` หรือ Version อื่น; ตัวติดตั้งสองตัวคัดลอกพร้อมกัน; อัปเดตเปิด autostart กลับ; `build.ps1` publish ทับโฟลเดอร์เดิม; กุญแจที่ส่งออกยังไม่ flush ลงดิสก์ก่อนเปิดสิทธิ์สำรอง; ปลายทางลิงก์ที่ลงท้ายด้วย `\` ถูกรายงานว่าไม่รวม
- ไม่ใช่บั๊กแต่ปรับ: manifest เขียนอักขระนอก ASCII เป็น UTF-8 ตรง ๆ (รุ่นเก่าอ่านได้ และรุ่นนี้อ่าน manifest แบบ `\uXXXX` เดิมได้); รหัส chunk ใช้ string ร่วมกันข้าม manifest เพื่อลด RAM ต่อ snapshot ที่เก็บ (ยังไม่ได้วัดผลหลังแก้)

Regression tests ใหม่: `AuditEngineTests`, `AuditGoogleTests`, `AuditOAuthTests`, `AuditApplicationTests`, `AuditDesktopTests`, `AuditCoverageTests` การแก้ใน Desktop/Tray/Installer/`install.ps1` ผ่านการคอมไพล์และตรวจโค้ดเท่านั้น ยังไม่ได้รันกับหน้าต่างหรือการติดตั้งจริง

### ข้อความเดิมในเอกสารนี้ที่ถูกแทน

- "กด X เมื่อมีงานอยู่จะซ่อนแล้วทำงานต่อจนเสร็จ": งานที่ล้มเหลวหรือกู้คืนได้บางส่วนจะเรียกหน้าต่างกลับมาและไม่ออกเอง
- "ตัวติดตั้งสร้าง HKCU Run": การอัปเดตไม่เขียนค่ากลับเมื่อ Tray เดิมมีอยู่และค่า Run ไม่มี
- "งานอัตโนมัติเทียบ StartedUtc ของงานสำเร็จ": เพิ่มเงื่อนไขว่าเวลาเริ่มต้องไม่อยู่ในอนาคต และการยกเลิกโดยผู้ใช้หลังเวลารอบถือว่าครอบคลุมรอบนั้น
- "local repository ใช้ FileShare.None lease": list/restore ใช้ lease แบบอ่านร่วม หรือไม่ใช้เมื่อคลังเขียนไม่ได้ ส่วนการสำรองยังต้องได้ lease แบบ exclusive
- ขั้นตอนของ `BackupEngine` และ Restore: เพิ่มการตรวจ manifest ก่อน commit กฎการล้างข้างต้น และการกู้คืนบางส่วน

### ผลตรวจ Release และประสิทธิภาพ

- Release: asset `PCAutobackup-win-x64.exe` บน GitHub มี digest `sha256:cbd81a94…5241e02b` ขนาด 206,821,105 bytes ตรงกับ `artifacts/PCAutobackup-win-x64.exe` ในเครื่อง; payload ใน installer, แพ็กเกจ 819 ไฟล์ และสำเนาที่ติดตั้งตรงกันทุกไฟล์; PDB ตรงกับ source ที่ tag; `ProductVersion` คือ `1.0.0+7c27c05…`; CI ของ commit ผ่านทั้ง main และ tag อ่านจาก GitHub API แบบไม่ล็อกอิน ไม่ได้ดาวน์โหลด asset ข้อจำกัด: exe สร้างจากเครื่องนี้ ไม่ใช่ artifact ของ CI, ไม่ได้เซ็น, build ไม่ reproducible แบบ bit-for-bit (ไม่มี `ContinuousIntegrationBuild`/PathMap, `Compress-Archive` เก็บเวลาไฟล์, ไม่มี `.gitattributes`)
- คำขอ Drive (mock ไม่ใช่ latency จริง): 1,154 ไฟล์ สำรองครั้งแรก 3,493 คำขอ (เส้นทางเดิม 5,801) รอบไม่เปลี่ยน 20 (เดิม 1,170); 5,000 ไฟล์ ครั้งแรก 15,079 รอบไม่เปลี่ยน 39 กู้คืน 9,903 ตัวเลขในเอกสาร 5,798 → 3,494 และ 1,172 → 25 ทำซ้ำได้ตรงเมื่อแบ่ง 3 หน้าต่อรายการ กู้คืนตรง SHA-256 ทุกไฟล์ทุกกรณี pagination ครบ
- Local store 20,000 ไฟล์ 174 MB บนเครื่องที่มีโหลดจากงานอื่น: สำรองครั้งแรก 740 วินาที รอบไม่เปลี่ยน 426 วินาที กู้คืน 430 วินาที peak working set ของ test host 88–118 MB manifest 209.7 bytes ต่อไฟล์ เวลาแกว่งมาก (กู้คืน 5,000 ไฟล์ชุดเดียวกันวัดได้ 45–159 วินาที) จึงไม่ใช้สรุปความเร็ว ชุด 100,000 ไฟล์จริงไม่ได้รัน
- คอขวดที่วัดได้: `CheckReparse` เดินทุกชั้นพาธกินเวลา 56–73% ต่อไฟล์ในรอบไม่เปลี่ยน; กู้คืนไฟล์เล็กจาก Drive ทำทีละคำขอ; `ListAsync` ของ Drive อ่านทั้งคลังทุกครั้ง 5 รอบต่อรอบสำรอง; ต่ำกว่า 1,024 chunk ยังค้นทีละชื่อ; ทุกรอบอ่าน manifest ที่เก็บไว้ทั้งหมด

### ยังตรวจไม่ได้

- Google Drive จริง: ไม่ได้ทดสอบเลยในงานนี้ รวมถึงพฤติกรรมของโฟลเดอร์ในถังขยะ ความครบของ `name contains` และรายการที่ล่าช้า
- จำนวนวัตถุในคลัง Drive จริงของเครื่องนี้ เทียบกับเพดาน 500,000 รายการต่อโฟลเดอร์
- การแก้ฝั่ง Desktop/Tray/Installer/`install.ps1` ในการใช้งานจริง และ sleep/resume/reboot
- สื่อ write-protected จริง (ทดสอบด้วย attribute read-only), exFAT/FAT32, ไฟดับ
- บรรทัดสรุปจำนวน test ใน log ของ CI (ต้องล็อกอิน GitHub)

### งานที่ควรทำต่อ (เรียงตามความเสี่ยงต่อข้อมูลและการกู้คืน)

1. นับวัตถุในโฟลเดอร์คลัง Drive จริง แล้วออกแบบการแบ่ง chunk เป็นโฟลเดอร์ย่อยพร้อมเส้นทางย้ายคลังเดิม ก่อนถึง 500,000 รายการ
2. ตัดสินใจนโยบาย: ลิงก์ที่ชี้ออกนอกต้นทางทำให้ไม่ล้างประวัติทุกรอบ ซึ่งรวม junction มาตรฐาน `My Music`/`My Pictures`/`My Videos` ใน Documents
3. chunk ที่ snapshot เก่าอ้างถึงยังไม่ถูกตรวจซ้ำ แม้ขนาดที่ Drive รายงานจะผิด ควรตรวจและซ่อมเมื่อมี byte ต้นทางที่ถูกต้องอยู่ในมือ และเพิ่ม scrub
4. คำสั่งลบหรือกัก snapshot ที่เสียจากในแอป และทางแทนที่กุญแจ DPAPI ที่ถอดไม่ได้ด้วยไฟล์ `.pcakey` โดยไม่เสีย DeviceId
5. `install.ps1` คัดลอกทับที่เดิมโดยไม่มี staging/rollback และ Worker ตามตารางอาจเริ่มระหว่างคัดลอก
6. งานที่ล้มเหลวถาวรถูกลองใหม่ทุก trigger โดยไม่มี backoff; Worker ถือ config lock ตลอดรอบ ทำให้บันทึกค่า/กู้คืนจาก Desktop ไม่ได้หลายชั่วโมง
7. ต้นทางที่สลับเป็นดิสก์อื่นหรือว่างเปล่ายังถูกสำรองเป็นชุดว่างและนับ retention; การย้ายไฟล์ระหว่างเดินต้นทางไม่ถูกตรวจจับ; นาฬิกากระโดดไปข้างหน้าทำให้ประวัติหมดอายุทั้งชุด
8. Release: สร้างและแนบ asset จาก CI พร้อม hash, ทำ build ให้ reproducible, เซ็นโค้ด, pin action ด้วย SHA
9. ประสิทธิภาพ: กู้คืนไฟล์เล็กแบบขนานข้ามไฟล์, ลดการเดิน ancestor ซ้ำ, รายการ `snapshots/` แบบไม่อ่านทั้งคลัง, เก็บสรุป manifest แทนการอ่านทุกชุดทุกรอบ
10. Test ที่ยังพึ่งเวลา/นาฬิกาจริง (`RecoveryLifecycleTests` ล้มหนึ่งครั้งภายใต้โหลด, ขอบเวลา 21:00, การอ่าน log วิเคราะห์ร่วมกัน)

## แก้ MIME identity-conflict (2026-11-10)

- ตรวจ metadata แบบอ่านอย่างเดียวโดย uploadId จาก log รอบ 02:19 ที่ล้มเหลว พบ 1 วัตถุในคลังจริง: `mimeType=image/jp2`, parent/pcabRole/uploadId/name canonical ตรง และ AES-GCM decrypt + HMAC chunk-name ผ่าน ไม่ได้เขียนหรือกู้คืนไฟล์จริงลงดิสก์
- สาเหตุที่ยืนยัน: query จำกัด MIME เป็น application/octet-stream จึงซ่อนวัตถุนี้ แล้ว direct-ID check ปฏิเสธ MIME image/jp2; เป็นเงื่อนไขของแอปที่แคบเกินไป ไม่ใช่หลักฐานว่าข้อมูลเสีย เหตุที่ Drive จัด MIME เป็น image/jp2 ยังไม่สรุปกลไกภายใน Google
- เอา MIME filter ออกจาก query; ทุก read/list/mutation ยังคง scope parent + app marker + canonical name; ปฏิเสธ Google Workspace/folder/shortcut ที่ติด object marker ส่วน MIME ไบนารีอื่นรับได้ การอ่าน encrypted payload ยังตรวจ AEAD/HMAC ตามเดิม
- เพิ่ม MetadataChecks ของ id/name/parents/pcabRole/mimeType/size/uploadId ไม่มี raw names/IDs/token; identity error แสดงชื่อช่องที่ไม่ตรง; คง bounded confirmation และไม่อัปโหลดซ้ำระหว่างรอ
- เพิ่ม regression tests แยกช่องผิด, accepted image/jp2, duplicate/foreign/native restrictions และการมองเห็น binary MIME ผ่าน Exists/List/Delete
- Release build ผ่าน; tests 238/238 ผ่าน; live Drive fixture แยกจาก production ผ่านด้วย binary MIME จริง image/jp2 และ application/pdf จำนวน 2 objects
- Final live test กำหนด metadata และ media Content-Type ของ fixture ระหว่าง upload; ทดลอง metadata-only PATCH ครั้งแรกแล้ว Drive ไม่เก็บ MIME เปลี่ยนไว้ จึงไม่นับชุดนั้นเป็นผลยืนยัน MIME
- Restore 36/36 files / 65,591 logical bytes ตรงทุก byte/SHA256; 34 chunks, 2 snapshots, duplicate object names 0, Junction alias entries 0; หลังจำลอง outage ชุดก่อนหน้ายังอยู่; configuration จริง hash ไม่เปลี่ยน
- Evidence แบบ read-only ของวัตถุจริง `%TEMP%\PCAutobackup-identity-diagnostic-20261110\evidence.json`; final synthetic report `%TEMP%\PCAutobackup-tests\drive-metadata-3151727f297442aeb6a3adf968c3d590\drive-test-result.json`; log `%LOCALAPPDATA%\PCAutobackup\logs\google-27732.jsonl` แสดง accepted-binary และผลรายช่อง
- ติดตั้งสำเร็จ 2026-11-10 10:06:57 +07:00; Core SHA256 A61BC37DA40C05A616EAA4FA6F56756245CBC89C6BAB63711D226F2278276776; hashes ตรง 817/817; configuration, startup, task XML 21:00 และ last-success marker ไม่เปลี่ยน
- Verification `%TEMP%\PCAutobackup-install-metadata-ef27a6fc9ab14969accde51fb0d43ac3\verification.json`
- Live synthetic harness peak working set 145,670,144 bytes (ประมาณ 139 MiB); ไม่ใช่การรับประกัน RAM ของงานจริง

## รอการยืนยันวัตถุหลังอัปโหลด (2026-11-10)

- รอบจริง 23:35:06 วันที่ 9 พ.ย. จบ Failed 02:09:35 วันที่ 10 พ.ย. ด้วย `Google Drive ยังไม่ยืนยันวัตถุที่อัปโหลด`; ไม่มี HTTP retry/error ใน log; ไม่มี snapshot ใหม่ สถานะ last-success เดิมไม่เปลี่ยน
- จุดล้มเหลวที่ยืนยันได้: PutAsync อาศัย name search เพียงครั้งเดียวหลังอัปโหลดและ throw หากผลว่าง โดยไม่ได้ตรวจ ID หรือรอ visibility สาเหตุว่าทำไม Google คืนผลว่างย้อนหลังยังพิสูจน์ไม่ได้
- เปลี่ยนเป็น bounded confirmation 8 attempts: metadata GET ด้วย reserved/existing file ID เมื่อ name search หาย/เก่า; ตรวจ parent/marker/name/MIME ก่อนรอ; ต้อง name search เหลือหนึ่งวัตถุและตรงขนาด/uploadId จึงยืนยัน ไม่มี POST/PATCH/data PUT เพิ่มในขั้นตอนนี้
- Log `upload-confirm` ระบุ missing/stale search, direct-ID visibility, identity conflict, recovered และ exhaustion ด้วย upload correlation ID ที่ไม่มีข้อมูลลับ
- เพิ่ม tests delay search สำหรับ create/overwrite, stale metadata, permanent missing, foreign object, duplicate writer และ cancellation พร้อมยืนยัน payload ส่งเพียงครั้งเดียว
- Release build และ tests 227/227 ผ่าน; live Drive fixture แยก repository จาก production ผ่าน: จำลอง search ไม่พบ 3 ครั้ง / direct GET 404 หนึ่งครั้ง แล้ว confirm ครั้งที่ 4 โดยไม่สร้างข้อมูลซ้ำ
- Restore ข้อมูลจำลอง 36/36 files / 65,591 logical bytes ตรงทุก byte/SHA256; 34 chunks, 2 snapshots, duplicate object names 0, Junction alias entries 0; persistent transport outage ยังคง snapshot ก่อนหน้าได้; hash configuration จริงไม่เปลี่ยน
- Report `%TEMP%\PCAutobackup-tests\drive-confirm-23265130ce444a6e9b8c977b67398896\drive-test-result.json`; log `%LOCALAPPDATA%\PCAutobackup\logs\google-27148.jsonl`
- การทดสอบใช้ fault injection กับผลตอบกลับของ Google Drive จริงเพื่อจำลอง visibility delay ไม่ได้อ้างว่า Google เกิด delay เองระหว่างทดสอบ; ไม่ใช้ไฟล์จริงหรือเปลี่ยน repository จริง
- เพิ่ม English translations ของ diagnostics หลัง live test และยืนยันด้วย release build/tests 227/227 อีกครั้ง โดยโค้ดอัปโหลดยังคงเดิม
- ติดตั้งสำเร็จ 2026-11-10 02:19:49 +07:00; Core SHA256 `718331D02F715EC8291524154317FF0FD07398D7B3FA3BB629FBCFE922AA0D97`; hashes ตรง 817/817; configuration, startup, task XML 21:00 และ last-success marker เดิมไม่เปลี่ยน
- Verification `%TEMP%\PCAutobackup-install-confirm-c6d77258bb9e46e2809ef0ed03249b77\verification.json`
- Peak working set ของ live synthetic harness 143,556,608 bytes (ประมาณ 137 MiB); ไม่ใช่การรับประกัน RAM ของรอบจริง

## แก้ transient Google request failure (2026-11-09)

- สองรอบจริงจบด้วย generic HttpRequestException โดยไม่มี inner exception ในสถานะเดิม จึงยังสรุป root cause ระดับเครือข่ายไม่ได้ พบช่องว่างที่ upload-init และ OAuth refresh ไม่ retry, timeout ไม่ถูก retry ใน GoogleSession และสถานะ timeout อาจถูกจัดเป็น Cancelled
- GoogleSession เพิ่ม bounded retry 6 attempts/backoff/jitter, timeout แปลงเป็น failure ที่มี request ID, retry token refresh และ 403 เฉพาะ rate limit; metadata response อ่านจนจบภายในขอบเขต retry ส่วน media download คง streaming
- Upload จอง file IDs ล่วงหน้าเป็น batch 100 และใช้ stable ID ตลอด retry/restart; HTTP 409 เข้าสู่ reconciliation เดิมโดยไม่สร้าง ID ใหม่; data upload ที่ผลตอบรับสูญหายมี backoff ก่อน probe
- เพิ่ม structured allowlist diagnostics: operation, attempts, status, exception type/HResult/HttpRequestError/socket code; ไม่มี raw exception message หรือข้อมูลลับ บันทึก final failure chain จาก ApplicationService ด้วย
- ทดสอบอัตโนมัติเพิ่ม transport/timeout exhaustion, caller cancellation, OAuth refresh retry, transient/permanent HTTP errors, stable ID หลัง lost init acknowledgement และตรวจ log ไม่เปิดเผย secret; ปรับ regression เดิมที่ห้าม retry create ให้ตรวจ bounded retry กับ ID เดียวแทน
- Release build ผ่าน และ automated tests 220/220 ผ่าน; final package Core SHA256 `2051B5A3718DA5CECE25BE0533687BE659B8F275E8DF666826730A03B42D31B0`
- Live Drive fault-injection ผ่านสองชุดแยกจาก production: HTTP 503, lost upload-init acknowledgement (ConnectionReset), lost final data acknowledgement (timeout), persistent outage ครบ 6 attempts; รอบที่ล้มเหลวไม่เพิ่ม snapshot และชุดก่อนหน้ายังคงอ่านได้
- Final fixture: 36 files / 65,591 bytes, 34 unique chunk objects, 2 successful snapshots; restore 36/36 ตรงทุก byte/SHA256, Junction alias entries 0, config จริง hash เดิม; report `%TEMP%\PCAutobackup-tests\drive-retry-3ad9c5c8a3d1438dbcf63c43ee16b75b\drive-test-result.json`
- Log final live test `%LOCALAPPDATA%\PCAutobackup\logs\google-20168.jsonl` ยืนยันขั้นตอน/SocketError/timeout/recovered-after-probe; ไม่มีการทดสอบกับไฟล์ Project จริง
- Peak working set ของ live fixture รอบแรก 145,883,136 bytes (ประมาณ 139 MiB); เป็น test harness ข้อมูลจำลอง ไม่ใช่การรับประกัน RAM ของ production
- ติดตั้งสำเร็จ 2026-11-09 23:35:07 +07:00: hashes ตรง 817/817 files; configuration, HKCU startup, task XML (21:00), last-success marker ไม่เปลี่ยน; verification `%TEMP%\PCAutobackup-install-retry-a427c172109e479f84f8d04dca2bfa40\verification.json`
- Tray ใหม่เริ่มรอบชดเชยตามพฤติกรรมเดิมเวลา 23:35:06 (Worker PID 14228); สถานะ Running ณ การตรวจหลังติดตั้ง ยังไม่อ้างว่ารอบจริงสำเร็จ ไม่มีการหยุด Worker

# Project context — PC AutoBackup

## ติดตั้งรุ่นคิวไฟล์เล็กหลังรอบเดิมจบ — 9 พฤศจิกายน 2026 เวลา 21:29 น.

ผู้ใช้อนุญาตให้รอ Worker เดิมจบแล้วติดตั้งทับ โดยคงการตั้งค่าและตารางเดิม Worker PID 1676 เริ่ม 01:07:52 และจบ 21:29:31 ด้วย Failed: `An error occurred while sending the request.` ประมวลผล 168,468 ไฟล์ 64,088,974,343 bytes แต่ยังไม่มี snapshot commit จึงไม่ถือว่ารอบนี้สำรองครบ รอบ 21:00 ถูกข้ามด้วย application lock ตามที่ออกแบบไว้ ไม่กระทบรอบเดิม

หลัง Worker ออก ติดตั้งจาก `artifacts/PCAutobackup-smallfiles-win-x64/install.ps1 -NoLaunch` สำเร็จ เทียบ SHA-256 payload 817/817 ตรง การตั้งค่าแบบ byte hash, startup และ Task Scheduler XML ก่อน–หลังตรงเดิม ตารางทุกวัน 21:00 และต้นทาง 2 รายการเดิม Installed Core ของ Desktop/Worker/Tray ตรงกัน: `6802B24B4391F341E5D5F7A510B028A5E2E3A2CC6AFB829627E916023289A350` เริ่ม Tray PID 12908 แล้วระบบตรวจ missed/failed round และเริ่ม Worker รุ่นใหม่ PID 15532 อัตโนมัติ 21:29:55 สถานะ Running ขณะตรวจหลังติดตั้ง ไม่ได้สั่ง backup ด้วยมือหรือใช้ข้อมูลจริงเป็น fixture การตรวจนี้ยืนยันการติดตั้ง ไม่ได้ยืนยันว่ารอบใหม่สำเร็จ

หลักฐานใน Temp: `C:/Users/USER/AppData/Local/Temp/PCAutobackup-install-smallfiles-4f0a3b57dada49229c92a86d9e76b462/verification.json` และ `completed-run.json` รายการด้านล่างที่ระบุว่ายังไม่ติดตั้งเป็นสถานะก่อนหน้านี้

## ปรับคิวไฟล์เล็ก — 9 พฤศจิกายน 2026

ย้ายการ drain upload จากท้ายแต่ละไฟล์ไปท้ายการอ่านทุกต้นทาง คง bounded pending queue และการยกเลิก/รอ task ทั้งหมดเมื่อเกิดข้อผิดพลาด จึงใช้ concurrency 16 ข้ามไฟล์เล็กได้จริง พร้อม dedupe chunk ที่กำลังส่งหรือส่งสำเร็จแล้ว Snapshot commit ยังคงรอทุก object durable ลด ancestor checks ซ้ำต่อ entry เฉพาะช่วงประมาณขนาด (ยังตรวจ directory chain และ entry reparse) ส่วน actual read และ restore คง full-path check เดิม เพิ่ม fixture ทดสอบหลายไฟล์พร้อมกัน 2/16 งาน, byte-for-byte restore, duplicate content และ upload failure รุ่นนี้ยังไม่ติดตั้งทับ เพราะ Worker จริงยังทำงาน รุ่นแพ็กแยกอยู่ `artifacts/PCAutobackup-smallfiles-win-x64` ไม่แก้ executable ที่ Worker เดิมใช้อยู่

วัด testhost ระหว่าง synthetic automated suite: working set 146,329,600 bytes, peak working set ณ จุดวัด 610,529,280 bytes รวม test runner และหลาย workload ไม่ใช่ Worker idle/production guarantee

ผลตรวจรุ่นคิวไฟล์เล็ก: Build ผ่าน, automated suite 204/204 ผ่าน (48 ไฟล์เล็กยืนยัน concurrent uploads สูงสุด 16 และกู้คืน byte-for-byte ครบ) ทดสอบ Google Drive จริงด้วย fixture 3 ไฟล์ 16,439 bytes และ Junction 3 ตัวในคลังแยก `10pIwoCWH0BPt6Bsu2t5sWUt2mfJ8bErI` สำรอง 2 รอบยังมี 2 chunks, restore 3/3 ตรงทุก byte, alias ใน manifest = 0, config จริง hash เดิม ยังไม่ได้วัดอัตราเร่งของ production workload จึงไม่รับประกันจำนวนเท่าที่เร็วขึ้น Worker เดิม PID 1676 ยังทำงานและ installed Core hash เดิม ณ ตรวจ 02:10 น.

## รองรับ Junction ภายในต้นทาง — 9 พฤศจิกายน 2026

BackupEngine ข้าม junction/symlink ที่พบระหว่าง scan และ backup โดยไม่เดินตามลิงก์ จึงสำรองข้อมูลผ่านเส้นทางจริงในต้นทางที่เลือกเพียงครั้งเดียว และไม่วนซ้ำเมื่อมีลิงก์กลับไปยังต้นทาง รายการที่ข้ามแสดงเป็นคำเตือนภาษาไทย/อังกฤษ หากปลายทางอยู่นอกต้นทาง หาย หรือผ่าน reparse point อื่น จะระบุว่าไม่ได้รวมปลายทางและงด prune ทั้ง retention/quota รอบนั้น ชุดเก่าจึงยังอยู่แม้ retention=0 โดย quota ของชุดใหม่ยังตรวจตามเดิม ไม่สร้างลิงก์กลับตอน restore และไม่เปลี่ยนข้อห้าม reparse point สำหรับ restore, repository, source root หรือ unknown/cloud reparse tag

เพิ่ม JunctionBackupTests ใช้เฉพาะ fixture ใน temporary directory: หลาย alias, cycle, scan เปิด/ปิด, round-trip, changed/deleted files, retention=0, external/dangling junction และ restore path safety ติดตั้งรุ่นแก้ไขทับในเครื่องแล้วตามคำขอผู้ใช้วันที่ 9 พฤศจิกายน 2026 (ยังไม่เผยแพร่): EXE exit 0, เทียบ payload 817/817 ตรง, application-data files ก่อนเริ่ม Tray ทั้ง 4 ไฟล์มี hash เดิม รวม configuration, startup และ Task Scheduler XML ตรงเดิม ตารางทุกวัน 21:00 และต้นทาง 2 รายการเดิม Installed Core SHA-256 `6CFE0ED7843B8C91D8736924AA37E1BB02D3F1555E9BA42E82C9B4AB30C43ADE` หลังเปิด Tray พบ Worker เริ่มอัตโนมัติจากการตรวจรอบค้างตามนโยบายเดิม ไม่ได้สั่ง backup ด้วยมือหรือใช้ข้อมูลจริงเป็น fixture ผลดิบอยู่ใน ignored `artifacts/install-junction-fix/verification.json` รายการ release ด้านล่างเป็นประวัติรุ่นก่อนหน้า

ผลตรวจ: automated tests 201/201 ผ่าน วัด process `testhost` ระหว่างรัน suite ทั้งชุดได้ working set 199,081,984 bytes และ peak working set ณ เวลาสุ่มวัด 709,357,568 bytes ตัวเลขนี้รวม test runner และ synthetic workloads หลายกรณี ไม่ใช่การวัด Desktop/Tray idle หรือ Worker production และไม่ใช่เพดาน RAM ที่รับประกัน

ตรวจ CI หลัง Release persistent-undo ผ่าน2/2 แล้ว ดาวน์โหลด EXE จาก Release มาติดตั้งทับ exit0 SHA256 EXE856CFD528A7174BC4DF6B300DDDEAAEEFED727FA0CD827CEE276A9A7A6D26FFE เทียบpayload816/816ตรง InstalledCore70A9C27081EE21082C149C30097E6A445E10297FD59E6044FEA6827D57C42C5B config/startup/taskXMLเดิมทุกค่า คง16/16และdaily21:00 เริ่มTrayจริงแล้ว ดู [รายงานติดตั้ง Release](docs/release-persistent-undo-install-verification-2026-11-09.md) เผยแพร่รายงานและเพิ่มผล816/816กับCI2/2ในรายละเอียดReleaseเดิม โดยไม่เปลี่ยนassetหรือตาราง

เตรียมเผยแพร่ `v2026.11.09-persistent-undo` รวมแก้ปุ่มนำออกไทย/อังกฤษ, ประวัติ Undo ถาวร, ยืนยันนำออก, คืนโฟลเดอร์ทั้งหมด, ล้างประวัติพร้อมคำยืนยัน และแถบซ่อนหลัง60วินาที ตัวติดตั้งไฟล์เดียวชื่อ PCAutobackup-win-x64.exe พร้อมคู่มือ HTML และ SHA256SUMS.txt คง16/16และเวลา21:00 หลักฐานทดสอบหน้าต่างจริงอยู่ในรายงาน persistent-source-ui-verification-2026-11-09.md บันทึกก่อนหน้านี้ที่ระบุว่ายังไม่เผยแพร่เป็นสถานะ ณ เวลาตรวจครั้งนั้น

ติดตั้ง persistent Undo รุ่นล่าสุดในเครื่องและทดสอบหน้าจอจริงครบแล้ว (9 พฤศจิกายน 2026): Undo, Confirm, Restore folders, Clear No/Yes, ซ่อนแถบหลังรอจริงเกิน60s และประวัติอยู่หลังเปิดใหม่ผ่าน เทียบ installed payload815/815 และไฟล์จำลอง SHA2/2 ตรง configurationจริง/startupเดิม ตารางdaily21:00เดิม แต่installerrefresh StartBoundaryเป็นวันที่9ตามวันติดตั้ง คง16/16 Core5477718DEF48011932DC2915770FF5ADE1303A95713E073BD08BD0991B4181F3 ดู [รายงาน](docs/persistent-source-ui-verification-2026-11-09.md) ยังไม่commit/pushหรือReleaseใหม่ ข้อความสถานะก่อนติดตั้งด้านล่างเป็นประวัติ

## ปรับ Undo แบบถาวร — 9 พฤศจิกายน 2026

แทนที่พฤติกรรม session-only ด้านล่างด้วย `PendingSourceRemovals` ใน AppConfiguration บันทึกพร้อม Sources แบบ atomic และใช้ Revision ป้องกัน stale writer รายการมี Source, OriginalIndex และ RemovedUtc ไม่มีการตัดประวัติอัตโนมัติที่ 20 รายการ การปิด/รีเฟรชไม่ล้างประวัติ แถบ Undo/ยืนยันนำออกซ่อนหลัง 60 วินาทีโดยใช้ timer เฉพาะช่วงที่จำเป็นและหยุดเมื่อหมดเวลา ยืนยันนำออกลบรายการล่าสุดจากประวัติ; คืนโฟลเดอร์คืนทั้งหมดแบบย้อนลำดับและคงรายการที่มี ID/path ซ้ำ; ล้างประวัติต้องตอบ Yes ใน dialog (default No) ทุก mutation rollback เมื่อ save ล้มเหลว ไม่เปลี่ยน concurrency 16/16 หรือเวลา 21:00 การแก้รอบนี้ยังไม่ได้ติดตั้งทับแอปที่เปิดอยู่หรือเผยแพร่ Release

## สถานะล่าสุด: ปุ่มนำออกและ Undo

แก้ข้อความ `System.Windows.ResourceReferenceExpression` ในปุ่มนำออกของ Sources DataTemplate โดยใช้ DynamicResource โดยตรง และเลื่อนการ resolve ของ `LocalizeExtension` จนมี DependencyObject จริง พร้อมแก้ return-type metadata เป็น object ชื่อปุ่มเปลี่ยนตามภาษาเป็น นำออก / Remove ได้ทันที

เพิ่ม `SourceRemovalUndoHistory` แบบ session-only จำกัด 20 รายการ เก็บสำเนา BackupSource พร้อมตำแหน่งเดิม บันทึกประวัติหลัง save สำเร็จเท่านั้น Undo คืน ID/path/label/order และ pop หลัง save สำเร็จ มี rollback เมื่อ save ล้มเหลว และป้องกัน ID/path ซ้ำ ประวัติอยู่ข้ามการสลับภาษา แต่ล้างเมื่อรีเฟรช configuration หรือปิด UI ไม่มี timer หรือการสำเนาเนื้อหาไฟล์

Release build ผ่าน 0 warnings/errors และ 186 tests ผ่าน ทดสอบ compiled ItemsControl ก่อนแก้พบ ResourceReferenceExpression จริง หลังแก้เป็น string ไทย→อังกฤษ→ไทย ทดสอบ WPF จริงด้วยสองโฟลเดอร์จำลอง Remove/Undo และสลับภาษาระหว่างรอ Undo ผ่าน SHA-256/ขนาดไฟล์จำลองคงเดิม 2/2 และการตั้งค่าทดสอบกลับครบทุก field ยกเว้น Revision ติดตั้ง candidate ในเครื่องแล้ว เทียบ payload 814 ไฟล์ตรงทั้งหมด Core SHA-256 `B088C52946F8B46E379AB8911CAB6734CE4217673F44EE6BBA3C842CE87C7587` การตั้งค่าจริง/startup/task XML คงเดิม คง 16/16 และทุกวัน 21:00 ยังไม่ได้ commit/push หรือสร้าง Release ใหม่ ดู [รายงาน](docs/localization-undo-verification-2026-11-08.md) ส่วนบันทึกด้านล่างเป็นประวัติตามช่วงพัฒนา

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

แก้idleRAM: Coreclearbufferrefsหลังdrain; Desktopone-timebackgroundAggressiveGC+LOHcompactionหลังApplicationIdle ไม่trimworking-set/no timer มีgenerationgateกับjobstart ทดสอบ182ผ่าน actualWPF A/BlocalSHA3/3ทั้งคู่ Drivepairedpost60private104.40→83.76MB WS163.89→127.18MB; localmixedWSสูงขึ้น ต้องอ่านข้อจำกัด Candidateติดตั้งในเครื่องCore4FB3112BDC97C96757C98201601A6D32C110E46DB54CC323587A5CC47D0D78C2 config/taskXMLเดิมคง16/16เวลา21:00 ดูdocs/idle-memory-fix-verification-2026-11-08.md ยังไม่commit/push

เตรียมRelease v2026.11.08-idle-ram พร้อมตัวติดตั้งไฟล์เดียวและคู่มือHTML รุ่นนี้คงprogress%,processedMB/s,ETAและ16/16เวลา21:00 แต่เพิ่มคืนmanagedmemoryหลังงาน มีข้อจำกัดการวัดRAMก่อน–หลังตามรายงาน

ตรวจติดตั้ง Release v2026.11.08-idle-ram ในเครื่องสำเร็จ exit 0 เทียบ SHA-256 กับ payload จาก build ตัวติดตั้งครบ 813 ไฟล์ ตรงทั้งหมด Installed Core F7BB6E24914827B1B222069634E9EEA3BDF134192BD701160F28D0F7B37765C3 Configuration/startup/scheduler XML ก่อน–หลังคงเดิม คง 16/16 และทุกวัน 21:00 เปิด Tray รุ่น Release แล้ว รายงาน docs/release-idle-ram-install-verification-2026-11-08.md

2026-11-10 duplicate-name recovery: ตรวจ metadata คลังจริงแบบอ่านอย่างเดียว 74,448 objects พบ 1 ชื่อ chunk มี 2 file IDs ขนาดเท่ากัน 25,707 bytes MIME image/jp2 และ application/octet-stream สร้างต่างรอบ Ciphertext ต่างกัน แต่ AES-GCM และ keyed content HMAC ถูกต้องทั้งคู่ plaintext เท่ากัน สอดคล้องกับการค้นหากรอง MIME ในรุ่นเก่าที่มองไม่เห็นสำเนาแรก แล้วสร้างสำเนาใหม่ก่อนแก้ MIME discovery ไม่แก้/ลบข้อมูลจริงในขั้นวิเคราะห์

เพิ่ม IAuthenticatedChunkStore: BackupEngine ส่ง callback ตรวจ AES-GCM/HMAC โดยไม่ส่งกุญแจให้ Google store; Find ยอมเลือกชื่อ chunk ซ้ำเมื่อทุกสำเนาผ่านตรวจ จำกัด 4 MiB + header ต่อสำเนา ล้าง cache เมื่อเปลี่ยน engine/key เลือก file ID ตาม ordinal เก็บทุกสำเนาเดิม ห้าม mutation/confirmation ยอมรับชื่อซ้ำ List รวม logical chunk และรวม physical bytes สำหรับ quota; owner/manifest ซ้ำยัง fail closed ข้อมูลเสีย/ต่าง content address ไม่ถูกเลือกข้าม ทดสอบ Release 252/252 และ Drive จำลองจริงผ่าน 36 ไฟล์ กู้คืนตรวจ 3 ครั้งรวม 108/108 ไม่มี alias Junction, repeat ไม่อัปโหลด chunk เพิ่ม, corrupted orphan duplicate หยุดโดยเก็บ 2 snapshots เดิมและกู้คืนได้ Peak harness 141,418,496 bytes ดู docs/duplicate-object-recovery-2026-11-10.md

ติดตั้งรุ่นแก้ duplicate chunks เมื่อ 10 พฤศจิกายน 2026 เวลา 13:25:23 ตรวจไฟล์แพ็กเกจ 818/818 ตรง Core 39F8D994029008F5550917ED7DCFAEFC947F719E3A245D9C96D347258DAEE9C1 Configuration/startup/scheduler XML/previous success marker คงเดิม ตาราง 21:00 Tray เริ่มรอบปกติหลังเปิด 13:25:21 สถานะ Running ยังไม่ใช่ผลสำรองสำเร็จ รายงานติดตั้ง %TEMP%/PCAutobackup-install-duplicates-ca57975234034ad78d0f187d5214c155/verification.json

รอบจริง 10 พฤศจิกายน 2026 เวลา 13:25 สำเร็จ 11 พฤศจิกายน 00:38:27 ใช้ 11 ชม. 13 นาที สำรอง 398,384 ไฟล์ 79,874,330,750 bytes มี ConnectionReset หนึ่งครั้งและยืนยันอัปโหลดเดิมด้วย probe HTTP 200 ตรวจโฟลเดอร์ปลายทางของ Junction จาก snapshot ครบ 154,738 ไฟล์และ 8,293 โฟลเดอร์ ทั้ง 9 Junction ไม่มีรายการ alias ใน manifest

ลดคำขอ Drive สำหรับไฟล์เล็กโดยคงรูปแบบคลังเดิม ใช้ multipart สำหรับ chunk ใหม่ขนาดก่อนเข้ารหัสไม่เกิน 256 KiB และรวมการค้นหากับการสร้างภายใต้ mutation lease จึงใช้ 3 คำขอต่อ chunk ปกติ เทียบกับ 5 เดิม Retry ใช้ ID ที่จองไว้เดิมและยืนยันชื่อ ID ขนาดและ uploadId หลังส่ง เมื่อ snapshot เดิมมีอย่างน้อย 1,024 chunk ใช้รายการ metadata แบบแบ่งหน้าสำหรับ chunk ที่มีอยู่และตรวจซ้ำก่อน commit ชื่อซ้ำหรือผลไม่ชัดเจนกลับไปตรวจทีละชื่อ การทดสอบอัตโนมัติผ่าน 257 ข้อ

ผล A/B Drive จริงบนไฟล์จำลอง 1,154 ไฟล์ 786,116 bytes และ Junction 3 จุด สำรองครั้งแรก 5,798 → 3,494 คำขอ 249.99 → 193.44 วินาที รอบไฟล์ไม่เปลี่ยน 1,172 → 25 คำขอ 40.59 → 20.57 วินาที กู้คืนด้วยรุ่นใหม่ทั้ง snapshot ใหม่และ snapshot รุ่นเดิมครบและตรงทุก byte ตรวจ alias ไม่พบรายการซ้ำ มีรายละเอียดวิธีวัดและ peak working set ใน docs/drive-small-files-2026-11-11.md

ติดตั้งรุ่น multipart เมื่อ 11 พฤศจิกายน 2026 เวลา 10:47:43 ตรวจไฟล์ 819/819 ตรงแพ็กเกจ Core C274A08B88C564B51DD162790E98906D3AE17768E0139C85156DB9482590A8F5 คง configuration, startup, scheduler XML, status และ success marker เดิม ตาราง 21:00 เปิด Tray ข้ามการตรวจงานค้างเฉพาะตอนเปิดครั้งนี้ ไม่เริ่มรอบจริง รายงานติดตั้ง %TEMP%/PCAutobackup-install-small-multipart-10940ca7a657415cacd24b3fe8629a62/verification.json

## Codex review หลังรับ handoff (2026-11-11)
ตรวจ diff งาน Claude และแก้เพิ่มสามจุด: partial-restore report ไม่เขียนทับ/ไม่กลืน error, ตรวจ task ที่หายด้วย HRESULT เฉพาะแทน exit code ทั่วไป, และปฏิเสธ build output ที่ผ่าน Junction ก่อนลบแพ็กเกจ Tests 336/336; solution และ installer build ผ่าน 0 warnings/errors ทดสอบ Google Drive จริงด้วยคลังจำลองใหม่ 34 ไฟล์และ Junction 3 จุด สำรองสองรอบ กู้คืนตรงทุก byte ไม่มีชื่อวัตถุซ้ำและไม่มี chunk upload ซ้ำในรอบ unchanged ค่าตั้งจริง hash เดิม ยังไม่ push หรือติดตั้ง ดู `docs/claude-audit-review-2026-11-11.md` สำหรับหลักฐานและข้อจำกัด

UI/installer QA รอบ 2026-11-11 ยังไม่ครบ: UI backup และ history ผ่านบน config จำลอง; restore dialog ถูกบล็อกด้วยข้อจำกัด computer-use; install helper busy guard ผ่าน แต่ clean install/upgrade ต้องใช้ Windows แยก ตรวจ config/task/startup/installed Core จริงยังเดิม Worker ไม่ถูกหยุด ดู docs/ui-installer-qa-2026-11-11.md ยังไม่พร้อมรับรอง release และยังไม่ push/install

เตรียม Windows Sandbox kit แล้วตามคำขอ: artifacts/WindowsSandbox-QA/Start-QA.wsb พร้อม candidate installer สร้างใหม่, self-contained synthetic fixture และ runner ที่ map Input read-only/Results writable ปิด network/clipboard ตรวจ XML/PowerShell syntax และ Core hash ตรงกัน ยังไม่ได้รัน Sandbox เพราะเครื่องนี้ยังไม่มีตัวเรียกใช้งาน ดู docs/windows-sandbox-qa-th.md ห้ามนับ packaging เป็นผลผ่าน installer/UI

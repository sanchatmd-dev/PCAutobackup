# ผลสำรองอัตโนมัติ 21:00 — 8 พฤศจิกายน 2026

เฝ้าดูรอบจริงโดยไม่สั่ง backup เพิ่ม Windows Task Scheduler เริ่ม Worker PID27420 เวลา21:00:01.162 สถานะสำรองเริ่ม21:00:02.571 และสำเร็จ21:10:25.291 (ประมาณ10นาที23วินาที) Task result0 รอบถัดไป9พฤศจิกายน21:00 Snapshot20261108140034303-6e3ab895d8c2444096256d3245038234 ตรงกับ manifest ใหม่บนGoogle Drive มี46ไฟล์ รวม22,258,505,892plaintext bytes

Worker ใช้ installed Core SHA-256 EDD26B928208655A887097387E4C28CC1E7B44AC50A76509F5D45BBB3640245D ซึ่ง production factoryกำหนด backup16/restore16 และ Google mutation slots16/optimized uploads จากโค้ดและการทดสอบ factory ก่อนเผยแพร่ ไม่มี HTTP trace ของ Workerรอบนี้ จึงยืนยันได้ว่าใช้รุ่นที่ตั้งเพดาน16 ไม่ใช่ยืนยันว่ามี16 dataPUTพร้อมกันตลอดเวลา

## หลักฐาน incremental

อ่านเฉพาะ metadataและlatest encrypted manifest ก่อน/หลัง ไม่อ่านไฟล์ต้นทางเพิ่ม ไม่สั่งrestoreหรือprune ชุดใหม่อ้าง5342unique chunks ใช้เดิม3405chunks (14,158,836,053ciphertext bytes) และใหม่1937chunks (8,099,840,783ciphertext bytes) เพิ่มmanifest370238bytes chunksเดิมที่ยังอยู่ไม่มีDriveID/size/ModifiedUtcเปลี่ยน ไม่มีchunkถูกลบระหว่างcapture หลักฐานสอดคล้องกับการใช้ข้อมูลเดิมซ้ำ ไม่ส่งทั้ง22.26GBใหม่ ตัวเลขนี้เป็นdurable ciphertext ไม่ใช่จำนวนbytesผ่านเครือข่ายและไม่รวมHTTP/TLS/retries

เทียบpathในmanifestด้วยHMAC: ไฟล์เดิมcontent refs/ขนาดตรง37ไฟล์ (14,158,727,093logical bytes), pathเพิ่ม9, pathหาย41, common-pathcontentเปลี่ยน0 ค่าpathเพิ่ม/หายเทียบกับsnapshotก่อน ไม่ยืนยันว่าเป็นการเพิ่ม/ลบโดยตรงแทนrenameหรือย้ายpath แอปยังอ่าน/hashต้นทางและตรวจchunkเดิมทุกattempt

## RAM ของ Worker จริง

- Windows PeakWorkingSet64 และstatusหลังสำเร็จ:530,894,848bytes =530.89decimal MB (506.30MiB)
- สูงสุดจากตัวอย่างWorking Set ทุกประมาณ250ms:526,651,392bytes =526.65MB
- สูงสุดsampled private memory:556,589,056bytes =556.59MB
- 2295samples ไม่รวมTray/Desktop ข้อมูลจริงและmetadataมากกว่าชุดจำลอง จึงมีpeakสูงกว่า ค่านี้ไม่รับประกันสำหรับTBหรือทุกworkload

## การตั้งค่าและข้อจำกัด

Scheduler XMLก่อน/หลังตรงกัน ตารางยังทุกวัน21:00 Enabled และLastTaskResult0 Settingsทุกช่องคงเดิมเมื่อยกเว้นProtectedGoogleTokens/Revision (ต่ออายุOAuthตามปกติ) Fullconfigurationhashจึงเปลี่ยน Installedbinaries780ไฟล์ตรงกันก่อน/หลัง ไม่มีการdeploy/commit/pushในงานตรวจนี้ หลักฐานดิบอยู่ในartifacts/scheduled21-verification/ (ignored) ไม่มีfilenamesหรือfilecontentsในรายงาน ใช้metadataและauthenticatedmanifestพิสูจน์reuse ไม่ใช่packettraceและไม่ได้restore/SHA256ไฟล์ผู้ใช้รอบนี้
## อัตราเฉลี่ยทั้งรอบ

ข้อมูลเข้ารหัสใหม่ที่บันทึกสำเร็จรวม manifest 8,100,211,021 bytes หารเวลา BackupAsync ประมาณ 622.72 วินาที ได้ประมาณ 13.01 MB/s หรือ 104.06 Mbps เป็นอัตรา durable bytes ต่อเวลาทั้งรอบ รวมอ่าน/hash และตรวจ chunks เดิม ไม่ใช่ความเร็วเครือข่ายเฉพาะช่วง upload และไม่รวม HTTP/TLS/retries ไม่มีการจับ network trace ในรอบนี้

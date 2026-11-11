# แก้ชื่อ chunk ซ้ำใน Google Drive — 10 พฤศจิกายน 2026

## หลักฐานและสาเหตุ

ตรวจคลังจริงแบบอ่านอย่างเดียว: metadata 74,448 objects มีชื่อ chunk ซ้ำ 1 ชื่อ 2 file IDs ขนาดสำเนาละ 25,707 bytes MIME `image/jp2` และ `application/octet-stream` สำเนาแรกแก้ไขเวลา 02:09:34 และสำเนาที่สอง 04:18:18 วันที่ 10 พฤศจิกายน 2026 (เวลาไทย) ทั้งคู่ผ่าน AES-GCM และ HMAC ตรงชื่อ chunk เดียวกัน plaintext เท่ากัน แต่ ciphertext ต่างกันจาก nonce การค้นหาแบบเดิมกรองเฉพาะ octet-stream จึงซ่อนสำเนาแรก และทำให้มีโอกาสสร้างใหม่ในรอบถัดไป รุ่นแก้ MIME จึงค้นพบทั้งสองแล้วหยุดตามนโยบายชื่อซ้ำเดิม

ไม่ได้แก้ไข ลบ หรือกู้คืนไฟล์จริงลงดิสก์ระหว่างวิเคราะห์ หลักฐานอยู่ที่ `%TEMP%/PCAutobackup-duplicate-diagnostic-20261110/authenticated-evidence.json`

## พฤติกรรมรุ่นแก้

- ตรวจทุกสำเนาของชื่อ chunk ด้วย AES-GCM และ HMAC โดยจำกัดขนาดอ่าน แล้วเลือก file ID แบบ ordinal เมื่อทุกสำเนาผ่าน
- รองรับ ciphertext ต่างกันที่แทน plaintext เดียวกัน เก็บสำเนาทางกายภาพทั้งหมด ไม่สร้าง chunk เพิ่ม
- ชื่อ owner/manifest ซ้ำ ข้อมูลผิด key หรือผิด content address และข้อมูลเสีย ยังคงหยุดอย่างปลอดภัย
- ห้ามเขียนทับและลบชื่อซ้ำ รายการ logical chunk ไม่ซ้ำ แต่รวมขนาดทุกสำเนาสำหรับ quota หาก cleanup พบชื่อซ้ำที่ต้องลบ จะคงสำเนาไว้และรายงานคำเตือนหลัง commit
- log `duplicate-object` แยกการยืนยันสำเนาเหมือนกัน การตรวจล้มเหลว และ mutation ที่ถูกปฏิเสธ ไม่มีชื่อไฟล์ เนื้อหา หรือ credentials

## การทดสอบก่อนติดตั้ง

Release tests ผ่าน 252/252; build/publish Desktop, Worker และ Tray ผ่าน Core SHA-256 `39F8D994029008F5550917ED7DCFAEFC947F719E3A245D9C96D347258DAEE9C1`

ทดสอบ Google Drive จริงใน DeviceId และ repository ใหม่แยกจากงานจริง: 36 ไฟล์ 65,591 bytes, 34 unique chunks, Junction 3 จุด สร้าง chunk ชื่อซ้ำด้วย ciphertext ใหม่และ MIME image/jp2 จริงบน Drive

- สำรองสำเร็จ 2 snapshots; รอบซ้ำส่งเฉพาะ manifest ไม่มี chunk เพิ่ม
- กู้คืน snapshot เก่าและใหม่ และกู้คืนเก่าอีกครั้งหลังฉีดความเสียหาย: 108/108 การตรวจไฟล์ตรงทุก byte
- ไม่มี alias Junction ในผลกู้คืน และ empty directory ครบ
- สำเนาที่ตั้งใจสร้างทั้งสองอยู่ครบ ไม่มีการลบหรือแก้ไข
- List คืนหนึ่ง logical chunk แต่คิดขนาดทั้งสองสำเนา; การลบชื่อซ้ำถูกปฏิเสธ
- เพิ่มคู่ orphan chunk ที่สำเนาหนึ่งเสียหาย: รอบทดสอบล้มเหลวตามคาด ไม่ commit snapshot ใหม่ และ snapshot เดิมกู้คืนได้
- configuration hash จริงไม่เปลี่ยน Peak working set ของ synthetic harness 141,418,496 bytes (134.87 MiB); ไม่ใช่การรับประกัน RAM ของข้อมูลจริง

รายงาน: `%TEMP%/PCAutobackup-tests/drive-duplicates-119fcdfdf3f04abb94f504e6b0d96654/drive-test-result.json`

การทดสอบนี้ไม่ได้ยืนยันว่ารอบสำรองจริงรอบถัดไปจะสำเร็จหรือว่า Project จริงสำรองครบ ต้องตรวจผลรอบจริงเมื่อจบ

## ติดตั้งในเครื่อง

ติดตั้งเสร็จ 13:25:23 เวลาไทย ตรวจ hash เทียบแพ็กเกจ 818/818 ไฟล์ตรงทั้งหมด configuration, HKCU startup, Task Scheduler XML และ previous-success marker คงเดิม ตารางยังเป็น 21:00 Tray รุ่นใหม่ทำงานและเริ่มรอบปกติอัตโนมัติ 13:25:21 สถานะ Running ณ ตรวจหลังติดตั้ง ยังไม่ได้ยืนยันผลรอบจริง

หลักฐานติดตั้ง: `%TEMP%/PCAutobackup-install-duplicates-ca57975234034ad78d0f187d5214c155/verification.json`

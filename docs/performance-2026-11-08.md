# ผลทดลองลดคำขอและสำรองพร้อมกัน 2 ก้อน — 8 พฤศจิกายน 2026

สถานะ: โค้ดทดลองใน checkout เท่านั้น ยังไม่ได้ติดตั้ง เปลี่ยน configuration/ตาราง หรือเผยแพร่ทับ production; Desktop/Worker ปกติยังไม่เปิดตัวเลือกทดลอง ค่าเริ่มต้นสำรองทุกวัน 21:00 ของผู้ใช้ยังเปิดอยู่

## วิธีเปรียบเทียบ

ใช้ไฟล์จำลองชุดเดียวกัน 4 ไฟล์: random 256 MiB, 32 KiB, ข้อความ 40 bytes และไฟล์ว่าง รวม 268,468,264 bytes สร้างคลัง Google Drive ใหม่แยกแต่ละรอบ จึงไม่มี chunks จากรอบก่อนให้ใช้ซ้ำ ทั้งหมดเป็นข้อมูลจำลอง

ผลหลักใช้ลำดับ ABBA: รุ่นเดิม (round 4), รุ่นทดลองที่แก้คิวแล้ว (5, 6), รุ่นเดิม (7) แต่ละรอบกู้คืนด้วย Core รุ่นเดิมและเทียบ SHA-256 ทุกไฟล์ก่อนนับว่าสำเร็จ รุ่นเดิมเป็น binary ที่คัดลอกแยกและตรวจ hash ตรงกับ Core ที่ติดตั้งอยู่

เวลางานไม่รวมการสร้าง repository และการเริ่ม process; ความเร็วคำนวณจากขนาดไฟล์ต้นทางหารด้วยเวลางาน ไม่ใช่ความเร็วเฉพาะช่วงที่แพ็กเก็ตวิ่ง ตัวเลข MB ใช้ฐานสิบ ส่วน MiB = 1,048,576 bytes วัด working set และ private memory ทุก 250 ms พร้อมค่า OS peak ใน headless child harness ที่ใช้ Core/GoogleDriveBackupStore จริง ไม่รวม Tray/UI และไม่ได้รันจาก installed Worker.exe

## ผลหลัก

| รอบ | รุ่น | เวลาสำรอง (วินาที) | อัตราข้อมูลต้นทาง (MB/s) | HTTP requests | Peak working set (MB) | Sampled peak private (MB) |
| --- | --- | ---: | ---: | ---: | ---: | ---: |
| 4 | เดิม | 246.24 | 1.090 | 600 | 88.81 | 49.55 |
| 5 | ทดลอง 2 ก้อน | 119.71 | 2.243 | 344 | 114.66 | 72.75 |
| 6 | ทดลอง 2 ก้อน | 116.92 | 2.296 | 344 | 107.38 | 64.16 |
| 7 | เดิม | 241.68 | 1.111 | 600 | 101.80 | 60.95 |

เวลาเฉลี่ยลดจาก **243.96 เป็น 118.31 วินาที** หรือ **51.50%** อัตราข้อมูลประมาณ **1.10 เป็น 2.27 MB/s** เร็วขึ้น **2.06 เท่า** ใน workload นี้ HTTP รวมลด **42.67%** จาก 600 เหลือ 344 ต่อรอบ

Peak working set สูงสุดที่พบต่อกลุ่มเพิ่มจาก 101.80 เป็น 114.66 MB (ประมาณ 12.86 MB); ค่าเฉลี่ยของ peak สองรอบเพิ่มจาก 95.30 เป็น 111.02 MB ตัวเลขเหล่านี้ไม่ใช่เพดาน RAM ที่รับประกัน Metadata ยังโตตามจำนวนไฟล์/chunks และประวัติ

## คำขอที่ลดลงและความขนานจริง

- Data PUT ลดจาก 324 เป็น 68; encrypted chunk 4 MiB+32 bytes ส่งด้วย final request เดียว แทนสี่คำขอ 1 MiB และอีกคำขอ 32 bytes
- Metadata GET ยังคง 206 และ session POST 68 เพราะยังตรวจ duplicate/conflict และตรวจยืนยันหลังอัปโหลด ไม่มีการตัดการตรวจความถูกต้องเพื่อให้เร็วขึ้น
- รุ่นทดลองทั้งสองรอบตรวจพบ maxActiveHttp=2 และ maxActiveDataPut=2 ผ่าน transport จริง รุ่นเดิมรอบ 7 ได้ 1 ทั้งสองค่า
- มีช่วงเวลาคำขอ data PUT ที่ยังรอผลซ้อนกันจริงประมาณ 17.22/25.62 วินาทีต่อรอบ ไม่ใช้ช่วง CopyToAsync ของ client buffer เป็นหลักฐานว่า WAN ส่งไบต์พร้อมกันทั้งหมด
- ไม่มี rate-limit หรือ retry error ในรอบหลัก พบ HTTP 200 และ 308 ตามขั้นตอน resumable ปกติ
- จำนวน bytes ของ request ใน candidate มากกว่า baseline 10 bytes เนื่องจากชื่อเครื่องใน manifest ทดสอบยาวกว่า ข้อมูลต้นทางทั้ง 4 ไฟล์เหมือนกัน

## ความถูกต้องและขอบเขต

ทั้ง 4 ชุดสำรองในผลหลักกู้คืนด้วย Core รุ่นเดิมได้ และ SHA-256 ตรงครบทั้ง 4 ไฟล์ทุกรอบ ยืนยันความเข้ากันได้ของรูปแบบคลังเดิม ยังไม่ได้เพิ่มความขนานในขั้นกู้คืน จึงไม่อ้างว่ารุ่นนี้เพิ่มความเร็วดาวน์โหลด

Automated tests ผ่าน **118 tests**; build solution ผ่าน **0 warnings / 0 errors** ครอบคลุม sustained concurrency เกินคู่แรก, dedupe, failure/cancellation ที่รอ task จบก่อนออก, root exception, partial acknowledgement, lost acknowledgement, session expiry, name conflict และ exclusive delete/manifest mutations

การวัดเพียงสองรอบต่อรุ่นยังไม่ใช่หลักประกันผลระยะยาวกับทุกเครือข่ายหรือข้อมูลระดับ TB ไม่ได้แยกวัด CPU/disk และไม่ทดสอบไฟดับจริงในรอบนี้

## รายละเอียด implementation และข้อผิดพลาดที่แก้ระหว่างทดลอง

BackupEngine รับ `maxConcurrentChunks` 1–2 (default 1), ใช้ buffers ซ้ำจำนวนจำกัด, dedupe และรอทุก chunk durable ก่อน commit เมื่อมี failure จะยกเลิกและรอทุก task พร้อมรักษาสาเหตุผิดพลาดเดิม

GoogleDriveBackupStore เปิดทดลองด้วย `optimizeChunkUploads=true`, `maxConcurrentMutations=2` เฉพาะ harness; ค่าเดิม false/1 และ factory ที่แอปใช้ยังไม่เปิด ใช้ buffer ของ MemoryStream โดยตรงเมื่อทำได้ ชื่อ chunk ซ้ำ serialize ด้วย bounded stripes ส่วน delete/manifest เป็น exclusive

รุ่นทดลองแรกใช้ Dictionary.First() เพื่อรอ task จึงติดพฤติกรรม Dictionary ที่นำช่องที่ลบกลับมาใช้ใหม่: หลังคู่แรก task ที่เสร็จแล้วค้างในรายการและ task ใหม่ถูก await ทีละก้อน แก้เป็น Task.WhenAny และเพิ่ม regression 6 unique chunks ผล candidate ก่อนแก้ (rounds 2–3) ไม่รวมในตารางหลัก เก็บเป็น diagnostic เพื่ออธิบายว่าผลเร็วขึ้นเพียงประมาณ 16% ในตอนแรกเกิดจากอะไร

## การแยกจาก production และหลักฐาน

ตรวจ hash configuration เดิม, hash ไฟล์ติดตั้ง exe/dll 780 ไฟล์ และ XML ของงาน Task Scheduler แล้วเหมือนก่อนทดสอบทั้งหมด Task ประจำยัง Enabled และ NextRun เป็น 21:00 วันที่ 8 พฤศจิกายน 2026 ไม่มี commit/push, release หรือ installer ใหม่จากงานทดลองนี้

- Baseline Core SHA-256: `C76C6A105F2BAC33C1A01741AD3AB170F959970C9FC70C64249BEB858E766214`
- Candidate Core SHA-256: `FF07294A05B2C16D21A120799113E1FE326701E856D455CC9AA71095CC4608B4`
- หลักฐานดิบในเครื่อง (ไม่เข้า Git): `artifacts/performance-verification/run-20261108/result.json`, `concurrency-summary.json`, รายงานรายรอบและ memory CSV
- ผลก่อนแก้คิว: `result-v1-diagnostic.json`
- คลัง Drive จำลองของการทดสอบทั้งหมดแยกจากคลังจริงและยังเก็บสำหรับตรวจสอบ รวมข้อมูลต้นทางประมาณ 1.75 GiB จาก 7 รอบ; ไม่มีการลบหรือแก้ชุดสำรองจริง

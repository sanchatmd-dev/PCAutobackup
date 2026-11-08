# ผลทดลอง Google Drive จริง: 16 เทียบ 32 chunks

ใช้ candidate Core DLL เดียวกันและ harness DLL เดียวกันทั้งสองค่า ทดสอบ ABBA 16,32,32,16 แต่ละรอบเป็น upload และ restore คนละโปรเซส ทำเรียงกัน ไม่มีงาน backup/restore พร้อมกัน ใช้ repository สังเคราะห์ใหม่สี่แห่ง และ restore ลงโฟลเดอร์ใหม่ทุกครั้ง

ใช้ข้อมูลสังเคราะห์เดิมทุก byte: 268,468,264 bytes รวม 4 ไฟล์ (256 MiB + 32 KiB + ข้อความ 40 bytes + ไฟล์ว่าง) ตรวจ SHA-256 และขนาดตรงกันครบ 4/4 ทุก restore อีกทั้ง Core เดิมแบบ 16 chunks อ่าน snapshot ที่สร้างด้วย 32 chunks ได้ครบ 4/4 รอบ compatibility แยกจากค่าเฉลี่ย

| รอบ | งาน | chunks | เวลา (s) | Peak working set (MB) | Peak private (MB) | Mean working set (MB) | Mean private (MB) | PUT/serialize พร้อมกัน | GET/body พร้อมกัน |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|
| A1 | backup | 16 | 32.734 | 305.45 | 264.25 | 200.87 | 160.05 | 13/12 | 1/1 |
| A1 | restore | 16 | 19.520 | 407.86 | 393.65 | 201.41 | 168.47 | 0/0 | 16/13 |
| B1 | backup | 32 | 29.170 | 441.38 | 401.10 | 291.61 | 254.42 | 23/21 | 1/1 |
| B1 | restore | 32 | 15.590 | 405.66 | 387.46 | 167.45 | 132.97 | 0/0 | 32/31 |
| B2 | backup | 32 | 31.383 | 449.25 | 409.26 | 274.73 | 234.03 | 25/22 | 1/1 |
| B2 | restore | 32 | 15.771 | 344.44 | 321.86 | 161.79 | 126.72 | 0/0 | 32/31 |
| A2 | backup | 16 | 31.832 | 346.32 | 303.55 | 227.76 | 187.24 | 15/13 | 1/1 |
| A2 | restore | 16 | 16.950 | 392.76 | 364.53 | 167.39 | 132.79 | 0/0 | 16/11 |

ตรวจการแยกโปรเซสจาก Start-Process แปดครั้งที่รอ exit ก่อนเริ่มงานถัดไป และ output path ใหม่แปดแห่ง ช่วงเวลาตั้งแต่สร้าง stdout จนถึง completedUtc ไม่ซ้อนกัน Windows ใช้ PID เดิมซ้ำได้: รอบนี้พบ PID 23988 ใน B1 upload และ B2 restore คนละเวลา จึงตรวจ process invocation และช่วงเวลาแทนการกำหนดว่า PID ทั้งแปดต้องไม่ซ้ำ ไม่ได้รันเครือข่ายเพิ่มหรือแก้ค่าที่วัด

ค่าเฉลี่ยและค่าสูงสุดจากสองรอบต่อค่า:

| งาน | chunks | เวลาเฉลี่ย (s) | Peak working set เฉลี่ย (MB) | Peak private เฉลี่ย (MB) | Working set เฉลี่ย (MB) | Private เฉลี่ย (MB) | Working set สูงสุด (MB) | Private สูงสุด (MB) |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| backup | 16 | 32.283 | 325.88 | 283.90 | 214.32 | 173.65 | 346.32 | 303.55 |
| backup | 32 | 30.276 | 445.31 | 405.18 | 283.17 | 244.22 | 449.25 | 409.26 |
| restore | 16 | 18.235 | 400.31 | 379.09 | 184.40 | 150.63 | 407.86 | 393.65 |
| restore | 32 | 15.680 | 375.05 | 354.66 | 164.62 | 129.85 | 405.66 | 387.46 |

- backup: 16 chunks เฉลี่ย 32.283 s; 32 chunks 30.276 s; ลดเวลา 2.006 s (6.21%). ค่าเฉลี่ย peak working set เปลี่ยน 119.43 MB; peak private เปลี่ยน 121.28 MB.
- restore: 16 chunks เฉลี่ย 18.235 s; 32 chunks 15.680 s; ลดเวลา 2.554 s (14.01%). ค่าเฉลี่ย peak working set เปลี่ยน -25.26 MB; peak private เปลี่ยน -24.43 MB.

| รอบ | งาน | Plaintext throughput (MB/s) | Requests | Transport errors | HTTP 4xx/5xx | PUT payload (bytes) | GET body (bytes) |
|---|---|---:|---:|---:|---:|---:|---:|
| A1 | backup | 8.202 | 344 | 0 | 0 | 268475819 | 64 |
| A1 | restore | 13.754 | 137 | 0 | 0 | 0 | 268481134 |
| B1 | backup | 9.204 | 344 | 0 | 0 | 268475819 | 64 |
| B1 | restore | 17.220 | 137 | 0 | 0 | 0 | 268481134 |
| B2 | backup | 8.555 | 344 | 0 | 0 | 268475819 | 64 |
| B2 | restore | 17.023 | 137 | 0 | 0 | 0 | 268481134 |
| A2 | backup | 8.434 | 344 | 0 | 0 | 268475818 | 64 |
| A2 | restore | 15.839 | 137 | 0 | 0 | 0 | 268481132 |

Payload เป็นข้อมูลที่ส่งผ่าน data PUT และ body ที่อ่านผ่าน media GET รวม ciphertext และ manifest ไม่ใช่ plaintext bytes และไม่รวม HTTP/TLS headers แยก request status และ stream event ใน requests.json ทุก phase

MB = 1,000,000 bytes ไม่ใช่ MiB เก็บ raw bytes ใน JSON วัด RAM ทุก 250 ms ระหว่าง operation อาจพลาด peak สั้น ๆ ค่า OS peak รวม startup แยกใน JSON เป็นการวัด Core ผ่าน harness ไม่ใช่ installed Worker.exe โดยตรง ผลนี้ไม่รับประกัน RAM หรือความเร็ว

เวลา upload รวม BackupAsync และบันทึก backup-result JSON; เวลา restore รวม ListSnapshotsAsync และ RestoreAsync รวม OAuth refresh เมื่อจำเป็น ไม่รวม startup, โหลด configuration/key, constructors, hash binary, บันทึกผลวัด หรือ hash ไฟล์หลัง restore นับ media GET ตั้งแต่เริ่มส่งถึงอ่าน body จบหรือ dispose แยก body stream ที่เริ่มหลัง headers ส่วน data PUT นับตั้งแต่เริ่มส่งถึง response headers และแยกการ serialize body จริง

ข้อจำกัด: ผลครอบคลุมข้อมูลสังเคราะห์ 268,468,264 bytes นี้เท่านั้น ไม่อนุมานความเร็วหรือ RAM ระดับ TB ค่า chunks เป็นเพดาน concurrency; stripes และจังหวะ I/O อาจทำให้ค่าที่วัดจริงต่ำกว่าเพดาน สองรอบต่อค่า ใช้เครือข่ายจริง ไม่ flush cache และไม่ระงับโปรเซสอื่น ABBA ลด drift ได้บางส่วน แต่ไม่ตัดความแปรปรวน แต่ละ repository ใช้ key และ ciphertext ใหม่ ข้อมูล plaintext และ SHA-256 เดิมทั้งหมด

ตรวจ production configuration SHA-256 ก่อน/หลังตรงกัน binary ที่ติดตั้ง 780 ไฟล์ตรงกัน และ Task Scheduler XML ตรงกัน เวลา 21:00 เดิม ไม่แก้ configuration/ตารางเวลา ไม่ติดตั้ง ไม่แตะ repository ทดสอบก่อนหน้า ไม่ backup/restore/prune ไฟล์จริงผู้ใช้ อ่าน OAuth ในหน่วยความจำเท่านั้น descriptor ทดสอบตัด OAuth credentials ก่อนบันทึก มี monitor ตรวจ Worker จริงทุก 250 ms เพื่อหยุดเฉพาะ benchmark ถ้างานจริงเริ่ม

หลักฐาน: result.json, candidate-version.json, expected.json, แต่ละรอบ backup/restore/result.json, requests.json (พร้อม stream events), memory.csv และ compatibility-old16-restores-new32/result.json

สถานะโค้ด: constructor concurrency รองรับ 1–32 ค่าเริ่มต้นยัง 1 เปิด 16/32 เฉพาะ harness ชุดทดสอบ Release ผ่าน 167 tests และ solution build ไม่มี warning/error รวม sustained 96 chunks, failure/cancel drain 32 slots และ exclusive gate ไม่มี install/commit/push/release จากงานนี้ หลักฐานดิบอยู่ใน artifacts/thirtytwo-chunk-performance/run-20261108-abba/ (ignored) คลังจำลองใหม่ 4 ชุดยังเก็บสำหรับตรวจผล ประมาณ 1 GiB ของต้นทางรวม ไม่มีการแก้หรือลบคลังสำรองจริง

Candidate Core SHA-256: CDF34B996C61D8D62973F364F1CE0E7B839ADFEB492C1EB4624CC3B35206FE77. ตั้ง upload 32 แต่คำขอ data PUT พร้อมกันจริงสูงสุด 23/25 ในสองรอบ ส่วน download ถึง 32 ทั้งสองรอบ ช่วง serialize body สูงสุด 22 และ response body streams สูงสุด 31 ทุก phase รวม compatibility จบโดย active counters เป็น 0

การตีความ: การเพิ่มเป็น 32 ให้ประโยชน์ด้านเวลาสำรองเฉลี่ยเพียง 6.21% ในชุดนี้ ขณะที่ sampled peak working set สูงสุดเพิ่มประมาณ 103 MB จึงยังไม่ใช่เหตุผลเพียงพอให้เปลี่ยนค่าเริ่มต้น สำหรับ restore ค่า RAM ที่สุ่มวัดของ 32 ต่ำกว่า 16 เล็กน้อยในชุดนี้ แต่การจัดสรร/GC และระยะเวลางานแปรปรวน ไม่ใช่หลักฐานว่าเพิ่ม concurrency แล้วใช้ RAM ต่ำลงเสมอ

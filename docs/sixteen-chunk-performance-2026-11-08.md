# ผลทดลอง Google Drive จริง: 8 เทียบ 16 chunks

ใช้ candidate Core DLL เดียวกันและ harness DLL เดียวกันทั้งสองค่า ทดสอบ ABBA 8,16,16,8 แต่ละรอบเป็น upload และ restore คนละโปรเซส ทำเรียงกัน ไม่มีงาน backup/restore พร้อมกัน ใช้ repository สังเคราะห์ใหม่สี่แห่ง และ restore ลงโฟลเดอร์ใหม่ทุกครั้ง

ใช้ข้อมูลสังเคราะห์เดิมทุก byte: 268,468,264 bytes รวม 4 ไฟล์ (256 MiB + 32 KiB + ข้อความ 40 bytes + ไฟล์ว่าง) ตรวจ SHA-256 และขนาดตรงกันครบ 4/4 ทุก restore อีกทั้ง Core เดิมแบบ 8 chunks อ่าน snapshot ที่สร้างด้วย 16 chunks ได้ครบ 4/4 รอบ compatibility แยกจากค่าเฉลี่ย

| รอบ | งาน | chunks | เวลา (s) | Peak working set (MB) | Peak private (MB) | Mean working set (MB) | Mean private (MB) | PUT/serialize พร้อมกัน | GET/body พร้อมกัน |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|
| A1 | backup | 8 | 42.388 | 264.29 | 223.01 | 191.71 | 150.53 | 8/7 | 1/1 |
| A1 | restore | 8 | 25.013 | 203.65 | 170.30 | 133.64 | 94.85 | 0/0 | 8/7 |
| B1 | backup | 16 | 31.884 | 366.23 | 323.60 | 246.89 | 206.35 | 16/12 | 1/1 |
| B1 | restore | 16 | 17.191 | 325.19 | 309.31 | 147.44 | 111.47 | 0/0 | 16/12 |
| B2 | backup | 16 | 31.540 | 366.78 | 324.19 | 244.65 | 204.25 | 16/13 | 1/1 |
| B2 | restore | 16 | 18.309 | 300.04 | 260.02 | 158.99 | 121.65 | 0/0 | 16/14 |
| A2 | backup | 8 | 46.646 | 275.16 | 232.26 | 200.29 | 159.12 | 8/5 | 1/1 |
| A2 | restore | 8 | 27.548 | 242.33 | 206.48 | 144.67 | 108.17 | 0/0 | 8/8 |

ค่าเฉลี่ยและค่าสูงสุดจากสองรอบต่อค่า:

| งาน | chunks | เวลาเฉลี่ย (s) | Peak working set เฉลี่ย (MB) | Peak private เฉลี่ย (MB) | Working set เฉลี่ย (MB) | Private เฉลี่ย (MB) | Working set สูงสุด (MB) | Private สูงสุด (MB) |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| backup | 8 | 44.517 | 269.72 | 227.64 | 196.00 | 154.83 | 275.16 | 232.26 |
| backup | 16 | 31.712 | 366.51 | 323.89 | 245.77 | 205.30 | 366.78 | 324.19 |
| restore | 8 | 26.280 | 222.99 | 188.39 | 139.16 | 101.51 | 242.33 | 206.48 |
| restore | 16 | 17.750 | 312.62 | 284.67 | 153.22 | 116.56 | 325.19 | 309.31 |

- backup: 8 chunks เฉลี่ย 44.517 s; 16 chunks 31.712 s; ลดเวลา 12.805 s (28.76%). ค่าเฉลี่ย peak working set เปลี่ยน 96.78 MB; peak private เปลี่ยน 96.26 MB.
- restore: 8 chunks เฉลี่ย 26.280 s; 16 chunks 17.750 s; ลดเวลา 8.531 s (32.46%). ค่าเฉลี่ย peak working set เปลี่ยน 89.62 MB; peak private เปลี่ยน 96.28 MB.

| รอบ | งาน | Plaintext throughput (MB/s) | Requests | Transport errors | HTTP 4xx/5xx | PUT payload (bytes) | GET body (bytes) |
|---|---|---:|---:|---:|---:|---:|---:|
| A1 | backup | 6.334 | 344 | 0 | 0 | 268475817 | 64 |
| A1 | restore | 10.733 | 137 | 0 | 0 | 0 | 268481130 |
| B1 | backup | 8.420 | 344 | 0 | 0 | 268475817 | 64 |
| B1 | restore | 15.617 | 137 | 0 | 0 | 0 | 268481130 |
| B2 | backup | 8.512 | 344 | 0 | 0 | 268475817 | 64 |
| B2 | restore | 14.663 | 137 | 0 | 0 | 0 | 268481130 |
| A2 | backup | 5.755 | 344 | 0 | 0 | 268475817 | 64 |
| A2 | restore | 9.746 | 137 | 0 | 0 | 0 | 268481130 |

Payload เป็นข้อมูลที่ส่งผ่าน data PUT และ body ที่อ่านผ่าน media GET รวม ciphertext และ manifest ไม่ใช่ plaintext bytes และไม่รวม HTTP/TLS headers แยก request status และ stream event ใน requests.json ทุก phase

MB = 1,000,000 bytes ไม่ใช่ MiB เก็บ raw bytes ใน JSON วัด RAM ทุก 250 ms ระหว่าง operation อาจพลาด peak สั้น ๆ ค่า OS peak รวม startup แยกใน JSON เป็นการวัด Core ผ่าน harness ไม่ใช่ installed Worker.exe โดยตรง ผลนี้ไม่รับประกัน RAM หรือความเร็ว

เวลา upload รวม BackupAsync และบันทึก backup-result JSON; เวลา restore รวม ListSnapshotsAsync และ RestoreAsync รวม OAuth refresh เมื่อจำเป็น ไม่รวม startup, โหลด configuration/key, constructors, hash binary, บันทึกผลวัด หรือ hash ไฟล์หลัง restore นับ media GET ตั้งแต่เริ่มส่งถึงอ่าน body จบหรือ dispose แยก body stream ที่เริ่มหลัง headers ส่วน data PUT นับตั้งแต่เริ่มส่งถึง response headers และแยกการ serialize body จริง

ข้อจำกัด: ผลครอบคลุมข้อมูลสังเคราะห์ 268,468,264 bytes นี้เท่านั้น ไม่อนุมานความเร็วหรือ RAM ระดับ TB ค่า chunks เป็นเพดาน concurrency; stripes และจังหวะ I/O อาจทำให้ค่าที่วัดจริงต่ำกว่าเพดาน สองรอบต่อค่า ใช้เครือข่ายจริง ไม่ flush cache และไม่ระงับโปรเซสอื่น ABBA ลด drift ได้บางส่วน แต่ไม่ตัดความแปรปรวน แต่ละ repository ใช้ key และ ciphertext ใหม่ ข้อมูล plaintext และ SHA-256 เดิมทั้งหมด

ตรวจ production configuration SHA-256 ก่อน/หลังตรงกัน binary ที่ติดตั้ง 780 ไฟล์ตรงกัน และ Task Scheduler XML ตรงกัน เวลา 21:00 เดิม ไม่แก้ configuration/ตารางเวลา ไม่ติดตั้ง ไม่แตะ repository ทดสอบก่อนหน้า ไม่ backup/restore/prune ไฟล์จริงผู้ใช้ อ่าน OAuth ในหน่วยความจำเท่านั้น descriptor ทดสอบตัด OAuth credentials ก่อนบันทึก มี monitor ตรวจ Worker จริงทุก 250 ms เพื่อหยุดเฉพาะ benchmark ถ้างานจริงเริ่ม

หลักฐาน: result.json, candidate-version.json, expected.json, แต่ละรอบ backup/restore/result.json, requests.json (พร้อม stream events), memory.csv และ compatibility-old8-restores-new16/result.json

สถานะโค้ด: constructor concurrency รองรับ 1–16 ค่าเริ่มต้นยัง 1 เปิด 8/16 เฉพาะ harness ชุดทดสอบ Release ผ่าน 157 tests และ solution build ไม่มี warning/error รวม sustained 48 chunks, failure/cancel drain 16 slots และ exclusive gate ไม่มี install/commit/push/release จากงานนี้ หลักฐานดิบอยู่ใน artifacts/sixteen-chunk-performance/run-20261108-abba/ (ignored) คลังจำลองใหม่ 4 ชุดยังเก็บสำหรับตรวจผล ประมาณ 1 GiB ของต้นทางรวม ไม่มีการแก้หรือลบคลังสำรองจริง

Candidate Core SHA-256: 2919C8039584BA5B24C21B20B37577F1B8C91032A3A9F93487C0C306E60438A4. คำขอ media ที่ยังไม่เสร็จสูงสุด 16 ทั้ง upload/download ส่วน body serialization สูงสุด 13 และ response body streams สูงสุด 14 ไม่อ้างว่า WAN ส่ง 16 ก้อนเต็มแบนด์วิดท์พร้อมกันตลอดเวลา

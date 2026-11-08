# ผลทดลอง Google Drive จริง: 4 เทียบ 8 chunks

ใช้ candidate Core DLL เดียวกันและ harness DLL เดียวกันทั้งสองค่า ทดสอบ ABBA 4,8,8,4 แต่ละรอบเป็น upload และ restore คนละโปรเซส ทำเรียงกัน ไม่มีงาน backup/restore พร้อมกัน ใช้ repository สังเคราะห์ใหม่สี่แห่ง และ restore ลงโฟลเดอร์ใหม่ทุกครั้ง

ใช้ข้อมูลสังเคราะห์เดิมทุก byte: 268,468,264 bytes รวม 4 ไฟล์ (256 MiB + 32 KiB + ข้อความ 40 bytes + ไฟล์ว่าง) ตรวจ SHA-256 และขนาดตรงกันครบ 4/4 ทุก restore อีกทั้ง Core เดิมแบบ 4 chunks อ่าน snapshot ที่สร้างด้วย 8 chunks ได้ครบ 4/4 รอบ compatibility แยกจากค่าเฉลี่ย

| รอบ | งาน | chunks | เวลา (s) | Peak working set (MB) | Peak private (MB) | Mean working set (MB) | Mean private (MB) | PUT/serialize พร้อมกัน | GET/body พร้อมกัน |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|
| A1 | backup | 4 | 67.666 | 198.88 | 156.74 | 135.04 | 100.74 | 4/3 | 1/1 |
| A1 | restore | 4 | 43.457 | 158.72 | 121.43 | 103.80 | 63.86 | 0/0 | 4/4 |
| B1 | backup | 8 | 43.456 | 264.80 | 222.91 | 179.89 | 138.44 | 8/7 | 1/1 |
| B1 | restore | 8 | 24.780 | 248.63 | 224.15 | 120.18 | 81.87 | 0/0 | 8/7 |
| B2 | backup | 8 | 42.921 | 264.88 | 223.89 | 195.69 | 154.83 | 8/7 | 1/1 |
| B2 | restore | 8 | 25.411 | 275.80 | 252.48 | 119.40 | 81.98 | 0/0 | 8/7 |
| A2 | backup | 4 | 68.793 | 151.50 | 109.10 | 116.05 | 73.95 | 4/4 | 1/1 |
| A2 | restore | 4 | 38.441 | 197.73 | 159.69 | 119.70 | 80.40 | 0/0 | 4/4 |

ค่าเฉลี่ยและค่าสูงสุดจากสองรอบต่อค่า:

| งาน | chunks | เวลาเฉลี่ย (s) | Peak working set เฉลี่ย (MB) | Peak private เฉลี่ย (MB) | Working set เฉลี่ย (MB) | Private เฉลี่ย (MB) | Working set สูงสุด (MB) | Private สูงสุด (MB) |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| backup | 4 | 68.230 | 175.19 | 132.92 | 125.55 | 87.35 | 198.88 | 156.74 |
| backup | 8 | 43.189 | 264.84 | 223.40 | 187.79 | 146.64 | 264.88 | 223.89 |
| restore | 4 | 40.949 | 178.23 | 140.56 | 111.75 | 72.13 | 197.73 | 159.69 |
| restore | 8 | 25.096 | 262.22 | 238.31 | 119.79 | 81.92 | 275.80 | 252.48 |

- backup: 4 chunks เฉลี่ย 68.230 s; 8 chunks 43.189 s; ลดเวลา 25.041 s (36.70%). ค่าเฉลี่ย peak working set เปลี่ยน 89.65 MB; peak private เปลี่ยน 90.48 MB.
- restore: 4 chunks เฉลี่ย 40.949 s; 8 chunks 25.096 s; ลดเวลา 15.853 s (38.71%). ค่าเฉลี่ย peak working set เปลี่ยน 83.99 MB; peak private เปลี่ยน 97.76 MB.

| รอบ | งาน | Plaintext throughput (MB/s) | Requests | Transport errors | HTTP 4xx/5xx | PUT payload (bytes) | GET body (bytes) |
|---|---|---:|---:|---:|---:|---:|---:|
| A1 | backup | 3.968 | 344 | 0 | 0 | 268475815 | 64 |
| A1 | restore | 6.178 | 137 | 0 | 0 | 0 | 268481126 |
| B1 | backup | 6.178 | 344 | 0 | 0 | 268475815 | 64 |
| B1 | restore | 10.834 | 137 | 0 | 0 | 0 | 268481126 |
| B2 | backup | 6.255 | 344 | 0 | 0 | 268475815 | 64 |
| B2 | restore | 10.565 | 137 | 0 | 0 | 0 | 268481126 |
| A2 | backup | 3.903 | 344 | 0 | 0 | 268475815 | 64 |
| A2 | restore | 6.984 | 137 | 0 | 0 | 0 | 268481126 |

Payload เป็นข้อมูลที่ส่งผ่าน data PUT และ body ที่อ่านผ่าน media GET รวม ciphertext และ manifest ไม่ใช่ plaintext bytes และไม่รวม HTTP/TLS headers แยก request status และ stream event ใน requests.json ทุก phase

MB = 1,000,000 bytes ไม่ใช่ MiB เก็บ raw bytes ใน JSON วัด RAM ทุก 250 ms ระหว่าง operation อาจพลาด peak สั้น ๆ ค่า OS peak รวม startup แยกใน JSON เป็นการวัด Core ผ่าน harness ไม่ใช่ installed Worker.exe โดยตรง ผลนี้ไม่รับประกัน RAM หรือความเร็ว

เวลา upload รวม BackupAsync และบันทึก backup-result JSON; เวลา restore รวม ListSnapshotsAsync และ RestoreAsync รวม OAuth refresh เมื่อจำเป็น ไม่รวม startup, โหลด configuration/key, constructors, hash binary, บันทึกผลวัด หรือ hash ไฟล์หลัง restore นับ media GET ตั้งแต่เริ่มส่งถึงอ่าน body จบหรือ dispose แยก body stream ที่เริ่มหลัง headers ส่วน data PUT นับตั้งแต่เริ่มส่งถึง response headers และแยกการ serialize body จริง

ข้อจำกัด: สองรอบต่อค่า ใช้เครือข่ายจริง ไม่ flush cache และไม่ระงับโปรเซสอื่น ABBA ลด drift ได้บางส่วน แต่ไม่ตัดความแปรปรวน แต่ละ repository ใช้ key และ ciphertext ใหม่ ข้อมูล plaintext และ SHA-256 เดิมทั้งหมด

ตรวจ production configuration SHA-256 ก่อน/หลังตรงกัน binary ที่ติดตั้ง 780 ไฟล์ตรงกัน และ Task Scheduler XML ตรงกัน เวลา 21:00 เดิม ไม่แก้ configuration/ตารางเวลา ไม่ติดตั้ง ไม่แตะ repository ทดสอบก่อนหน้า ไม่ backup/restore/prune ไฟล์จริงผู้ใช้ อ่าน OAuth ในหน่วยความจำเท่านั้น descriptor ทดสอบตัด OAuth credentials ก่อนบันทึก มี monitor ตรวจ Worker จริงทุก 250 ms เพื่อหยุดเฉพาะ benchmark ถ้างานจริงเริ่ม

หลักฐาน: result.json, candidate-version.json, expected.json, แต่ละรอบ backup/restore/result.json, requests.json (พร้อม stream events), memory.csv และ compatibility-old4-restores-new8/result.json

สถานะโค้ด: constructor concurrencyรองรับ1–8 ค่าเริ่มต้นยัง1 เปิด4/8เฉพาะharness ชุดทดสอบRelease147testsผ่าน solutionbuild0warnings/errors รวมsustained24chunks/failure/cancel8slots/exclusivegate ไม่มีinstall/commit/push/releaseจากงานนี้ หลักฐานดิบข้างต้นอยู่ใน artifacts/eight-chunk-performance/run-20261108-abba/ (ignored) คลังจำลองใหม่4ชุดยังเก็บสำหรับตรวจผล ประมาณ1GiBของต้นทางรวม ไม่มีการแก้หรือลบคลังสำรองจริง

CandidateCore SHA256: 4D479C2A77009C6C6622A52EA06222133CF0D1FFF786F63EF74E55D3620D066C. ตัวนับคำขอที่ยังทำไม่เสร็จสูงสุด8ทั้งupload/download; ช่วงserializebodyและresponsebodystreamเปิดพร้อมกันที่สังเกตได้สูงสุด7ในสองรอบ8ก้อน จึงไม่อ้างว่าWANส่ง8ก้อนเต็มแบนด์วิดท์พร้อมกันตลอดเวลา.

# ผลทดลอง Google Drive จริง: 2 เทียบ 4 chunks

ใช้ candidate Core DLL เดียวกันและ harness DLL เดียวกันทั้งสองค่า ทดสอบ ABBA 2,4,4,2 แต่ละรอบเป็น upload และ restore คนละโปรเซส ทำเรียงกัน ไม่มีงาน backup/restore พร้อมกัน ใช้ repository สังเคราะห์ใหม่สี่แห่ง และ restore ลงโฟลเดอร์ใหม่ทุกครั้ง

ใช้ข้อมูลสังเคราะห์เดิมทุก byte: 268,468,264 bytes รวม 4 ไฟล์ (256 MiB + 32 KiB + ข้อความ 40 bytes + ไฟล์ว่าง) ตรวจ SHA-256 และขนาดตรงกันครบ 4/4 ทุก restore อีกทั้ง Core เดิมแบบ 2 chunks อ่าน snapshot ที่สร้างด้วย 4 chunks ได้ครบ 4/4 รอบ compatibility แยกจากค่าเฉลี่ย

| รอบ | งาน | chunks | เวลา (s) | Peak working set (MB) | Peak private (MB) | Mean working set (MB) | Mean private (MB) | PUT/serialize พร้อมกัน | GET/body พร้อมกัน |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|
| A1 | backup | 2 | 119.788 | 111.67 | 69.34 | 90.95 | 49.24 | 2/2 | 1/1 |
| A1 | restore | 2 | 71.719 | 157.12 | 117.49 | 100.68 | 61.12 | 0/0 | 2/2 |
| B1 | backup | 4 | 68.675 | 153.76 | 111.37 | 112.26 | 70.17 | 4/4 | 1/1 |
| B1 | restore | 4 | 41.022 | 245.39 | 221.05 | 129.11 | 90.56 | 0/0 | 4/3 |
| B2 | backup | 4 | 69.990 | 160.33 | 118.24 | 124.09 | 82.09 | 4/4 | 1/1 |
| B2 | restore | 4 | 40.199 | 157.88 | 121.67 | 111.05 | 72.38 | 0/0 | 4/4 |
| A2 | backup | 2 | 119.257 | 109.21 | 65.61 | 90.39 | 47.41 | 2/2 | 1/1 |
| A2 | restore | 2 | 78.450 | 125.67 | 86.00 | 89.81 | 49.32 | 0/0 | 2/2 |

ค่าเฉลี่ยและค่าสูงสุดจากสองรอบต่อค่า:

| งาน | chunks | เวลาเฉลี่ย (s) | Peak working set เฉลี่ย (MB) | Peak private เฉลี่ย (MB) | Working set เฉลี่ย (MB) | Private เฉลี่ย (MB) | Working set สูงสุด (MB) | Private สูงสุด (MB) |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| backup | 2 | 119.522 | 110.44 | 67.48 | 90.67 | 48.33 | 111.67 | 69.34 |
| backup | 4 | 69.333 | 157.04 | 114.80 | 118.18 | 76.13 | 160.33 | 118.24 |
| restore | 2 | 75.085 | 141.39 | 101.75 | 95.24 | 55.22 | 157.12 | 117.49 |
| restore | 4 | 40.611 | 201.64 | 171.36 | 120.08 | 81.47 | 245.39 | 221.05 |

- backup: 2 chunks เฉลี่ย 119.522 s; 4 chunks 69.333 s; ลดเวลา 50.190 s (41.99%). ค่าเฉลี่ย peak working set เปลี่ยน 46.60 MB; peak private เปลี่ยน 47.33 MB.
- restore: 2 chunks เฉลี่ย 75.085 s; 4 chunks 40.611 s; ลดเวลา 34.474 s (45.91%). ค่าเฉลี่ย peak working set เปลี่ยน 60.24 MB; peak private เปลี่ยน 69.62 MB.

MB = 1,000,000 bytes ไม่ใช่ MiB เก็บ raw bytes ใน JSON วัด RAM ทุก 250 ms ระหว่าง operation อาจพลาด peak สั้น ๆ ค่า OS peak รวม startup แยกใน JSON เป็นการวัด Core ผ่าน harness ไม่ใช่ installed Worker.exe โดยตรง ผลนี้ไม่รับประกัน RAM หรือความเร็ว

เวลา upload รวม BackupAsync และบันทึก backup-result JSON; เวลา restore รวม ListSnapshotsAsync และ RestoreAsync รวม OAuth refresh เมื่อจำเป็น ไม่รวม startup, โหลด configuration/key, constructors, hash binary, บันทึกผลวัด หรือ hash ไฟล์หลัง restore นับ media GET ตั้งแต่เริ่มส่งถึงอ่าน body จบหรือ dispose แยก body stream ที่เริ่มหลัง headers ส่วน data PUT นับตั้งแต่เริ่มส่งถึง response headers และแยกการ serialize body จริง

ข้อจำกัด: สองรอบต่อค่า ใช้เครือข่ายจริง ไม่ flush cache และไม่ระงับโปรเซสอื่น ABBA ลด drift ได้บางส่วน แต่ไม่ตัดความแปรปรวน แต่ละ repository ใช้ key และ ciphertext ใหม่ ข้อมูล plaintext และ SHA-256 เดิมทั้งหมด

ตรวจ production configuration SHA-256 ก่อน/หลังตรงกัน binary ที่ติดตั้ง 780 ไฟล์ตรงกัน และ Task Scheduler XML ตรงกัน ไม่แก้ configuration/ตารางเวลา ไม่ติดตั้ง ไม่แตะเจ็ด repository ทดสอบก่อนหน้า ไม่ backup/restore/prune ไฟล์จริงผู้ใช้ อ่าน OAuth ในหน่วยความจำเท่านั้น descriptor ทดสอบตัด OAuth credentials ก่อนบันทึก

หลักฐาน: result.json, candidate-version.json, expected.json, แต่ละรอบ backup/restore/result.json, requests.json (พร้อม stream events), memory.csv และ compatibility-old2-restores-new4/result.json

สถานะโค้ด: constructor optionsรองรับ1–4 ค่าเริ่มต้นยัง1 เปิด2/4เฉพาะharness Release137testsผ่าน solutionbuild0warnings/errors รวมfailure/cancelทุก4slotและexclusivegate ไม่มีinstall/commit/push/releaseจากการทดลอง ตารางจริงยังEnabledวันละครั้ง21:00 หลักฐานดิบตามรายการข้างต้นอยู่ใน artifacts/four-chunk-performance/run-20261108-abba/ (ignored) คลังจำลองใหม่4ชุดยังเก็บไว้สำหรับตรวจผล รวมข้อมูลต้นทางประมาณ1GiB และไม่มีการแตะชุดสำรองจริง

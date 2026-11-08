# ผลทดลอง Restore จาก Google Drive จริง

ทดสอบ ABBA กับ Core เดิมที่ติดตั้งและ candidate ที่เปิด `maxConcurrentRestoreChunks=2` ใช้ harness DLL เดียวกันทั้งสี่รอบ อ่าน snapshot สังเคราะห์เดียวกันจาก repository round-4 และ restore ลงโฟลเดอร์ใหม่ทุกครั้ง ข้อมูลรวม 268,468,264 bytes (4 ไฟล์) ตรวจ SHA-256 และขนาดครบทุกไฟล์ทุกรอบ

| รอบ | เวลา operation (วินาที) | Peak working set ที่สุ่มวัด (MB) | Peak private bytes ที่สุ่มวัด (MB) | Media GET / body stream พร้อมกันสูงสุด | SHA-256 |
|---|---:|---:|---:|---:|---|
| A1 | 117.259 | 136.90 | 110.69 | 1 / 1 | ผ่าน 4/4 |
| B1 | 62.145 | 194.51 | 163.44 | 2 / 2 | ผ่าน 4/4 |
| B2 | 57.817 | 157.32 | 123.73 | 2 / 2 | ผ่าน 4/4 |
| A2 | 108.385 | 130.12 | 99.47 | 1 / 1 | ผ่าน 4/4 |

ค่าเฉลี่ย Core เดิม: 112.822 วินาที; candidate: 59.981 วินาที ผลต่าง 52.841 วินาที (46.84%) ค่าเฉลี่ย sampled peak private bytes ของ candidate เปลี่ยน 38.51 MB; sampled peak working set เปลี่ยน 42.41 MB

หน่วย MB = 1,000,000 bytes; MiB = 1,048,576 bytes เก็บค่า bytes จริงใน JSON วัด RAM ทุก 250 ms ระหว่าง operation จึงอาจพลาด peak ช่วงสั้น และไม่ใช่การวัด installed Worker.exe โดยตรง ค่า OS peak ที่รวม startup แยกรายงานใน JSON

เวลา operation รวม ListSnapshotsAsync, RestoreAsync และ OAuth refresh เมื่อจำเป็น ไม่รวม startup, โหลด configuration/key, constructors, hash binary, บันทึกผล หรือ hash ไฟล์หลัง restore นับ media GET ตั้งแต่เริ่มส่งคำขอจนอ่าน body จบ (EOF) หรือ dispose stream จึงครอบคลุม body จริง ไม่หยุดนับเมื่อรับ headers แยกค่า body stream ที่เปิดพร้อมกันหลังรับ headers ถึง EOF/dispose โดยไม่รวมคำขอที่ยังรอ headers

ข้อจำกัด: มีเพียงสองรอบต่อรุ่น ใช้เครือข่ายจริงและ snapshot เดิม จึงมีความแปรปรวนและผลจาก cache ของ Google/เครือข่าย/Windows ไม่ได้ flush cache หรือระงับโปรเซสอื่น ABBA ลด drift ได้บางส่วน ผลนี้ไม่ใช่การรับประกันความเร็วหรือ RAM

ตรวจ SHA-256 configuration และ binary ที่ติดตั้ง 780 ไฟล์ก่อน/หลังตรงกัน ตรวจ Task Scheduler XML ก่อน/หลังตรงกัน ใช้ OAuth ในหน่วยความจำ ไม่บันทึก token หรือ configuration ไม่ upload, prune, install หรือ restore ไฟล์จริงของผู้ใช้

หลักฐาน: `result.json`, `A1/B1/B2/A2/result.json`, `requests.json` พร้อม event EOF/dispose และ `memory.csv` ในแต่ละรอบ

สถานะ 8 พฤศจิกายน 2026: เป็นโค้ดทดลอง ยังไม่ deploy ค่า constructor maxConcurrentRestoreChunks เริ่มต้น1 เปิด2เฉพาะ harness ใช้ Queue จำกัดสอง tasks พร้อมเขียนไฟล์ตามลำดับ ยกเลิกและรอทุก task เมื่อผิดพลาด รักษา path safety/ไม่เขียนทับ และลบไฟล์ที่กู้คืนไม่ครบ ชุดทดสอบ Release ผ่าน125 tests และ solution build0warnings/errors รวม6chunks/out-of-order/corruption/cancellation/no-overwrite ตารางจริงยัง Enabled ทุกวัน21:00

หลักฐานดิบข้างต้นอยู่ที่ artifacts/restore-performance/run-20261108-abba/ (ignored) ไม่มี credentials ในรายงานนี้ ไม่มีการสร้างหรือติดตั้ง installer ใหม่จากงานทดลองนี้

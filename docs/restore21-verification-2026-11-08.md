# กู้คืนชุดรอบ 21:00 ด้วยแอปจริง — 8 พฤศจิกายน 2026

ผู้ใช้กดเองตามขั้นตอน: ประวัติและกู้คืน > รีเฟรช > เลือก 08 พ.ย. 2569 21:00 (SCR9,46ไฟล์,22.26GB) > กู้คืนชุดที่เลือก > เลือกโฟลเดอร์ใหม่ว่าง C:/Users/USER/Documents/PCAutobackup-Restore-2100-20261108 > Select Folder ปุ่ม Select Folder เริ่มงานทันที ผู้ใช้ซ่อนแล้วเปิดหน้าต่างงานเดิมอีกครั้ง งานยังต่อเนื่อง ไม่มีการกดเริ่มซ้ำ

Snapshot20261108140034303-6e3ab895d8c2444096256d3245038234 กู้คืนครบ46ไฟล์ รวม22,258,505,892bytes ไม่มีไฟล์ขาดหรือเกิน SHA-256ไฟล์กู้คืนตรงกับsource baseline46/46 และsourceที่อ่านซ้ำหลังงาน46/46 ต้นฉบับก่อน/หลังไม่เปลี่ยนทั้ง46ไฟล์ ทั้งsourceและrestored recompute chunk HMAC/lengthตรงauthenticated manifest46/46 ตรวจเสร็จ21:40:28 โดยไม่เขียนต้นฉบับ ไม่มีการอ่านชื่อไฟล์หรือเนื้อหาลงรายงาน

## เวลาและRAM

ไฟล์แรกสร้าง21:22:32.442 พบจำนวนและขนาดไฟล์ครบ21:36:26.900 ช่วงการเขียนที่สังเกตได้834.457วินาที (13นาที54วินาที) คิดplaintext throughput26.67MB/s (213.39Mbps) ไม่ใช่เวลาตั้งแต่กดปุ่มหรือเวลาRestoreAsyncที่จับโดยตรง ไม่รวมเตรียมก่อนสร้างไฟล์แรกและอาจไม่ครอบคลุมfinalizationหลังขนาดครบ มีpoll1วินาทีและไม่ได้จับnetwork traffic จึงใช้เป็นประมาณการเฉพาะช่วงเขียนไฟล์ ไม่ใช่ความเร็วwire download

เก็บRAMของinstalled Desktop process PID5156ทุกประมาณ250ms ก่อนและระหว่างrestore:
- Windows process lifetime PeakWorkingSet64:383,000,576bytes=383.00decimal MB รวมUIและงานก่อนหน้าในprocessเดียวกัน ไม่ใช่restore-only allocation
- Sampled peakWorkingSetในช่วงไฟล์แรกถึงขนาดครบ:370,335,744bytes=370.34MB
- Sampled peakprivateในช่วงเดียวกัน:439,726,080bytes=439.73MB
- 3029samplesในช่วงเขียน ไม่รวมTray/hashing harness ไม่มีการวัดHTTP concurrencyจริงของรอบนี้ Source baselineอ่านระหว่างrestore (ผู้ใช้กดเริ่มก่อนbaselineจบ) และsource rehashหลังงานยืนยันไม่เปลี่ยนทั้ง46ไฟล์ การอ่านhashพร้อมกันอาจเพิ่มdisk contention

## คงค่าเดิม

Installed CoreยังEDD26B928208655A887097387E4C28CC1E7B44AC50A76509F5D45BBB3640245D production backup16/restore16 ไม่มีเปลี่ยนbinaryหรือrelease ConfigurationSHAและSchedulerXMLก่อน/หลังตรงกัน ตารางรอบถัดไป9พ.ย.2026เวลา21:00 ผลดิบอยู่artifacts/restore21-verification/ (ignored) งานนี้ได้รับอนุญาตชัดเจนให้restoreและhashไฟล์จริง ไม่มีการลบหรือเขียนทับต้นฉบับ ไม่ได้commit/pushในงานตรวจนี้
ตรวจข้อความจากหน้าต่างแอปหลังงานแล้ว: “กู้คืนสำเร็จ · 46 ไฟล์ · 22.26 GB” ปุ่มรีเฟรชกลับมาใช้งานได้ ยืนยันผลจาก UI เพิ่มเติมจากจำนวนไฟล์และSHA-256 ผลรายงานนี้เผยแพร่ตามคำขอถัดมา โดยไม่รวมผลดิบ ชื่อไฟล์ผู้ใช้ credentials หรือไฟล์กู้คืนในGit

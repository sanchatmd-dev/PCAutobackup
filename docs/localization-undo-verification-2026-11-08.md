# ผลตรวจปุ่มนำออกและ Undo

วันที่ตรวจและติดตั้ง: 8 พฤศจิกายน 2026 เวลาไทย หลังรอบสำรอง 21:00 ของวันนั้น

## ปัญหาและการแก้ไข

หน้าโฟลเดอร์ต้นทางของรุ่นติดตั้งเดิมแสดง `System.Windows.ResourceReferenceExpression` แทนข้อความปุ่มนำออก ทั้งภาพผู้ใช้และ accessibility tree ของแอปจริงพบอาการนี้ `LocalizeExtension` คืน dynamic resource expression ระหว่าง WPF เตรียม DataTemplate ก่อนมี control จริง ทำให้ expression กลายเป็น Content ของปุ่ม

แก้ Sources DataTemplate ให้ใช้ DynamicResource โดยตรง แก้ markup-extension return-type metadata เป็น object และให้ extension คืนตัวเองเมื่อ target ยังไม่ใช่ DependencyObject จึงเลื่อนการ resolve จนมี control จริง ภาษาไทยแสดง **นำออก** ภาษาอังกฤษแสดง **Remove**

เพิ่มแถบ **เลิกทำ / Undo** หลังนำโฟลเดอร์ออก คืน ID, พาธ, ชื่อ และลำดับเดิม เก็บสูงสุด 20 รายการในหน่วยความจำของหน้าตั้งค่าครั้งนั้น ไม่อ่านหรือคัดลอกเนื้อหาไฟล์ ไม่มี timer ประวัติอยู่ข้ามการเปลี่ยนภาษา แต่ล้างเมื่อรีเฟรช configuration หรือปิดหน้าตั้งค่า ป้องกันการคืนซ้ำกับ ID หรือ canonical path ที่มีอยู่แล้ว

Remove บันทึกประวัติหลัง save สำเร็จเท่านั้น Undo ลบประวัติหลัง save สำเร็จเท่านั้น หาก save ล้มเหลวจะ rollback รายการในหน่วยความจำและเก็บรายการ Undo ไว้

## การตรวจอัตโนมัติและหน้าจอจริง

- Solution Release build สำเร็จ ไม่มี warning/error; tests ผ่าน **186/186** รวมกรณี Undo หลายรายการ, ลำดับ, ID/path ซ้ำ, สำเนาประวัติ, ขอบเขต 20 รายการ, clear และเก็บประวัติเมื่อ save ยังไม่สำเร็จ
- STA harness โหลด compiled MainWindow และ realize container ของ Sources ItemsControl รุ่นเดิม พบ Content type เป็น `System.Windows.ResourceReferenceExpression` ทุกช่วงไทย→อังกฤษ→ไทย การเรียก `DataTemplate.LoadContent` เดี่ยว ๆ ไม่ทำให้เกิดอาการ ต้องสร้าง container จริง
- Harness เดียวกันกับ candidate พบ `System.String` และข้อความ **นำออก→Remove→นำออก** โดย ID binding และ click handler เดิมยังถูกต้อง รวมทั้งมี Undo handler ใหม่
- ทดสอบหน้าต่าง WPF จริงกับ configuration แยกและโฟลเดอร์จำลองสองรายการ: นำ A ออก, Undo ไทย, นำ A ออกอีกครั้ง, สลับอังกฤษ, Undo อังกฤษ, สลับกลับไทย รายการ A/B กลับลำดับเดิมพร้อม ID/path/label ครบ
- SHA-256 และขนาดไฟล์ต้นฉบับจำลองตรงก่อนทดสอบ **2/2** Configuration จำลองกลับตรงทุก field ยกเว้น Revision ที่เพิ่มตามการบันทึก
- ไม่กดนำออกกับโฟลเดอร์จริงของผู้ใช้ และไม่สำรอง อัปโหลด กู้คืน หรือล้างข้อมูลจริงในการทดสอบนี้

## การติดตั้งและค่าที่รักษาไว้

Publish ชุด self-contained win-x64 แล้วติดตั้งด้วย install.ps1 ของ candidate โดยไม่เปิดหน้าตั้งค่าอัตโนมัติ เทียบ SHA-256 ไฟล์ที่คาดหวังในชุด publish กับไฟล์ติดตั้ง **814/814** ตรงทั้งหมด ไม่มี mismatch การตรวจนี้ไม่ได้อ้างว่าไม่มีไฟล์ส่วนเกินในโฟลเดอร์ติดตั้ง

Installed Core SHA-256: `B088C52946F8B46E379AB8911CAB6734CE4217673F44EE6BBA3C842CE87C7587`

SHA-256 configuration จริง, startup Run key และ Task Scheduler XML ก่อน–หลังคงเดิม ตารางยังเปิดทุกวัน **21:00** รอบถัดไปขณะตรวจคือ **9 พฤศจิกายน 2026 21:00 +07:00** ApplicationService ยังคง concurrency สำรองและกู้คืน **16/16** ไม่มีการเปลี่ยน engine หรือรูปแบบชุดสำรองในงานนี้ ปิด Tray ของ fixture แล้วเปิด Tray จากโฟลเดอร์ติดตั้งจริง

ผลดิบและ harness อยู่ใน ignored `artifacts/localization-button-verification` ไม่มี credentials หรือ configuration จริงอยู่ในรายงานนี้ ยังไม่ได้ commit/push, สร้างตัวติดตั้งไฟล์เดียวใหม่ หรืออัปเดต GitHub Release

หลังปิดหน้าตั้งค่าทดสอบ เหลือ Tray ที่ติดตั้งจริงหนึ่ง process ตัวอย่าง RAM ขณะพักหนึ่งครั้ง: Working Set 37.40 MB, Private Memory 9.57 MB (หน่วยฐานสิบ) เป็นการวัดหลังติดตั้ง ไม่ใช่การเปรียบเทียบก่อน–หลังหรือคำรับประกัน RAM ทุก workload

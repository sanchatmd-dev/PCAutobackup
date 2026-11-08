# Google Drive progress และ RAM หลังงาน — 8 พฤศจิกายน 2026

ทดสอบactualinstalled WPF Core86288AA8F8083A19BBE96B2DBF29E55DB20B3E652516E7890CE3524DC746B5B2 PID2944 ใช้configurationและrepositoryGoogleDriveจำลองใหม่แยกจากคลังจริง Source random512MiB+small32KiB+emptyรวม536,903,680bytes3ไฟล์ schedule/inventoryfalseเฉพาะtestconfig Rootกดbackupและrestoreผ่านUIจริง ไม่อ่านหรือสำรองsourceส่วนตัวผู้ใช้

## Progress บน Drive

เริ่มเตรียมแสดง--%/--MBs/ETA-- ระหว่างuploadเห็น77% ·13.74MB/s ·ETA10s, bar76.5578% และlogicalbytes411.04MBจาก536.90MB: remainingประมาณ125.86MB/13.74MB/s=9.16s ปัดขึ้น10s สอดคล้องกับสูตร Backupสำเร็จ3filesเริ่ม22:35:43.869 จบ22:36:29.707 รวม45.838s หน้าจอสุดท้าย100% ·11.70MB/s ·ETA0 (UItimerเริ่มก่อนstatusเล็กน้อย) RestoreจากDriveสำเร็จ3files จบ100% ·21.94MB/s ·ETA0; ไม่ได้เก็บตัวอย่างกลางrestoreในรอบนี้

ความเร็วเป็นlogicalprocessedbytes/เวลาelapsedทั้งงาน รวมตรวจและcrypto ไม่ใช่wireupload/download โดยตรง ETAเป็นค่าประมาณส่วนข้อมูลที่เหลือ ไม่รับประกันเวลาขั้นfinalize/metadata ชุดนี้ยืนยันค่าบนGoogleDriveจริงเพิ่มเติมจากlocaltestและunitmath ไม่ใช่การวัดactualHTTPconcurrency

SHA-256 generation/source/restoredตรง3/3 ทั้งขนาดและเนื้อหา ไม่มีไฟล์ขาด/เกิน ตรวจเสร็จ22:40:06 Snapshot20261108153549826-ca267daa6aea47fcb5f7fcd81c12a560

## RAM ตามจริง โดยไม่บังคับGC/trim

| สถานะ | WorkingSet MB | PrivateMemory MB |
|---|---:|---:|
| เปิดtestUIพักก่อนงาน30วินาที |92.92|71.57|
| หลังbackupสำเร็จประมาณ18วินาที เปิดUI |198.25|249.83|
| หลังbackupสำเร็จประมาณ77วินาที เปิดUI |128.64|248.32|
| หลังrestoreสำเร็จที่สังเกตครั้งแรก เปิดUI |255.79|159.22|
| อีก85วินาทีหลังสังเกตrestoreสำเร็จ เปิดUI |163.64|157.30|
| ปิดtestUIและtestTrayแล้ว เหลือmainTrayอย่างเดียว |1.38|8.63|

MainTrayก่อนทดสอบWS2.33MB/private8.63MB; หลังทดสอบprivateเท่ากัน8,626,176bytes Desktop0 Worker0 Tray1 การปิดหน้าต่างหลังงานทำให้Desktopprocessออกและหน่วยความจำของprocessถูกคืน แต่การเปิดUIค้างไว้ไม่รับประกันว่าค่าprivateกลับbaselineทันที .NET/GCอาจเก็บallocationsไว้ การวัดนี้ไม่เพียงพอวินิจฉัยmemoryleakหรือพิสูจน์ไม่มีleakระยะยาว WSลดได้จากWindowspaging จึงต้องแยกจากprivate

เก็บDesktop2944ทุกประมาณ250msรวม1348samples ช่วงlifetimeนี้OSpeakWS442.27MB, sampledpeakWS431.03MB/private382.57MB ไม่รวมTray/helper และไม่รับประกันทุกworkload ไม่มีการบังคับGCหรือemptyworkingsetเพื่อให้ค่าต่ำ

## Invariants

ConfigurationหลักและSchedulerXMLก่อน/หลังตรงกัน คง16/16และตาราง21:00 ไม่ติดตั้งหรือแก้code/Releaseในงานนี้ ตัวทดสอบใช้บัญชีOAuthเดิมเฉพาะคลังจำลอง เก็บtestkey/tokensแบบDPAPIในignoredtestconfig ไม่บันทึกsecretsในรายงาน ข้อมูลจำลองและหลักฐานอยู่artifacts/drive-progress-idle-verification/ (ignored) ไม่ได้commit/pushจากงานนี้
# ทดสอบ Release progress ผ่านตัวติดตั้งและ WPF จริง — 8 พฤศจิกายน 2026

ใช้ตัวติดตั้ง PCAutobackup-win-x64.exe v2026.11.08-progress SHA256 D2DB787E82C765233477EACDE66988E2E0AC1FB1EEF5F9E93C0AD5B70DECD926 ติดตั้งทับแอปที่มีอยู่สำเร็จ exit0 เวลา22:13:46 Coreที่ติดตั้งตรงrelease86288AA8F8083A19BBE96B2DBF29E55DB20B3E652516E7890CE3524DC746B5B2 ก่อนติดตั้งเป็นรุ่นprogressเดียวกันที่buildก่อนcommit (91ECA…) จึงเป็นsame-feature reinstall ไม่ใช่ทดสอบupgradeจากรุ่นก่อนมีprogress

สร้างข้อมูลจำลอง random2GiB+32KiB+empty รวม2,147,516,416bytes3ไฟล์ ใช้ค่าตั้งแยกและlocal encrypted repositoryในartifacts ไม่ใช้ไฟล์จริงของผู้ใช้ ไม่ใช้Driveจริง ปิดscheduleเฉพาะconfigทดสอบ ไม่เปลี่ยนconfigหลัก ทดสอบโดยกดสำรอง/กู้คืนจากWPFที่ติดตั้งจริง (PID2688) ไปโฟลเดอร์ว่างใหม่

## ผลหน้าจอ

- สำรองระหว่างไฟล์ screenshotเห็น57% ·157.74MB/s ·ETA00:00:06 พร้อมข้อมูลประมาณ1.22GBจาก2.15GB; accessibilityก่อนหน้านั้นเห็น19% ·123.26MB/s ·ETA15s
- สำรองสำเร็จ3ไฟล์ จบ100% ·161.17MB/s ·ETA00:00:00; statusเริ่ม22:17:09.599 สำเร็จ22:17:22.874 รวม13.274s แต่UItimerเริ่มก่อนstatus จึงไม่ใช่ช่วงเวลาเดียวกันกับMB/sบนหน้าจอ
- กู้คืนระหว่างไฟล์ screenshotเห็น12% ·103.74MB/s ·ETA19s พร้อม268.44MB และต่อมา62% ·142.82MB/s ·ETA6s พร้อม1.34GB
- กู้คืนสำเร็จ3ไฟล์ จบ100% ·147.52MB/s ·ETA00:00:00

เปอร์เซ็นต์และETAสอดคล้องโดยประมาณกับlogicalbytesรวมและaverageprocessed speed เช่น12.5%ของยอดรวมประมาณ268.44MB และremainingประมาณ1.88GB/103.74MB/sได้ประมาณ19s ค่าETAเป็นการคาดการณ์และปรับตามความเร็ว ไม่ใช่รับประกันเวลาจบ MB/sรวมอ่าน/hash/crypto/เขียน ไม่ใช่networkthroughput งานlocalนี้ไม่ทดสอบความเร็วGoogleDrive screenshotและAXอาจจับคนละจังหวะเนื่องจากอ่านแต่ละcontrolแยกกัน จึงไม่ใช้AXหลายช่องเป็นtransactional snapshot Unit tests178ผ่านในreleaseก่อนหน้า รวมmath/ภาษา/zero/unknown/cap99/commitfailure

## RAM actual Desktop

เก็บทุกประมาณ250ms แยกเฉพาะPID2688 ไม่รวมแอปconfigจริงอีกprocess:

| งาน | sampledpeakWorkingSet MB | sampledpeakPrivate MB | Windowsprocesslifetimepeak MB |
|---|---:|---:|---:|
| สำรอง (45samplesตามstatusinterval) |260.14|220.84|260.16|
| กู้คืน (127samplesตั้งแต่ไฟล์ปลายทางแรกถึงปิดหน้าทดสอบ รวมช่วงพักท้ายงาน) |566.29|729.40|626.27|

MBฐาน10 ใช้โปรเซสเดียวสำรองก่อนกู้คืน การจัดสรรเดิม/GCและการทดสอบlocalที่เร็วมีผลต่อpeak ไม่ใช่หลักฐานว่าfeatureเพิ่มRAMเท่าใดเทียบรุ่นเก่า และไม่รับประกันRAMคงที่ทุกworkload

## คงการตั้งค่า

Configurationหลัก, SchedulerXMLและWindowsstartupRunkeyก่อน/หลังตรงกันทั้งหมด รอบถัดไป9พฤศจิกายน2026เวลา21:00 productionfactoryยังbackup16/restore16 Googlemutation16 ไม่เปลี่ยนsource/destination/retention/credentialsของผู้ใช้ ปิดหน้าต่างและTrayเฉพาะconfigจำลองหลังสำเร็จ เก็บfixtureและlogsในartifacts/progress-installer-verification/ (ignored) ไม่มีcredentials/filecontentsในรายงาน ไม่มีcommit/pushหรือเปลี่ยนReleaseจากงานทดสอบนี้
ตรวจSHA-256เสร็จ22:22:43: generation/source/restoredตรงกัน3/3ไฟล์ รวม2,147,516,416bytes ต้นฉบับไม่เปลี่ยน ไม่มีไฟล์ขาดหรือเกิน หลักฐานfixture/restored-sha-result.json ตรวจtimerโดยอนุมานจากfinaldisplayได้13.32454sสำหรับbackup ต่างจากstatus13.27428sประมาณ50ms สอดคล้องกับUIเริ่มจับก่อนstatus แต่ไม่ใช่การอ่านUItimerโดยตรง

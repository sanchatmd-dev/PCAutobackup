# คืน RAM หลังจบงานขณะเปิด UI — 8 พฤศจิกายน 2026

เปลี่ยนCoreให้ล้างการอ้างอิงbuffer queues/pending tasksและlocalchunkหลังdrain ทั้งสำเร็จ/ยกเลิก/ล้มเหลว Desktopทำmanagedmemory collectionและLOH compactionครั้งเดียวหลังtransferจบและDispatcher.ApplicationIdle/asyncstackคืนแล้ว โดยTask.Run ไม่ใช้EmptyWorkingSetหรือworking-set trim ไม่มีtimerประจำ งานใหม่invalidatecleanupที่รอหรือawaitcleanupที่กำลังทำแบบasync ไม่ให้GCซ้อนกับการเริ่มtransfer คงprogress100และผลสำเร็จ

เป็นการเรียกGCอย่างตั้งใจในตัวproductเพื่อคืนdeadmanagedsegmentsจริง อาจมีGCpauseสั้น ไม่ใช่การเปลี่ยนarchitectureเป็นWorkerทุกUIjob และไม่ได้รับประกันว่าค่าRAMกลับbaselineเป๊ะหรือเท่ากับTray ข้อมูลที่UI/.NETต้องใช้อยู่ยังคงอยู่

## วิธีเทียบ

BaselinefreezeinstalledReleaseprogress Core86288AA8F8083A19BBE96B2DBF29E55DB20B3E652516E7890CE3524DC746B5B2 ก่อนแก้ CandidateCore4FB3112BDC97C96757C98201601A6D32C110E46DB54CC323587A5CC47D0D78C2 โปรเซสWPFจริงแยกกัน แต่ละรอบconfig/repo/testkeyใหม่ ใช้sourcefixtureเดียวกัน Local1GiB+32KiB+empty3files และpairedGoogleDrive512MiB+32KiB+empty3files ใหม่แต่ละrepo ไม่แตะsourceหรือbackupจริงของผู้ใช้ RootกดactualUI ไม่ใช้externalGC/trimจากเครื่องมือวัด Candidateมีone-timeGCเป็นพฤติกรรมproductที่กำลังทดสอบ

เก็บRAMทุกประมาณ250ms ค่าMBฐาน10 เปิดUIตลอดช่วงพัก Localbackupanchorเป็นstatus.CompletedUtc; restoreanchorเป็นเวลาUIยืนยันสำเร็จครั้งแรก (อาจช้ากว่างานจริง) ใช้sampleแรก>=anchor+60sเพื่อเทียบ ไม่ใช่manualsnapshot80/87s

| ที่เวลา+60s | Privateเดิม MB | Privateแก้ MB | WSเดิม MB | WSแก้ MB |
|---|---:|---:|---:|---:|
| Local backup |93.68|90.05|105.01|126.51|
| Local restore |148.56|100.61|150.00|171.29|
| Google Drive backup |104.40|83.76|163.89|127.18|

Driveprivateลด19.77% WSลด22.40% ก่อนงานDriveprivate72.12/72.57MBและWS124.97/128.40MBตามลำดับ หลังงานcandidateWS127.18ใกล้ก่อนงาน แต่private83.76ยังสูงกว่าก่อนงานประมาณ11MB Localprivateลดเล็กน้อยหลังbackupและลด47.95MBหลังrestoreที่60s แต่WSสูงขึ้น21MB รุ่นเดิมGCเองภายหลัง: baselineprivateลดถึง106.03MBที่ประมาณ77sหลังrestore-observation ส่วนcandidate100.42MBที่87s จึงห้ามสรุปว่าลด32%ถาวรหรือRAMทุกตัวชี้วัดดีขึ้นเสมอ ไม่ใช้historicalDrive248MBจากงานก่อนแทนbaselineของคู่ทดสอบนี้

หนึ่งคู่ต่อworkload ลำดับbaselineก่อนcandidate cache/GC/OSpagingและเครือข่ายแปรปรวน ไม่มีheapdumpเพื่อพิสูจน์rootcauseทุกobject ผลนี้ไม่ใช่leakหรือTBguarantee ไม่มีเก็บGCpause stopwatchแยก การทดสอบเร็วไม่ใช้เป็นspeedbenchmark

## ความถูกต้องและการติดตั้ง

Localทั้งbaselineและcandidateสำรอง/กู้คืนสำเร็จ SHA-256 source-generation-restoredตรง3/3 รวม1,073,774,592bytes ไม่ขาด/เกิน PairDrivebackupทั้งคู่สำเร็จ3files536,903,680bytes จบ100%ETA0; ไม่restoreคู่Driveซ้ำในงานนี้ TestsRelease182ผ่าน buildไม่มีwarnings/errors รวมpolicybackgroundonce/stalegeneration/serializejobstart/failuregate และfailure/cancellationของCore

ติดตั้งcandidateในเครื่องแล้ว Desktop/Worker/Trayhashตรงpackage ConfigurationหลักและSchedulerXMLก่อน/หลังตรงกัน ยังbackup16/restore16 และรอบถัดไป9พฤศจิกายน21:00 Worker--helpเปิดได้ ไม่commit/pushหรือเปลี่ยนGitHubReleaseในงานนี้ หลักฐานดิบรวมcsv/phaseanchors/SHA/configtestที่ป้องกันkeyด้วยDPAPIอยู่artifacts/idle-memory-fix-verification/ (ignored)
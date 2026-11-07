# PC AutoBackup

แอป Windows สำหรับสำรองไฟล์แบบเข้ารหัสไปยัง **Google Drive**, **External HDD/SSD** หรือ **โฟลเดอร์ที่กำหนดเอง** พร้อมกู้คืนบนเครื่องใหม่ด้วยกุญแจกู้คืน หน้าต่างเป็น WPF native ภาษาไทย ไม่มี Electron หรือ browser runtime ค้างเบื้องหลัง

[หน้าแอป](https://sanchatmd-dev.github.io/PCAutobackup/) · [นโยบายความเป็นส่วนตัว](https://sanchatmd-dev.github.io/PCAutobackup/privacy.html) · [เงื่อนไขการใช้](https://sanchatmd-dev.github.io/PCAutobackup/terms.html)

## ความสามารถ

- สำรองผ่าน Windows Task Scheduler วันละ 1–24 รอบตามเวลาที่เลือก ค่าเริ่มต้นวันละครั้ง 21:00; ปิดหน้าต่างได้ worker จะเปิดเฉพาะเวลาทำงานแล้วออก ไม่ต้องมีโปรเซส resident ระหว่างรอบ
- Google Drive เป็นค่าเริ่มต้น ล็อกอินผ่านเบราว์เซอร์ของระบบด้วย OAuth PKCE และยืนยันอีเมลจริงจาก Google แอปไม่รับรหัสผ่าน Google
- อ่านไฟล์ทีละ chunk 4 MiB เข้ารหัส AES-256-GCM และใช้ keyed content IDs เพื่อไม่เก็บ chunk เดิมซ้ำใน repository เดียวกัน
- ค่าเริ่มต้นเก็บประวัติ **7 วัน**; เปลี่ยนจำนวนวันได้; `0` หมายถึงเก็บเฉพาะชุดล่าสุดที่สำเร็จ
- กำหนดเพดานพื้นที่ Backup เป็น GB หรือเว้นว่างเพื่อไม่กำหนดเพดาน ค่าที่นับคือข้อมูลสำรองของ repository นี้ ไม่ใช่พื้นที่ทั้งหมดของบัญชี Google
- เมื่อพื้นที่ไม่พอ ล้างประวัติเก่าสุดก่อน จึงอาจเก็บย้อนหลังสั้นกว่าจำนวนวันที่ตั้งไว้ หากชุดใหม่เพียงชุดเดียวเกินเพดาน จะไม่ยืนยันชุดใหม่และรักษาชุดล่าสุดที่สำเร็จไว้
- ไฟล์ที่ลบในต้นทางจะหายจากชุดล่าสุดเมื่อสำรองรอบใหม่สำเร็จ แต่ยังอยู่ในประวัติที่ไม่ถูกล้าง
- เลือกหลายโฟลเดอร์ แสดงความคืบหน้า ยกเลิกงาน และกู้คืนไปยังโฟลเดอร์ว่างโดยไม่เขียนทับไฟล์เดิม
- ดิสก์ภายนอกใช้ volume identity เพื่อไม่เผลอเขียนลงดิสก์อื่นเมื่ออักษรไดรฟ์เปลี่ยน
- แนบรายชื่อโปรแกรมที่ติดตั้งจาก Windows Registry เป็น JSON ได้ เพื่อช่วยติดตั้งใหม่ด้วยตัวเอง

## เริ่มใช้งาน

1. สร้างหรือดาวน์โหลดชุด Windows จาก workflow แล้วแตกไว้ในโฟลเดอร์ถาวร เช่น `%LOCALAPPDATA%\Programs\PCAutobackup` อย่าย้ายหลังเปิดตารางเวลาโดยไม่ตั้งเวลาใหม่
2. เปิด `PCAutobackup.exe` ชุดที่สร้างด้วย `scripts/build.ps1` รวม .NET runtime มาแล้ว ไม่ต้องติดตั้ง .NET เพิ่มบนเครื่องใช้งาน
3. ใน **โฟลเดอร์ต้นทาง** เพิ่มเฉพาะโฟลเดอร์ที่ต้องการ เช่น Documents, Desktop, Pictures และโฟลเดอร์งาน
4. ใน **พื้นที่จัดเก็บ** เลือก Google Drive และเชื่อมบัญชี หรือเลือกโฟลเดอร์เฉพาะบน External HDD/SSD/ดิสก์อื่น
5. ใน **การตั้งค่า** ส่งออกกุญแจกู้คืนไปยังที่ปลอดภัยนอกคอมเครื่องนี้ เช่น USB อีกตัว ห้ามเก็บสำเนาเพียงชุดเดียวไว้ใน PC ที่กำลังสำรอง
6. ตั้งจำนวนวัน/เพดานพื้นที่ และลอง **สำรองตอนนี้** หนึ่งครั้ง ตรวจรายการประวัติและทดลองกู้คืนไฟล์ก่อนพึ่งพางานอัตโนมัติ
7. เปิดตัวเลือกสำรองอัตโนมัติ กรอก **เวลาสำรองแต่ละวัน** เช่น `21:00` หรือ `09:00, 13:00, 21:00` แล้วบันทึก จากนั้นปิดหน้าต่างได้ ใส่เวลาแบบ 24 ชั่วโมง ไม่ซ้ำกัน สูงสุด 24 รอบต่อวัน

ไม่มีการเปิดงานสำรองจริง สร้าง scheduled task หรืออัปโหลดข้อมูลโดยอัตโนมัติแค่เปิดแอป

## ตั้งค่า Google Drive ครั้งแรก

แอปไม่มี OAuth client ของผู้พัฒนาฝังมาให้ ผู้ติดตั้งต้องใช้ Google Cloud project ของตนเอง:

1. เข้า [Google Cloud Console](https://console.cloud.google.com/) สร้างหรือเลือก project และเปิด **Google Drive API**
2. ตั้งค่า OAuth consent screen / Google Auth Platform ใส่ข้อมูลแอป หากใช้สถานะ Testing ให้เพิ่มบัญชีที่จะใช้ในรายชื่อ test users
3. สร้าง OAuth Client ID ชนิด **Desktop app** แล้วดาวน์โหลด client JSON
4. ที่หน้าพื้นที่จัดเก็บของ PC AutoBackup กดนำเข้า OAuth client JSON (หรือระบุ Client ID/Client secret ของ Desktop client)
5. กด **เชื่อมต่อ Google** เลือกบัญชี/เข้าสู่ระบบบนหน้า Google และอนุญาตสิทธิ์ Drive จากนั้นแอปจะแสดงอีเมลที่ Google ยืนยันจริง อีเมลที่พิมพ์ในแอปเป็นเพียงตัวช่วยเลือกบัญชี

ดูขั้นตอนหน้าจอปัจจุบัน การทดสอบไฟล์จำลอง และการแก้ปัญหาที่ [คู่มือ Google Desktop OAuth](docs/google-setup.md)

สิทธิ์ที่ใช้: `openid`, `email`, `https://www.googleapis.com/auth/drive.file` สำหรับไฟล์ที่แอปสร้าง/ได้รับอนุญาต โฟลเดอร์ที่แอปสร้างคือ `PC AutoBackup` และแยก repository ตามเครื่อง ไม่แก้ไฟล์ทั่วไปนอกคลังของแอป

**เมื่อต้องกู้คืนบนคอมใหม่ ใช้ Google account เดิมและ OAuth application/project เดิม** จึงจะค้นพบ repository เดิมภายใต้ `drive.file` ได้ รุ่นนี้ยังไม่มี Google Picker เพื่อรับสิทธิ์ข้าม OAuth application จึงควรเก็บ client JSON ไว้ด้วย (แยกจากกุญแจกู้คืนตามสมควร)

Google OAuth project ที่อยู่ในสถานะ External/Testing อาจมี refresh token อายุ 7 วันสำหรับสิทธิ์ Drive ทำให้ต้องเชื่อมใหม่ ควรตั้งสถานะและปฏิบัติตามข้อกำหนดของ Google ให้เหมาะกับงานสำรองอัตโนมัติระยะยาว

เอกสารทางการ: [Desktop OAuth](https://developers.google.com/identity/protocols/oauth2/native-app), [Drive scopes](https://developers.google.com/workspace/drive/api/guides/api-specific-auth), [Token expiration](https://developers.google.com/identity/protocols/oauth2), [Resumable uploads](https://developers.google.com/workspace/drive/api/guides/manage-uploads)

## กู้คืนเมื่อเปลี่ยนเครื่อง

1. ติดตั้ง/เปิดแอปบนเครื่องใหม่ แล้วนำเข้าไฟล์กุญแจ `.pcakey` ที่ส่งออกไว้
2. เปิด **ประวัติและกู้คืน** เลือกคลังสำรองจากโฟลเดอร์บนดิสก์ หรือเชื่อม Google ด้วย OAuth application เดิมแล้วเลือกเครื่องต้นทางจากรายการ
3. เลือกจุดกู้คืน และเลือก **โฟลเดอร์ว่าง** เป็นปลายทาง
4. แอปตรวจความถูกต้องและถอดรหัสไฟล์ แล้วสร้างโฟลเดอร์ต้นทางแยกตามชื่อที่อ่านออกและรหัสคงที่ ย้าย Documents/Desktop/โฟลเดอร์งานไปที่ที่เหมาะสมในเครื่องใหม่หลังตรวจสอบผล
5. หากจะเริ่มสำรองเครื่องใหม่ ให้เลือก repository ใหม่สำหรับเครื่องใหม่ คลังเดิมมีเจ้าของเครื่องกำกับไว้เพื่อป้องกันคอมใหม่ที่ว่างทำให้ประวัติเครื่องเดิมถูกล้าง

ไฟล์ `.pcakey` คือกุญแจ 256 บิต ผู้ที่มีทั้งกุญแจและคลังสามารถอ่านข้อมูลได้ หากกุญแจหายและไม่สามารถเปิดข้อมูล DPAPI ในบัญชี Windows เดิมได้ แอปไม่มีระบบรีเซ็ตเพื่อถอดรหัสข้อมูลเดิม

ถ้าเครื่องนี้มีกุญแจสำรองใช้งานอยู่แล้ว การนำเข้ากุญแจอีกคลังจะใช้สำหรับการกู้คืนใน session นี้เท่านั้น ไม่แทนที่กุญแจของงานอัตโนมัติเดิม เมื่อเปิดแอปใหม่ต้องนำเข้ากุญแจกู้คืนต่างคลังอีกครั้ง สำหรับการติดตั้งใหม่ที่ยังไม่มีกุญแจ แอปจะบันทึกกุญแจแรกด้วย DPAPI

## RAM และการทำงานเบื้องหลัง

การใช้ RAM ระหว่างรอบเป็น **0 สำหรับโปรเซสแอป** เมื่อปิด Desktop และ worker ทำงานเสร็จแล้ว (ไม่รวม Windows Task Scheduler ซึ่งเป็นบริการของระบบ) ไม่จำเป็นต้องซ่อนไว้ใน system tray

ระหว่างสำรองอ่านเนื้อหาเป็น chunk และอัปโหลดทีละส่วน แต่ข้อมูลรายการไฟล์และชุด chunk references ยังใช้ RAM ตามจำนวนไฟล์/ประวัติ จึงไม่รับประกันเพดาน 50 MB ระหว่างทำงาน ไม่โหลดเนื้อหาไฟล์ขนาดใหญ่ทั้งไฟล์เข้าหน่วยความจำ ประวัติแต่ละ manifest ถูกอ่านทีละชุด ขนาด encrypted manifest จำกัด 256 MiB

การทดสอบ Windows 11 x64 ด้วยไฟล์สุ่ม 128 MiB + ไฟล์ภาษาไทยหนึ่งไฟล์ วัด peak working set ของ worker ได้ประมาณ **75.5 MiB รอบแรก** และ **44.6 MiB รอบไม่เปลี่ยนข้อมูล** ตัวเลขนี้เป็นผลจากชุดทดสอบเล็ก ไม่ใช่เพดานสำหรับทุก workload รายละเอียดอยู่ใน [ผลตรวจสอบ](docs/validation.md)

งานประจำวันสแกนและ hash ไฟล์ที่เลือกทั้งหมด เพื่อจับการเปลี่ยนแปลง แต่ไม่ดาวน์โหลด chunk ที่สำรองไว้แล้วทุกวัน ข้อมูลเดิมจะถูกตรวจสอบการถอดรหัสและความถูกต้องเมื่อกู้คืน การตรวจ corruption ทุกวันแบบ full scrub ยังไม่รองรับ

ตารางเวลาใช้บัญชี Windows ปัจจุบันแบบ **ผู้ใช้ต้องล็อกอินอยู่** ไม่เก็บรหัสผ่าน Windows หากปิดเครื่องหรือ Sleep ตอนถึงรอบ เมื่อเข้าสู่ Windows ปลดล็อก หรือ Windows แจ้งกลับจาก Sleep จะเรียกตรวจงานตกค้างหลังประมาณ 1 นาที พร้อมเปิด StartWhenAvailable ของ Task Scheduler ไว้ด้วย ไม่ปลุกเครื่องเองและไม่ทำงานก่อนเข้าสู่บัญชี Windows เดิม หากพลาดหลายรอบจะสำรองข้อมูลปัจจุบันชดเชยครั้งเดียว ไม่สร้างภาพย้อนหลังที่ไม่มีจริง และไม่สำรองซ้ำหากมีงานสำเร็จที่เริ่มตั้งแต่รอบล่าสุดแล้ว

งานล้มเหลวให้ Task Scheduler ลองใหม่ทุก 5 นาที สูงสุด 3 ครั้งต่อ trigger เช่น รอเครือข่ายหลังกลับจาก Sleep งานไม่รันซ้อนกัน หากรอบใหม่มาถึงขณะงานเดิมยังทำอยู่จะไม่เริ่มอีก process ให้ตรวจผลในแอป งานที่ค้าง/ชิ้นส่วนที่อัปโหลดสำเร็จถูกใช้ซ้ำเมื่อรอบใหม่เริ่มได้ ไม่รับประกัน resume ตรง byte ของไฟล์ที่อ่านค้างเมื่อปิดเครื่อง การเริ่มจริงขึ้นกับ Windows, สถานะล็อกอิน และความพร้อมของต้นทาง/ปลายทาง

การตั้งค่าเก่าที่มีเวลาเดียวจะรักษาเวลานั้นไว้ เมื่อแก้เวลาผ่านหน้าตั้งค่า ระบบจะบันทึกรายการหลายเวลาให้ใหม่ ตัวเลือกหลายโฟลเดอร์มีอยู่ที่ **โฟลเดอร์ต้นทาง → เพิ่มโฟลเดอร์** เลือกหลายโฟลเดอร์ใน dialog หรือเพิ่มทีละโฟลเดอร์ได้

อ้างอิง Windows: [StartWhenAvailable](https://learn.microsoft.com/en-us/windows/win32/taskschd/tasksettings-startwhenavailable), [session-state triggers](https://learn.microsoft.com/en-us/windows/win32/taskschd/taskschedulerschema-sessionstatechangetrigger-triggergroup-element), [event triggers](https://learn.microsoft.com/en-us/windows/win32/taskschd/taskschedulerschema-eventtrigger-triggergroup-element)

## ข้อจำกัดที่ควรเข้าใจก่อนใช้

- เป็น **file backup** ไม่ใช่ disk image หรือ bare-metal restore ไม่คัดลอก Windows/ไดรเวอร์ ไม่รับประกันย้ายโปรแกรมและการตั้งค่าข้าม OS ทุกเวอร์ชัน รายชื่อโปรแกรมเป็นข้อมูลช่วยติดตั้งเอง ไม่ได้มีตัวติดตั้งหรือ license มาด้วย
- ไม่ย้าย cookie/session/password ของ browser หรือไฟล์ credential ที่ผูกกับเครื่องให้ใช้งานข้ามเครื่องอัตโนมัติ ใช้ระบบ sync/export ของโปรแกรมนั้นเมื่อจำเป็น
- ไม่มี VSS หรือ application-aware database snapshot: ปิดโปรแกรมที่เขียนไฟล์ฐานข้อมูล/VM/อีเมลก่อนสำรอง ถ้าเปิดอ่านไม่ได้หรือไฟล์เปลี่ยนระหว่างอ่าน งานจะล้มเหลวและไม่ยืนยัน snapshot ใหม่
- ไม่เดินตาม junction/symlink/reparse point รวมถึงบาง cloud placeholder เลือกไฟล์ local ที่ดาวน์โหลดครบแล้ว หากพบ reparse point จะหยุดโดยไม่ตีความว่าเป็นการลบ
- ไม่รักษา ACL, alternate data streams, EFS certificates หรือ Windows account identity; กู้เนื้อหาไฟล์ โครงสร้างโฟลเดอร์ และเวลาแก้ไขไฟล์ที่รองรับ
- เมื่อถึง retention/quota แอปจะลบเฉพาะ manifest/chunk ของคลังที่ไม่มี snapshot ที่เหลืออ้างถึง และคงชุดล่าสุดไว้ การลดวัน/โควตาอาจทำให้กู้เวอร์ชันเก่าไม่ได้
- เพดานพื้นที่เป็นนโยบายข้อมูลที่ commit แล้ว ระหว่าง staging snapshot ใหม่ต้องมีพื้นที่เผื่อสำหรับชุดเก่าและส่วนที่เปลี่ยน และอาจมี orphan ชั่วคราวหากงานหรือ cleanup ถูกขัดจังหวะ แอปจะเก็บกวาดในรอบที่สำเร็จภายหลัง ไม่ใช่ hard filesystem quota
- ใช้หนึ่งเครื่อง/ผู้เขียนต่อ repository ไม่คัดลอก config พร้อม DeviceId ไปใช้เขียนคลังเดียวกันจากหลายเครื่อง Google Drive ไม่มี atomic unique-name/distributed lock; แอปตรวจชื่อซ้ำและหยุดแทนการเลือกไฟล์สุ่ม
- ชุดโปรแกรมนี้ยังไม่ได้เซ็น code-signing certificate และเป็นรุ่นแรก ควรทดลองกู้คืนกับข้อมูลสำคัญที่มีสำเนาอื่นอยู่ก่อน

## Build และทดสอบสำหรับนักพัฒนา

ต้องใช้ Windows และ [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)

```powershell
dotnet build PCAutobackup.sln -c Release
dotnet test tests/PCAutobackup.Tests/PCAutobackup.Tests.csproj -c Release
.\scripts\build.ps1
```

ผลลัพธ์อยู่ใน `artifacts/PCAutobackup-win-x64/` พร้อม `Worker/` และ runtime ไม่ต้องเปิด terminal เพื่อใช้งานปกติ รองรับ build ARM64 ด้วย `-Runtime win-arm64` แต่ต้องทดสอบบนเครื่อง ARM64 ก่อนใช้งานจริง

รันจาก source:

```powershell
dotnet run --project src/PCAutobackup.Desktop
# Worker ต้อง build/publish ไว้ก่อนจึงเปิดตารางเวลาได้
dotnet build src/PCAutobackup.Worker
# isolated configuration สำหรับ QA ไม่แตะ config ของผู้ใช้จริง
dotnet run --project src/PCAutobackup.Desktop -- --config C:\Test\configuration.json
dotnet run --project src/PCAutobackup.Worker -- --config C:\Test\configuration.json
```

GitHub Actions รัน tests บน Windows และแนบชุด portable app เป็น workflow artifact ซอร์สไม่รวม credentials, SDK, binary หรือ backup ของผู้ใช้

## ที่เก็บการตั้งค่าและสถานะ

- `%LOCALAPPDATA%\PCAutobackup\configuration.json` — การตั้งค่า, recovery key และ OAuth token ที่หุ้มด้วย Windows DPAPI สำหรับผู้ใช้ปัจจุบัน
- `configuration.json.status.json` — ผลงานครั้งล่าสุด และ `configuration.json.success.json` — ครั้งล่าสุดที่สำรองสำเร็จ
- task ชื่อ `PCAutobackup-<DeviceId>` — ปิดได้จากการตั้งค่าแอปก่อนลบโฟลเดอร์โปรแกรม
- ไม่เก็บรหัสผ่าน Google; client secret ของ Desktop OAuth ไม่ใช่รหัสผ่านบัญชี Google และไม่ควร commit ลง repo

## โครงสร้าง

```text
src/PCAutobackup.Core       Backup engine, stores, OAuth, config, scheduler
src/PCAutobackup.Desktop    Native WPF UI
src/PCAutobackup.Worker     Run once, record result, exit
tests/PCAutobackup.Tests    Synthetic integration + mocked Google tests
scripts/build.ps1          Self-contained portable Windows build
AGENTS.md                  บทบาท/รุ่นโมเดลและกฎการทำงานทีม
Contex.md                  บริบทสถาปัตยกรรมและสถานะโครงการ
```

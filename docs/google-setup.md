# ตั้งค่า Google Desktop OAuth และทดสอบกู้คืน

คู่มือนี้ใช้กับ PC AutoBackup บน Windows และ Google Auth Platform ที่ตรวจหน้าจอเมื่อ 7 พฤศจิกายน 2026 ชื่อเมนูอาจเปลี่ยนได้ ไม่ต้องกรอกรหัสผ่าน Google ในแอป PC AutoBackup

## 1. สร้างโปรเจกต์และเปิด API

1. เปิด [Google Cloud Console](https://console.cloud.google.com/) ด้วยบัญชีที่จะเก็บข้อมูลสำรอง
2. เปิดตัวเลือกโปรเจกต์ กด **New project** ตั้งชื่อ เช่น `PC AutoBackup` แล้วกด **Create**
3. เมื่อสร้างเสร็จ เลือกโปรเจกต์ใหม่นี้ ตรวจชื่อบนแถบด้านบนก่อนแก้การตั้งค่า
4. เปิด **APIs & Services**, **Library**, **Google Drive API** แล้วกด **Enable**

## 2. ตั้งชื่อแอปและผู้ทดสอบ

1. เปิด **OAuth consent screen** หรือ **Google Auth Platform** แล้วกด **Get started**
2. ใส่ App name เช่น `PC AutoBackup` และเลือก User support email ของคุณ
3. บัญชี Gmail ส่วนบุคคลเลือก Audience แบบ **External**
4. ใส่ Contact email อ่าน Google API Services: User Data Policy และยืนยันด้วยตนเองเมื่อยอมรับ จากนั้นกด **Continue** และ **Create**
5. เปิด **Audience**, **Add users** ใส่บัญชี Google ที่จะใช้ทดสอบ กด Enter ให้เป็นรายการอีเมลก่อนกด **Save** ตรวจว่าอีเมลปรากฏในตาราง Test users หลังแผงปิด

สถานะ **Testing** จำกัดผู้ใช้ตามรายการนี้ และ refresh token ของแอป External/Testing ที่ขอสิทธิ์ Drive มีอายุ 7 วันตามเงื่อนไขของ Google จึงไม่เหมาะกับงานอัตโนมัติระยะยาวโดยไม่กลับมาเชื่อมต่อใหม่ การใช้จริงควรตรวจข้อกำหนดและตั้งสถานะ Production ให้เรียบร้อยก่อนพึ่งพาตารางสำรอง ดู [Token expiration](https://developers.google.com/identity/protocols/oauth2#expiration)

### ใช้งานระยะยาว: เปลี่ยนเป็น Production

1. หาก **Audience → Publish app** ถูกปิดและแจ้งให้ตั้งค่า Branding ให้เปิด **Branding** และใส่หน้าแอป นโยบายความเป็นส่วนตัว เงื่อนไขการใช้ และ Authorized domain ที่สอดคล้องกับเว็บไซต์ของคุณ ห้ามใส่ลิงก์สมมติหรือโดเมนที่คุณควบคุมไม่ได้
2. โครงการนี้เผยแพร่หน้าแอปที่ [sanchatmd-dev.github.io/PCAutobackup](https://sanchatmd-dev.github.io/PCAutobackup/) พร้อม `privacy.html` และ `terms.html` ผ่าน GitHub Pages โดยใช้ Authorized domain `sanchatmd-dev.github.io` ผู้ที่ fork ต้องเปลี่ยนเว็บไซต์และนโยบายให้ตรงกับผู้ดูแลและพฤติกรรมของตนเอง
3. กด **Save** ใน Branding แล้วกลับไป **Audience → Publish app** อ่านหน้าต่าง **Push to production?** และกด **Confirm** เมื่อต้องการเปลี่ยนสถานะ ตรวจว่าแสดง **In production** จริง
4. กลับมาแอป กด **เชื่อมต่อ Google** อีกครั้ง เลือกบัญชีเดิมและอนุญาตสิทธิ์เดิม เพื่อรับการอนุญาตหลังเปลี่ยนสถานะ ไม่ต้องเปลี่ยน Desktop client หรือกุญแจกู้คืน
5. Production ไม่เท่ากับ Google ตรวจรับรองแบรนด์แล้ว และไม่ได้รับประกันว่า token จะไม่หมดอายุหรือถูกเพิกถอนด้วยเหตุอื่น การใช้งานส่วนตัวอาจเข้าข้อยกเว้นการตรวจรับรอง แต่การแจกจ่ายสาธารณะต้องตรวจข้อกำหนดเพิ่มเติม ดู [Manage App Audience](https://support.google.com/cloud/answer/15549945) และ [Brand verification](https://developers.google.com/identity/protocols/oauth2/production-readiness/brand-verification)

ตรวจจริงเมื่อ 7 พฤศจิกายน 2026: หลังเพิ่มเว็บไซต์และลิงก์นโยบาย ปุ่ม Publish app ใช้งานได้ และโปรเจกต์ของผู้ใช้แสดง In production แล้ว ไม่ได้เพิ่ม Drive scope ให้กว้างกว่า `drive.file`

## 3. สร้าง Desktop client

1. เปิด **Clients**, **Create client**
2. เลือก Application type เป็น **Desktop app** ตั้งชื่อ เช่น `PC AutoBackup Windows` แอปนี้ไม่ใช่ AI agent
3. กด **Create** แล้วกด **Download JSON** ก่อนปิดหน้าต่าง Google อาจไม่ให้ดาวน์โหลด client secret อีกหลังปิดหน้าต่างนี้
4. เก็บ JSON ไว้ในที่ปลอดภัยนอก repository ไม่ส่งรหัสผ่าน, token, recovery key หรือ JSON นี้ลง GitHub/แชต

Desktop client ใช้ system browser และ loopback redirect พร้อม PKCE ไม่ต้องสร้าง Web application หรือกรอก redirect URL ของเว็บไซต์ ดู [เอกสาร Desktop OAuth ของ Google](https://developers.google.com/identity/protocols/oauth2/native-app)

## 4. เชื่อมกับ PC AutoBackup

1. เปิด **พื้นที่จัดเก็บ**, เลือก **Google Drive**
2. ขยาย **การตั้งค่า Google OAuth สำหรับแอป** กด **นำเข้าไฟล์ client JSON…** แล้วเลือกไฟล์ที่ดาวน์โหลด
3. กรอกอีเมลแนะนำบัญชีได้ แต่ช่องนี้ไม่ได้ยืนยันตัวตน
4. กด **เชื่อมต่อ Google** เลือกบัญชีในเบราว์เซอร์ ตรวจชื่อแอปและอนุญาต `openid`, `email`, `drive.file` สิทธิ์ Drive นี้ใช้กับไฟล์ที่แอปสร้างหรือได้รับสิทธิ์
5. กลับมาแอป ตรวจว่าแสดง **เชื่อมต่อแล้ว** พร้อมอีเมลที่ถูกต้อง ข้อความสำเร็จบนหน้า loopback เพียงอย่างเดียวยังไม่ยืนยันว่าแอปแลก token และตรวจ Drive สำเร็จ

## 5. ทดลองก่อนเลือกข้อมูลจริง

1. สร้างโฟลเดอร์ Source สำหรับไฟล์จำลองเท่านั้น เช่นไฟล์ข้อความไทย ไฟล์ว่าง และไฟล์ขนาดมากกว่า 4 MiB
2. เพิ่มโฟลเดอร์นี้ที่ **โฟลเดอร์ต้นทาง**
3. ใน **ประวัติและกู้คืน** ปิด **แนบรายชื่อแอปที่ติดตั้ง** หากต้องการทดสอบเฉพาะไฟล์จำลอง
4. ใน **การตั้งค่า** ส่งออก `.pcakey` ไปยังที่เก็บซึ่งไม่อยู่ภายใน Source เก็บสำเนาอีกชุดนอกเครื่องก่อนใช้กับข้อมูลจริง
5. คง retention 7 วัน และยังไม่เปิดตารางอัตโนมัติสำหรับการทดลอง กด **สำรองตอนนี้** แล้วรอผลสำเร็จ
6. เปิด **ประวัติและกู้คืน**, กด **รีเฟรช**, เลือก snapshot แล้วกู้คืนลงโฟลเดอร์ว่างคนละแห่งกับ Source
7. เทียบจำนวนไฟล์ ขนาด และ SHA-256 ของต้นฉบับกับไฟล์ที่กู้คืน ใช้ PowerShell `Get-FileHash -LiteralPath '<path>' -Algorithm SHA256` ได้

แอปจะสร้างโฟลเดอร์ต้นทางที่มีชื่อพร้อมรหัสคงที่ภายในปลายทางกู้คืน ห้ามตีความชื่อโฟลเดอร์ที่ต่างจาก Source ว่าไฟล์ผิด และอย่าใช้แค่ชื่อ snapshot เป็นหลักฐานว่ากู้คืนครบ

เมื่อย้ายคอม ต้องมี recovery key, บัญชี Google เดิม และ OAuth application/project เดิม การคัดลอกเฉพาะ configuration ที่มี DPAPI จาก Windows เครื่องเดิมไม่เพียงพอ

## 6. เปิดสำรองรายวัน

เลือกโฟลเดอร์ที่ต้องการใน **โฟลเดอร์ต้นทาง** จากนั้นเปิด **การตั้งค่า → เปิดสำรองอัตโนมัติทุกวัน** ตั้งเวลา เช่น `21:00` และกด **บันทึกการตั้งค่า** ตรวจด้วยว่า Windows Task Scheduler มีงาน `PCAutobackup-<DeviceId>` สถานะ Ready และ Next Run Time ตรงกับเวลาที่ต้องการ แอปใช้เขตเวลาของ Windows

เครื่องต้องเปิดอยู่ มีผู้ใช้ Windows บัญชีเดิมลงชื่อเข้าใช้ และเข้าถึงต้นทาง/อินเทอร์เน็ตได้ ปิดหน้าต่างแอปได้ Worker จะทำงานแล้วออก งานที่พลาดอาจทำภายหลังเมื่อเครื่องพร้อมหรือหลังลงชื่อเข้าใช้ตามเงื่อนไขของ scheduler และตัว worker อย่าย้ายโฟลเดอร์โปรแกรมหลังตั้งเวลาโดยไม่บันทึกตารางใหม่ และเก็บ recovery key นอกเครื่องก่อนพึ่งพาการสำรองระยะยาว

## ข้อผิดพลาดที่พบบ่อย

- `access_denied` ในสถานะ Testing: ตรวจบัญชีใน Test users แล้วเริ่มเชื่อมต่อใหม่
- Drive API ไม่เปิดหรือสิทธิ์ไม่ครบ: เปิด API ในโปรเจกต์ของ client นั้นและเชื่อมต่อใหม่พร้อมอนุญาตสิทธิ์ที่แอปขอ
- หน้า loopback เข้าไม่ได้หลังรอนาน: แอปรอ OAuth สูงสุด 5 นาที กลับมาแอปแล้วเริ่มเชื่อมต่อใหม่ ห้ามคัดลอก authorization code มาใส่ในแชต
- เชื่อมต่อใหม่ทุก 7 วัน: ตรวจ Publishing status และเงื่อนไข token ของ Google; จำนวนวันเก็บประวัติ Backup เป็นคนละการตั้งค่า
- ไม่พบคลังบนคอมใหม่: ตรวจทั้งบัญชีและ OAuth application เดิม แล้วกดค้นหาคลังใน Google Drive
- กู้คืนไม่ได้: ตรวจ recovery key และเลือกโฟลเดอร์ว่าง ห้ามสร้างกุญแจใหม่เพื่อแทนกุญแจของข้อมูลเก่า

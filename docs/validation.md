# Validation — 2026-11-07

Environment: Windows 11 Home Single Language x64 (10.0.26200), .NET SDK 10.0.403. Release configuration, self-contained win-x64 package.

## Automated checks

- Solution build: 0 warnings, 0 errors.
- xUnit suite: **75 passed, 0 failed, 0 skipped**.
- Covers authenticated encrypted round-trip, multi-chunk files, UTF-8 names, empty directories, source reorder, changed/deleted files, positive/default retention and latest-only retention, quota refusal, missing source, cancellation, wrong key/device, corrupt chunk, path traversal, destination conflict, unknown objects and reuse without downloading committed chunks.
- Service tests cover new-PC restore with exported key, DPAPI persistence, invalid key import, stale configuration revision, foreign recovery key isolation, missing scheduled configuration, preflight status, postcommit status failures and local lease contention.
- Mocked Google transport tests cover OAuth state/scope validation, token refresh, paging, duplicate detection, object naming/scope, cancellation, resumable uploads, ambiguous response probes and upload-session URI validation. These do not replace live Google testing.

## Packaged worker smoke test

Inputs were generated synthetic data under an ignored QA directory: one 134,217,728-byte cryptographically random file, a 50-byte UTF-8 text file, and an empty directory. No personal files were selected or uploaded. Local destination only.

| Run | Peak working set | Elapsed time | Result |
|---|---:|---:|---|
| First backup | 79,187,968 bytes (75.5 MiB) | 2.53 s | Succeeded, 2 files |
| Unchanged backup | 46,813,184 bytes (44.6 MiB) | 0.96 s | Succeeded, existing chunks reused |

A different configuration/device ID imported the recovery key, selected the repository and restored the snapshot. The large file's SHA-256 matched the original. Worker processes exited after completion. These timings/memory observations are not guarantees and are not representative of millions of files, many retained versions or remote network performance.

## Windows integration

- Self-contained `PCAutobackup.exe` opened on Windows without requiring the machine-wide .NET 10 runtime.
- Native UI reviewed through Windows accessibility and screenshots; sidebar/primary button contrast corrected after inspection.
- Fresh defaults confirmed: Google Drive, no connected account, no selected sources, 7-day retention, schedule disabled.
- Native settings edited to 14 days / 50 GB and saved successfully to an isolated QA configuration; actual JSON matched and schedule remained disabled. Production defaults were not modified. Long help text wrapping was corrected after screenshot review.
- A uniquely named temporary scheduled task was registered, manually triggered, and completed a synthetic backup through the packaged worker. The test task was removed afterward. No production schedule was enabled.

## Live Google Drive trial — 2026-11-07

- Created a dedicated Google Cloud project, enabled Drive API, configured an External/Testing consent screen and Desktop OAuth client. The owner completed browser account selection and consent. The app verified the returned identity and a real Drive request, and persisted tokens using DPAPI.
- Selected only a generated test directory; application inventory was disabled and no personal files were uploaded. Inputs: a 5,242,880-byte random binary, 44-byte text, 90-byte UTF-8 Thai text in a Thai-named subdirectory, and a zero-byte file. Total: **4 files / 5,243,014 bytes**.
- Native UI backup succeeded in **23.65 s**. Listing snapshots and restoring from Google Drive to a separate empty directory succeeded. All four restored file lengths and SHA-256 hashes matched the baseline; the restored file count matched exactly.
- A separate packaged Worker run with unchanged inputs succeeded in **9.23 s**, peak working set **70,959,104 bytes (67.7 MiB)**, and exited. The earlier UI backup's process peak was 211,746,816 bytes (201.9 MiB), including the already-open WPF UI and OAuth setup; this is not a Worker measurement.
- The live restore exposed a display race: a queued progress callback could replace the completion message while run-history refresh awaited I/O. The desktop now stops accepting progress before terminal messages and ignores callbacks from previous jobs.
- Rebuilt the package, imported the exported recovery-key file, and repeated the real Drive restore into another empty directory. All four lengths and SHA-256 hashes matched again, with exactly four files. The UI retained its final “กู้คืนสำเร็จ · 4 ไฟล์ · 5.24 MB” message after the job finished.
- At the end of this initial trial, the configuration selected the synthetic source only, kept 7 days, and had scheduling disabled; OAuth was in Testing. Later production onboarding is recorded below. The exported recovery key is outside the selected source but still on this PC; an off-machine copy is required before relying on it for disaster recovery.
- Local private evidence (not committed): `%USERPROFILE%\Documents\PCAutobackup-Google-Test\expected-hashes.json`, `restore-verification.json`, `restore-after-fix-verification.json`, and `worker-run.json`. Credentials and recovery-key contents are excluded from evidence reports.

## Production onboarding — 2026-11-07

- The user approved publication of the app information, privacy and terms pages. GitHub Pages deployment succeeded; the public page and its policy links were inspected. Adding their URLs and the corresponding Authorized domain to Google Branding enabled Publish app.
- After the user's action-time confirmation, Google Audience displayed **In production**. The user then completed a new consent flow for the same account and Desktop client, retaining `openid`, `email`, and `drive.file`. The app verified the returned identity and a real Drive request before persisting the new authorization. No claim is made that Google brand verification was completed or that refresh tokens cannot expire for other reasons.
- The user selected one actual source directory and explicitly authorized daily backup at 21:00. Configuration and the registered Windows task independently confirmed scheduling enabled, Ready state, a one-day calendar interval and next run at **2026-11-07 21:00 +07:00**. Windows timezone was SE Asia Standard Time; the task uses the signed-in Windows user. Retention remained 7 days, quota unset, and application inventory disabled.
- With the desktop closed, the packaged Worker completed an initial backup of the authorized directory: **24 files / 31,110,907 bytes**, **106.95 s**, peak working set **88,137,728 bytes (84.1 MiB)**, exit code **0**. The desktop reopened and displayed the successful snapshot and daily time; it was then closed again to avoid resident UI memory. This onboarding backup is distinct from the earlier synthetic recovery tests; these 24 files were not restored in this step.
- Private source paths, file names, credentials, tokens and recovery-key contents were not added to this repository. Local evidence is in ignored `artifacts/setup/production-first-backup.json` and setup screenshots. The first future 21:00 timed trigger has not yet been observed.

## Retention, quota and scheduling update — 2026-11-07

- Added a controlled TimeProvider to the service/engine. Synthetic tests use isolated local repositories and do not alter Windows time, the user's cloud retention, quota or source files.
- Real encrypted snapshots at ages 8 days, exactly 7 days, 3 days and current: only the expired snapshot and its exclusive chunk were removed. Shared chunks, the exact-boundary snapshot and unrelated files survived. All three retained snapshots restored with matching SHA-256.
- A 20,000-byte quota removed older versions until the latest fit, while retaining shared data. Reducing the quota to 1 byte rejected the next snapshot, removed its staged chunk, preserved the previous successful snapshot and restored it correctly. Unrelated repository files remained byte-identical.
- Multiple source folders already existed. Tests now additionally cover identical folder/file names, reordered sources, and deleting from only one source without affecting the other.
- Added 1–24 daily times with validation, preserved legacy single-time configuration, and tested midnight, multiple missed days, repeated catch-up invocations, disabled schedules, failed retries, and DST boundaries. A successful job started before a scheduled round no longer hides that round merely because it completed later.
- Packaged native UI saved three custom times to an isolated local configuration. Windows accepted three calendar triggers plus logon, unlock and resume triggers. With the desktop closed, automatic runs at **19:13** and **19:14 +07:00** both backed up the two synthetic sources and exited with task result 0.
- A third isolated run invoked through Task Scheduler caught up an intentionally overdue round; a repeated invocation skipped it without creating another snapshot. All three synthetic snapshots restored both folders with matching SHA-256. The temporary task was removed. Task XML uses invariant Gregorian dates even under Thai Windows culture.
- The user's task was re-registered from the new scheduler XML, preserving **21:00 once daily**, current account, sources, retention and quota. It now checks overdue rounds one minute after logon, unlock or the Windows resume event, with StartWhenAvailable enabled and WakeToRun disabled. The production window's accessibility was unavailable, so registration was completed through the scheduler XML/API and independently inspected in Windows.
- Local evidence is under ignored `artifacts/validation/`: TRX results, native UI screenshot, task XML, automatic worker statuses and synthetic repository. No credentials, private source names or recovery keys are committed.

## Cloud icon, installation, tray and forced termination — 2026-11-07

- Generated a teal cloud/upload icon and packaged a multi-resolution ICO (16 through 256 pixels) for the executable, window, tray and shortcuts. The WPF sidebar uses the original PNG to avoid upscaling the smallest ICO frame.
- Added a separate Win32 notification-area process with no WPF/WinForms/Chromium, no polling loop and no watchdog. Measured synthetic idle working set **39,632,896 bytes (37.8 MiB)** and private memory **9,768,960 bytes (9.3 MiB)** after recovering a job. A later idle observation after closing settings measured **34.8 MiB / 8.2 MiB**. These are observations, not caps; active backup/settings use additional memory.
- Killed the packaged Worker after it started writing a new chunk from a 256 MiB synthetic random file. The previous successful status remained byte-identical and its snapshot restored before retry. Starting Tray recovered the interrupted job before the next scheduled time; the restored two-file snapshot contained **268,435,507 bytes**, and the binary SHA-256 matched exactly. No real user files/cloud data were modified in this test.
- Launched duplicate Tray and verified it exited while the original stayed active. Killed Tray and relaunched successfully. Unit tests also exercise abandoned mutex ownership, activation signals and detection of Running status only after a live job releases its lock.
- Native Windows UI: minimize exited idle WPF while Tray remained. With a synthetic 4 GiB zero file being backed up, X hid the window while the process and Running status remained active. Relaunch opened the same window; backup completed with three files / **4,563,402,803 bytes**, uploading only about 4.27 MB because zero chunks deduplicated. Forced termination of Desktop and Tray together was followed by successful reopening, normal history display and normal close.
- Per-user installation creates Desktop/Start Menu cloud shortcuts and an HKCU Run entry, then refreshes the existing task's Worker path. Installation checks active UI/Worker processes before replacing binaries and does not terminate an active backup.
- Installed and updated through Windows PowerShell 5.1, the same host used by Install.cmd. The real configuration hash and unrelated HKCU Run values stayed unchanged. Desktop shortcut opened the installed app, which displayed 21:00, 7 days and the existing Drive account. The registered task remained Ready with its next run at 21:00 and now points to the stable installed Worker path.
- After stopping Tray normally, launched the exact configured startup executable/arguments. One installed Tray stayed running, with **38.01 MiB working set / 9.96 MiB private memory** and **0.0 CPU seconds added over a two-second idle sample**. This tests the startup command, not a physical reboot or sign-out/sign-in cycle.
- Local private evidence: `Documents/PCAutobackup-Verification/tray-lifecycle-20261107/` (process-test.json, close-during-backup-result.json, install-verification.json and screenshot). No keys/configuration contents are committed.

## Not verified / current limits

- Long-duration Google token refresh, provider interruptions, and restore on a physically different Windows computer remain **not tested**. The live same-PC cloud round-trip above does not establish these outcomes.
- No physical external-disk disconnect/reconnect or changed-drive-letter hardware test, no ARM64 runtime test, no Windows-version matrix.
- Scheduler registration and two actual clock-triggered runs were verified; catch-up decisions were tested with a controlled clock. Physical sleep/reboot, Modern Standby variations and an overnight power-off/resume scenario have not been performed. Interactive user logon is required; Windows event delivery and network availability still matter.
- Active manifest and chunk reference metadata still scale with dataset/history size. No full scrub/VSS/application-aware snapshots, app reinstallation, credential migration, or full OS restore.
- Google Drive lacks cross-machine write coordination. One writer per repository only.

# Validation — 2026-11-07

Environment: Windows 11 Home Single Language x64 (10.0.26200), .NET SDK 10.0.403. Release configuration, self-contained win-x64 package.

## Automated checks

- Solution build: 0 warnings, 0 errors.
- xUnit suite: **46 passed, 0 failed, 0 skipped**.
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
- The production configuration currently selects the synthetic source only, keeps 7 days, and has scheduling disabled. OAuth remains in Testing; long-running authorization beyond its token lifetime is not proven by this trial. The exported recovery key is outside the selected source but still on this PC; an off-machine copy is required before relying on it for disaster recovery.
- Local private evidence (not committed): `%USERPROFILE%\Documents\PCAutobackup-Google-Test\expected-hashes.json`, `restore-verification.json`, `restore-after-fix-verification.json`, and `worker-run.json`. Credentials and recovery-key contents are excluded from evidence reports.

## Not verified / current limits

- Long-duration Google token refresh, provider interruptions, and restore on a physically different Windows computer remain **not tested**. The live same-PC cloud round-trip above does not establish these outcomes.
- No physical external-disk disconnect/reconnect or changed-drive-letter hardware test, no ARM64 runtime test, no Windows-version matrix.
- Scheduler tested registration/manual trigger, not an overnight power-off/resume scenario. Interactive user logon is required.
- Active manifest and chunk reference metadata still scale with dataset/history size. No full scrub/VSS/application-aware snapshots, app reinstallation, credential migration, or full OS restore.
- Google Drive lacks cross-machine write coordination. One writer per repository only.

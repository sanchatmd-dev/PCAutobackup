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

## Not verified / current limits

- Real Google sign-in, real-account upload/download, long-duration token refresh and provider interruptions were **not tested**; no user OAuth client/account was supplied. Configure the Desktop OAuth client and perform a small backup-and-restore trial before relying on Google storage.
- No physical external-disk disconnect/reconnect or changed-drive-letter hardware test, no ARM64 runtime test, no Windows-version matrix.
- Scheduler tested registration/manual trigger, not an overnight power-off/resume scenario. Interactive user logon is required.
- Active manifest and chunk reference metadata still scale with dataset/history size. No full scrub/VSS/application-aware snapshots, app reinstallation, credential migration, or full OS restore.
- Google Drive lacks cross-machine write coordination. One writer per repository only.

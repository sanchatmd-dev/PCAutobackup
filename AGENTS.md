# Agent working agreement

## Communication
- Use the `caveman` skill throughout this project, as requested by the user. This applies to every session and team: Codex (Astra/Terra/Sol) and Claude Code (Fable/Opus/Sonnet), Root and workers alike. Keep Thai replies concise while preserving technical facts, constraints, and necessary confirmation details. Skill source on this machine: `C:/Users/USER/.codex/skills/caveman/SKILL.md`; a Claude session that does not have the skill installed reads that file at the start of the session and follows it. This preference does not compress persisted code or documentation.

## Team and model policy
- This chat is Root: **Astra, high** (`gpt-6-astra`, high). Root owns architecture, integration, review, tests, release packaging and user communication.
- Delegate only to **Astra medium/low**, **GPT-6.1 Terra low/medium/high**, or **GPT-6.1 Sol low/medium/high**. Do not use other models or effort levels.
- Initial roles: Core/backup engineer = GPT-6.1 Terra high; Google integration engineer = GPT-6.1 Sol high; Windows UI engineer = GPT-6.1 Sol high.
- Claude team (use this team when the session runs in Claude Code; the Astra/Terra/Sol team above applies to Codex sessions):
  - Root: **Claude Fable 5.2, high** (`claude-fable-5-2`, high). Same ownership as Root above.
  - Delegate only to **Claude Opus 5.5 low/medium/high** (`claude-opus-5-5`) or **Claude Sonnet 5.5 low/medium/high** (`claude-sonnet-5-5`). Do not use other models or effort levels.
  - Initial roles: Core/backup engineer = Opus 5.5 high; Google integration engineer = Opus 5.5 high; Windows UI engineer = Sonnet 5.5 high; adversarial review and bug reproduction = Opus 5.5 high; read-only search, measurement runs and mechanical edits = Sonnet 5.5 low/medium.
- Agents share this checkout. Assign disjoint file ownership. Coordinate interface changes with Root. Do not revert other agents' changes. Do not push or publish independently. Agents that build or run tests in parallel work in separate git worktrees and hand Root a patch; only Root edits the main checkout.

## Product contract
- Build a real Windows desktop backup application, not a web mockup. Google Drive is the default destination; external HDD/SSD and custom local folders are alternatives.
- Prioritize low RAM: a small event-driven native tray stays resident; idle WPF settings exit when closed/minimized. This follows the user's later explicit tray/autostart requirement and supersedes the original zero-resident-process design. Use Windows Task Scheduler to launch a bounded, streaming backup worker at 1–24 user-selected daily times, defaulting to 21:00 once daily. Check missed/interrupted rounds after launch, logon, unlock and resume. Never add a watchdog that resists an explicit process termination.
- Default retention: 7 days. Users can set nonnegative whole days (0 = latest successful snapshot only), optional quota in GB, schedule and source folders.
- Enforce retention and quota without deleting the last successful, restorable snapshot. Missing source drives, failed reads, cancellation and failed uploads must not be interpreted as deletions. Commit a snapshot only after all its objects are durable. Do not delete arbitrary destination files.
- Store encrypted backup chunks and encrypted manifests. Recovery must work without the original PC using the recovery key. Protect locally stored keys and OAuth refresh tokens with Windows DPAPI. Never log secrets or ask users to give passwords to the app.
- Google authorization uses system-browser OAuth with PKCE, loopback redirect, state validation and offline access. Confirm the actual returned Google identity and Drive permission; a typed email is only a login hint. Do not fake a working account when credentials are missing.
- Restore must prevent path traversal, follow no reparse points, and avoid silently overwriting files. Reinstalling OS/drivers or guaranteeing arbitrary cross-version app migration is outside the supported file-restore boundary; document actual support candidly.
- Default user-facing language and primary docs are Thai. Provide a persisted English option for Desktop, app diagnostics and Tray. Language changes must not change schedules, sources, destinations, credentials or retention. Never translate file paths or backup contents.

## Engineering
- .NET 10, native WPF UI, separate headless Worker, small Win32 notification-area Tray and shared Core. Keep third-party dependencies minimal. No Electron. User-level installer creates Desktop/Start Menu shortcuts and HKCU logon startup; retain existing schedule/configuration on updates.
- Tests must cover the real failure modes: round-trip recovery, changed/deleted files, missing sources, corrupted/wrong-key data, retention/quota, cancellation, path safety, and OAuth validation where applicable.
- Test only with synthetic fixtures in designated temporary directories. Never back up, upload, prune or restore the user's actual files during development.
- `README.md`: setup, build, run, Google OAuth configuration, recovery, scheduling, limitations. `Contex.md` (intentional spelling): architecture, current state, decisions and next work. Keep both accurate.
- Do not commit generated binaries, credentials, keys, user configuration, backup repositories, SDKs or logs. Supply a reproducible local release build and Windows CI.
- Run build and appropriate automated tests before reporting completion. Measure actual memory; label measurements with workload and do not turn targets into guarantees.

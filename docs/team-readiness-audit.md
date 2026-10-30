# Team readiness - 30 October 2026

**Not yet ready for a general Mac rollout.** The source supports a controlled Mac build/pilot. Actual Apple Silicon/Intel execution and signed/notarized distribution remain unverified.

Current source candidate: `artifacts/release/0.4.19/MissionControl-0.4.19-source.zip`. The desktop has no demo workspace and no longer queries GitHub, opens browser URLs or accesses operating-system credential stores. The Mac-only Rust code compiles for Apple Silicon and Intel without warnings (checked from Windows with Apple's C compiler stubbed out for one dependency); that is a compile check, not a Mac run. Its Mac bundle contains the shell, backend and memory server. The GitHub credential relay remains Windows-only; host GitHub/Slack connections are independent.

## Portability implemented

- Data and host paths resolve from the recipient's environment, with explicit overrides. Mac data defaults to `~/Library/Application Support/MissionControl`.
- Project discovery uses local Git metadata, groups nested repositories and requires each teammate's explicit approval. No personal project/repository allowlist is compiled in.
- New users default to their local timezone and asking before private Slack searches. Updates preserve their choices.
- Native setup preserves unrelated configuration and refuses ownership collisions. Source/release packages exclude generated host profiles, databases, credentials, private transcripts and local test output.
- Mac packaging marks sidecars executable, configures the supported minimum OS and .NET entitlements, and includes setup resources. Use `scripts/build-tauri.mjs`; the bare Tauri command lacks that complete configuration.
- Mac quit and menu-bar behavior is implemented and still requires real Mac verification. The app registers no login item on macOS; people add it under System Settings > General > Login Items.
- Historical database migrations remain for safe upgrades. The obsolete Windows/Mac credential cleanup adapters and retry UI are removed.

## Required before team rollout

1. Build on actual Apple Silicon and Intel Macs and run the .NET, Rust, Python, browser and `scripts/verify-macos.py` checks. Follow [Mac setup](mac-team-setup.md).
2. Sign and notarize with the team's Apple Developer identity. Validate Gatekeeper and stapling on a recipient machine. Bundle-wide .NET entitlements may be narrowed to the two sidecars during signing work.
3. Exercise Finder launch, close/reopen, Cmd+Q, Dock quit, starting from System Settings Login Items, upgrade, data preservation and both native AI-host registrations.
4. Verify each teammate's provider authentication/scopes separately. A connection or schema check is not an end-to-end recap quality test.

## Product limits that remain

- Recaps can omit useful details despite full message delivery; see [benchmarks](../BENCHMARKS.md) and [limitations](../KNOWN_LIMITATIONS.md).
- The local database is not encrypted by Mission Control. Directory permissions, device encryption and source/backup retention remain separate concerns.
- Every developer has their own local installation. There is no shared history service, team RBAC, SSO or central retention management.
- AI-host connections keep the permissions granted in those hosts. A memory skill is not a global sandbox. Direct provider results and host transcripts need their own privacy controls.
- Local branch status uses saved remote references. Unapproved projects and symlink aliases remain excluded; unavailable source history cannot be recovered.

Use the [release checklist](../FINAL_INTEGRATION_CHECKLIST.md) for repeatable verification. Keep dated run reports under ignored `artifacts/` and distinguish Windows/build-plan checks from native Mac results.

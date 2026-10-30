# Release verification checklist

Use this checklist for each release. Store run-specific logs, fingerprints, screenshots and reports under ignored `artifacts/`; do not commit account details, real conversations or machine-specific installation reports. A historical pass is not a pass for the current release.

## Source and automated checks

- Review the working diff and keep version metadata aligned across the desktop, core, MCP and generated packages.
- Run the .NET solution tests, Python setup tests, build-configuration tests and UI typecheck/production build using the README commands. Report skips separately.
- Run `node ui/tests/workspace-flows.mjs` against the rebuilt UI. Verify approved-project boundaries, pagination, local reminders, settings and source notices.
- Run Rust lifecycle tests when shell or packaging changes require them.
- Check Mac and Windows build plans: Mac gets the backend and memory server; Windows also gets the optional GitHub helper. No generated host package is shipped.
- Check that the source ZIP matches the reviewed source and contains no local state. Packaging tests cover databases, transcripts, credentials, caches and unused icons.

## Windows native app

Build with `node scripts/build-tauri.mjs`. Exercise an explicitly isolated data directory, not the operator's live workspace:

```powershell
$env:MC_NATIVE_TEST_DATA_ROOT = Join-Path $env:TEMP ('mc-settings-' + [Guid]::NewGuid().ToString('N'))
$env:MC_DESKTOP_EXE = Join-Path (Get-Location) 'src-tauri/target/x86_64-pc-windows-msvc/release/mission-control.exe'
node ui/tests/settings-buttons.mjs
```

The test checks collection settings, pause/resume, purge/retention, startup registration, close-to-tray, reopen and complete quit. It refuses to replace another instance's startup entry. `native-smoke.mjs`, `native-visibility.mjs`, `tauri-lifecycle.mjs` and `packaged-mcp-smoke.mjs` cover additional isolated UI/transport paths; follow their explicit environment requirements.

For storage changes, build and run the disposable crash/replay fixtures with `scripts/crash-recovery.ps1` and `scripts/replay-restart.ps1`. Never point destructive fixtures at an existing database.

## Package and upgrade

- Run `scripts/package-release.ps1 -Version <version>` only after checks pass; it creates immutable release artifacts and checksums.
- Stop the installed collector before updating locked files. `scripts/update-installed.ps1` refuses a running installed app and creates a verified encrypted backup.
- Compare settings, source choices, project/grouping/approval metadata and host-configuration fingerprints before and after. Log only hashes/counts, never tokens or conversation text.
- Verify installed executable versions and bytes against the tested release. Tauri's NSIS bundle marker differs from the portable shell marker.
- Update memory plugins only when required. Preserve separate GitHub/Slack connections; existing host sessions retain their already-running helpers.

## Mac pilot and remaining evidence

Follow [Mac setup](docs/mac-team-setup.md) on actual Apple Silicon and Intel hardware. Verify signing/notarization, executable permissions, Finder launch, startup items, quit/reopen, update and native host setup. A Windows build-plan test is not Mac execution.

Live provider authentication and paid model recap evaluation are separate opt-in checks. Report the exact dataset/model, reviewed coverage, quality gaps and complete token accounting when measuring recaps. Never infer semantic completeness from successful message delivery.

# Mission Control on a teammate's Mac

The 0.4.18 source contains portability fixes and a reproducible verification path. **A signed Mac release has not yet been built or tested on real Mac hardware.** Start with a technical pilot; the Windows build and tests are not Mac certification. Each person runs a separate local installation with their own accounts and project approvals. There is no shared team database or central administrator service.

## Build and verify a pilot

Use macOS 15 or newer, Apple Silicon or Intel. The .NET 10 supported-platform list currently starts at macOS 15; see [Microsoft's support matrix](https://github.com/dotnet/core/blob/main/release-notes/10.0/supported-os.md). Build on a Mac with Xcode command-line tools, the .NET SDK specified in `global.json`, Node/npm, Rust and Python 3.11+. Users of a completed app do not need Node, Rust or .NET installed; Python is needed only for integration setup.

From the extracted source directory:

```sh
npm ci --prefix ui
dotnet restore MissionControl.slnx --packages .local/nuget
python3 -m unittest discover -s integrations/tests -p 'test_*.py'
node --test scripts/build-config.test.mjs
node scripts/build-tauri.mjs --arch arm64
```

For Intel use `--arch x64`. The Rust target must match: `aarch64-apple-darwin` or `x86_64-apple-darwin`. Prefer building and testing natively on each architecture. This produces separate native apps, not a universal binary. The app/DMG is under `src-tauri/target/<target>/release/bundle/`.

Use the build script above: invoking Tauri's build command directly omits the additional sidecars and complete setup resources. Use the current `MissionControl-0.4.18-source.zip` from `artifacts/release/0.4.18/`.

Run .NET and Rust tests on the Mac too. Use a canonical temporary directory because project privacy checks deliberately reject symlink aliases such as `/var`:

```sh
MC_TEST_TEMP=$(mktemp -d /private/tmp/mission-control-tests.XXXXXX)
TMPDIR="$MC_TEST_TEMP/" dotnet test MissionControl.slnx --no-restore
cargo test --manifest-path src-tauri/Cargo.toml --locked
python3 scripts/verify-macos.py \
  --app 'src-tauri/target/aarch64-apple-darwin/release/bundle/macos/Mission Control.app' \
  --report artifacts/mac-pilot.json
```

The Mac bundle includes the backend and memory server; the GitHub helper is Windows-only. The verifier checks sidecars, executable bits, code signatures, bundled setup resources, indexing of synthetic Claude Code sessions, MCP startup, owner-only new data directories and clean backend shutdown. It uses disposable synthetic data and does not connect to accounts. It does **not** verify the graphical lifecycle or provider authentication.

For distribution, use your organisation's Developer ID signing identity and notarization credentials through Tauri's supported environment variables. No signing identity or credentials are embedded in source. Follow [Tauri's signing/notarization instructions](https://v2.tauri.app/distribute/sign/macos/) and run the verifier with `--distribution` to require Gatekeeper acceptance and a stapled ticket. The bundled .NET executables use [Microsoft's documented apphost entitlements](https://learn.microsoft.com/en-us/dotnet/core/install/macos-notarization-issues). Test the signed result; do not disable Gatekeeper or strip quarantine to claim a release pass.

The current entitlements configuration applies those exceptions to the whole bundle. Before broad distribution, review signing the two .NET sidecars with their required entitlements while keeping the Rust shell narrower; that refinement has not been implemented or verified on Mac.

## Set up each teammate

1. Move the verified app to `/Applications` before configuring plugins. Do not configure a copy running from its DMG or a temporary extraction folder.
2. Open Mission Control, enable the desired local sources in Settings, and approve specific project roots. A parent workspace can group several Git repositories. No project or repository approval is inherited from the developer's PC.
3. Check Settings → Monitoring. Defaults are `~/.claude/projects`, `~/.codex/sessions` and `~/.codex/archived_sessions`. Source roots are editable. `CLAUDE_CONFIG_DIR` and `CODEX_HOME` are also honored when the collector inherits them. Finder does not inherit a terminal's environment, so use the editable source roots for nonstandard installations.
4. Install the desired host CLI, then run setup from Terminal. Choose `claude`, `codex` or `both`:

```sh
python3 '/Applications/Mission Control.app/Contents/Resources/integrations/setup.py' \
  --install --host both
```

Setup generates host packages on **that Mac**, preflights the executable against its existing local database, and uses the hosts' native plugin commands. It preserves unrelated host configuration and refuses conflicting/edited owned entries. Reload the hosts afterward. GitHub and Slack remain separate native connections: each teammate authenticates their own accounts. Do not copy another user's tokens or host configuration.

New recap timezone defaults to the Mac's timezone. Pass `--time-zone Europe/London` or another IANA name to fix a different zone. Private Slack defaults to `ask`; use `--private-slack public` to exclude private conversations or `--private-slack allow` only when that person explicitly chooses it. Repeating setup preserves previous choices.

The desktop has no GitHub account integration or PR overview. GitHub CLI is optional for the separate AI-host GitHub connection; local project discovery and branch reminders require no account login.

## Adjustable locations

| Setting | Default / override |
|---|---|
| App data | `~/Library/Application Support/MissionControl`; launch with `--data-dir /absolute/path` or inherited `MISSION_CONTROL_DATA_DIR` |
| Claude/Codex source roots | Editable in Monitoring; initial defaults honor `CLAUDE_CONFIG_DIR` / `CODEX_HOME` |
| Plugin package directory | `DATA_DIR/host-packages`; setup `--output /absolute/stable/path` |
| MCP executable | Detected from app bundle; setup `--mcp /absolute/path` |
| Codex config home | `CODEX_HOME` or `~/.codex`; setup `--codex-home /absolute/path` |
| Host CLI executables | PATH; setup `--claude-command` / `--codex-command` |
| Recap timezone | Local machine; setup `--time-zone local` or an IANA name |
| Private Slack consent | Per-person saved `ask`, `public` or explicit `allow` |

When changing the data directory, use the same directory for the desktop and setup; setup does not migrate or copy a database. For example, launch the app's `Contents/MacOS/mission-control` executable with `--data-dir`, then pass that path to setup. Use local storage, not an iCloud/network-shared database. macOS uses one app instance per user; simultaneous separate data-directory instances are not supported by the current Mac single-instance plugin.

## Pilot acceptance before rollout

- On each supported architecture, launch from Finder, approve a project, index actual Claude/Codex messages and confirm excluded projects are unavailable to MCP.
- Exercise project roots with spaces, Unicode, a multi-repository parent and a linked Git worktree. Symlink aliases remain deliberately rejected; use the real project path.
- Check permission-denied source diagnostics. macOS may require access to protected Documents/Desktop folders; grant only the access needed for selected projects.
- Close/reopen from the menu bar; minimize/restore; Quit completely; verify no orphan collector. Measure idle CPU/memory on representative history.
- Quitting: closing the window keeps the menu-bar icon; Cmd+Q, Dock > Quit and Quit completely all end both the app and its collector, and a sign-out or shutdown is not interrupted. These paths cannot be exercised on Windows.
- Menu bar: the icon is a monochrome template that follows light and dark mode, and clicking the Dock icon reopens the dashboard.
- Start at login: the app has no login-item setting on macOS; Settings points to System Settings > General > Login Items. Add Mission Control there from `/Applications`, sign out and back in, and confirm the dashboard opens and the collector runs.
- Open loops: commit on a new branch of an approved repository without pushing; the overview's Open loops panel lists it as never pushed, and lists it no longer once it is pushed.
- Register both hosts (where used), run a local memory read, then separately verify personal GitHub and Slack authentication. Setup does not prove provider scopes or workspace administrator approval.
- Test an app update and repeat setup, confirming settings, groups and approvals survive.
- Evaluate a real day. Complete message delivery does not guarantee a complete recap: the existing 24-item comparison still has missing and partial items. Do not treat recap suggestions as an authoritative task ledger.

Local history is best-effort redacted, not encrypted by Mission Control. New Unix workspace directories are owner-only; existing directories are not silently chmodded. Use the team's normal device encryption/access controls. In Claude Code, the plugin removes secret-looking values from Slack results before the model sees them; Codex Slack results are not filtered and can enter that host's transcript. The previously deferred gateway remains unimplemented.

## Updates and removal

Replace the app only after quitting its collector. Relaunch it, then repeat setup from the new app. Keep custom data/package paths stable. Setup and the MCP do not reset the database, copy history between people or grant project approvals. The older `.ps1` update/DPAPI-backup helpers remain Windows-only; do not run them on a Mac.

Use the host's native plugin uninstall command and `manage.py remove-config --host codex --target /absolute/config.toml` to remove owned Codex registration if desired. Removing the app/plugin does not erase local history. Explicitly review the local data directory and backups before deleting them.

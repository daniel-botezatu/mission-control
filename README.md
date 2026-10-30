# Mission Control

Mission Control indexes approved local Claude Code and Codex conversations. Its desktop provides Overview, Projects, Sessions, Search and Timeline; its read-only MCP server supplies evidence to the AI host for developer-memory questions.

Current source release: **0.4.19**. The desktop has no demo workspace: a new installation starts empty and reads nothing until you enable a session source and approve a project. It has no GitHub account queries, PR overview, browser-opening command or operating-system credential cleanup. Open loops shows local branch reminders and conversation drafts. GitHub and Slack connections in Claude/Codex remain separate. Update the memory plugin for 0.4.19's recap instructions, and on Windows the optional GitHub launcher, which now stays stopped until a real GitHub call and stops again after 10 idle minutes.

Windows x64 is tested. Mac packaging supports Apple Silicon and Intel, but a signed build and real Mac pilot are still required before team rollout. See the [readiness audit](docs/team-readiness-audit.md) and [Mac setup guide](docs/mac-team-setup.md).

## Use the desktop

1. Open Mission Control. A new installation starts empty: nothing is read until you enable a source and approve a project.
2. In Settings > Monitoring, enable the local Claude/Codex session folders to observe.
3. In Settings > Approved projects, approve specific project roots. Discovery never grants permission. A parent workspace groups nested Git repositories into one project using local repository/worktree metadata.
4. Resume collection. Search, Sessions and Timeline work without an AI host. Closing the dashboard keeps the collector in the tray; Quit completely stops it.

Only user messages and reliably identified assistant responses are retained. Reasoning traces and raw tool output are excluded; selected command-result facts can accompany an assistant reply. Source files are never changed. Conversations can span days and topics: dates belong to original messages, and long messages remain available through bounded continuation pages.

Overview and Settings > Privacy & storage show live entry/session counts, indexing backlog and disk use. Statistics refresh only while their view and native window are visible. Allocated database space includes reusable pages, so it need not grow with every new record. Backups and original transcripts are separate.

Open loops reads local branch metadata and drafts left in conversations. Branch reminders reflect locally saved remote references, not current GitHub state. A draft reminder does not prove that a message was never sent elsewhere. Every loaded group can expand; the branch limit is explicit.

Session search finds matching conversations; Timeline presents messages by their original date. Source notices identify collection gaps and can be dismissed without changing the underlying coverage evidence. Changed diagnostics appear again.

## Connect Claude Code or Codex

Use [the integration guide](integrations/README.md) or Settings > MCP integration. Generate packages on the recipient's machine; do not copy another user's host settings, tokens or generated plugin cache. Installation explicitly preserves unrelated host configuration.

The memory server is read-only and exposes five tools. The host provides reasoning and its own GitHub/Slack connections. Claude uses restricted history readers; Codex retains a full-message reading fallback. Saved timezone and private-Slack search consent belong to the host package. Local project approval does not grant external-account permissions.

Full-message delivery is not a guarantee of perfect recap quality. Known omissions and measurement limits are documented in [Known limitations](KNOWN_LIMITATIONS.md) and [Benchmarks](BENCHMARKS.md).

## Build and verify

Use the pinned .NET SDK in `global.json`, Node/npm, Rust and Python 3.11+ for development. Python is needed for setup and maintenance only, not collection or MCP serving.

```powershell
dotnet restore MissionControl.slnx --packages .local/nuget
dotnet test MissionControl.slnx --no-restore
npm ci --prefix ui
npm run build --prefix ui
node ui/tests/workspace-flows.mjs
python -X utf8 -m unittest discover -s integrations/tests -p "test_*.py"
node --test scripts/build-config.test.mjs
cargo test --manifest-path src-tauri/Cargo.toml --locked
node scripts/build-tauri.mjs
powershell -ExecutionPolicy Bypass -File scripts/package-release.ps1 -Version 0.4.19
```

The Windows release contains an NSIS installer and portable ZIP in `artifacts/release/0.4.19/`. Keep sidecars beside the portable executable. Edge WebView2 is required; .NET and Node are not separately required at runtime. Mac builds bundle the shell, backend and memory server; the Windows-only GitHub helper is omitted.

Use [the release checklist](FINAL_INTEGRATION_CHECKLIST.md) for native lifecycle, upgrade and preservation checks. The Windows symlink test requires an explicit privilege opt-in; skipped tests must be reported separately. Keep generated output under ignored `artifacts/` or `.local/`. Do not commit databases, host configurations, credentials or real transcripts. Required desktop icons, source fixtures and lockfiles are versioned.

See [Architecture](ARCHITECTURE.md), [research notes](RESEARCH.md) and [Known limitations](KNOWN_LIMITATIONS.md). Historical migration files remain necessary to recognize and upgrade existing databases; removing a retired runtime feature does not make its migration disposable.

## WSL / portability

Windows monitoring does not read WSL UNC mirrors. Publish and run the collector/MCP inside Linux with Linux-local paths and explicit project approvals:

```bash
dotnet publish src/MissionControl.Cli -c Release -r linux-x64 --self-contained true -o artifacts/linux/collector
dotnet publish src/MissionControl.Mcp -c Release -r linux-x64 --self-contained true -o artifacts/linux/mcp
artifacts/linux/collector/MissionControl.Cli approve --root /home/me/work/project --data-dir /home/me/.local/share/MissionControl
artifacts/linux/collector/MissionControl.Cli collect --watch --data-dir /home/me/.local/share/MissionControl
```

Configure the Linux host to use that MCP executable and data directory. Windows and Linux approvals/indexes remain separate. This workflow still needs native Linux/WSL validation.

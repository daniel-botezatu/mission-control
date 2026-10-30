# Mission Control

**Mac team preparation (source 0.4.11):** portability/setup fixes are implemented and Windows regressions pass. A real Mac build, signing/notarization and pilot remain required before team rollout. See [the readiness audit](docs/team-readiness-audit.md) and [Mac setup guide](docs/mac-team-setup.md). No personal settings, history or generated host packages should be distributed.

Version 0.4.16 fixes four issues a review of 0.4.15 found. Saved facts now come only from commands a shell actually executes: searching an old log for a test command, quoted text and dry runs (`git push --dry-run`) record nothing, and a call that also prints files or search results yields no facts because its output cannot be attributed (reading back a file the test itself just wrote still counts). Codex code-mode commands are read reliably, which finds pull requests 0.4.15 missed. Open loops counts unpushed commits the way Git does even when commit dates are out of order, and a branch whose history cannot be read is marked as not verified instead of never pushed.

Version 0.4.15 fixes six issues a review of 0.4.14 found. Open loops counts unpushed commits from each repository's own commit history, still without starting Git: a branch that is only behind is no longer listed, a diverged one is, a branch pushed under another name is not, and a branch whose history cannot be read is marked as not verified. Every group opens in place to show all its items, and review requests link to their own GitHub search. Saved replies take facts only from commands that performed them (`gh pr create`, `git commit`, `git push`, a test run), each labelled with the operation and when it ran, so a log the AI only read adds nothing; Codex code-mode commands now count too. An interrupted turn's last update keeps the time it was written instead of the time of the interruption. The memory server no longer lists a Codex continuation as missing history once its earlier part has been read, matching the dashboard.

Version 0.4.14 adds an **Open loops** panel to the overview: review requests and your pull requests that are blocked or waiting, local branches of approved projects that are not pushed (read from Git's own files; Git is never started), and drafts the AI left at the end of a conversation. It loads only while the overview is open and uses no AI. Each Claude or Codex session's GitHub server now starts on that session's first GitHub call instead of at startup, saving about 65 MB per session that never uses GitHub. In Claude Code, recaps run their coordinator in a fresh helper, so a recap no longer fills the conversation it is asked in, and they end with a checked list of open items; the plugin also removes secret-looking values from Slack results. Saved replies keep pull request links, commit IDs, pushes and test results from the turn's tool output, and an interrupted turn keeps the AI's last update. Codex no longer warns about an earlier conversation part that Mission Control has already read. The desktop records why it started and stopped in `shell-events.log` in its data folder.

Version 0.4.13 no longer reports a source file as missing history when the host deleted it after Mission Control had read it completely (for example Claude Code's 30-day clean-up); only unread parts count. Version 0.4.12 reads Codex rollouts that Codex has compressed (`.jsonl.zst`) as the same conversations as their plain files, and keeps slash commands, command output, compaction summaries and Codex app events out of the user's words (Codex review results are kept as AI-written results). It applies the saved retention period automatically once a day by message date, lets macOS Cmd+Q, Dock > Quit and logout quit normally, uses a monochrome macOS menu-bar icon, and refuses a login item for an app still running from Downloads or its disk image.

**Recap quality:** an internal comparison on one real working day found that reading every message does not by itself preserve every useful recap item; see [Known limitations](KNOWN_LIMITATIONS.md). The evaluation report stays with local evaluation artifacts and is not part of the source package.

Version 0.4.8 sends one copy of each memory response, fills bounded pages efficiently, and repeats coverage only when its meaning changes. Configure the recap timezone (for example `Europe/Amsterdam`) once; calendar-date filters and displayed message times use it, including DST. Memory plugin 0.4.9 removes outline reads from recaps: Claude uses bounded, complete-message readers and merges cited ledgers; Codex retains the full-stream fallback. The optional Windows GitHub launcher reuses the existing GitHub CLI login with a pinned, read-only official server. Slack remains a direct host connection; since 0.4.14 the Claude Code plugin removes secret-looking values from its results (Codex results are not filtered).


Mission Control (desktop, memory plugin and memory server, all versioned together since 0.4.11) provides a local developer-memory dashboard and read-only MCP server. They index approved Claude Code and Codex conversations. Claude Code/Codex provide reasoning; their separate official GitHub and Slack MCP connections provide live work evidence within those hosts.

The memory plugin provides a complete, paged message stream for a requested date window, across all approved sessions. It reads original message dates, including middle topics of chats spanning multiple days, and continues long bodies without omitting their middle. Bounded full MCP responses, signed cursors, content fingerprints and recent-first session reads support a topic-aware recap in the calling host. The plugin supplies evidence rather than inferred topic/completion labels. Existing source gaps remain explicit. The versioned memory server can be updated without stopping desktop collection; no schema change or history reset is needed.

Source notices identify the affected source and can be dismissed. The same notice stays dismissed across dashboard restarts; changed source diagnostics appear again. Details remain available in Settings.

Version 0.4.8 fixes the namespaced Claude read-only helper, scopes repeated path validation to each SQL statement, clears resolved replay warnings, and attributes remaining gaps to matching approved sessions. Compact recap pages carry all messages and topics with fewer repeated identifiers and coverage blocks. Private Slack/DM search consent is an explicit saved host-package preference. Update helpers retain the latest two verified encrypted backups and defer removal of older memory servers still in use.

## Indexing reliability

Version 0.4.3 fixes parser-state loss after long replies, repeated filesystem checks against unrelated repositories, bounded-queue rescan storms, and stale dashboard updates during continuous collection. Overview uses original activity timestamps. Live counters show unread files and bytes; every MCP response reports approved-project/source coverage, including empty results. Long retained messages are available through bounded pages in the dashboard and MCP. Automatic search adds technical synonyms and bounded spelling correction; use `mode: all` or `phrase` for strict matching.

The update preserves settings, source selections, repository groups and approvals. Original transcripts are replayed in place after the parser upgrade; no index clear is required.

## Run the desktop

Build with `node scripts/build-tauri.mjs`, then `scripts/package-release.ps1 -Version <version>` places the installer at `artifacts/release/<version>/Mission Control_<version>_x64-setup.exe` beside a portable ZIP. To use the ZIP, extract it and run `mission-control.exe`, keeping the backend and MCP executables beside it. Microsoft Edge WebView2 is required; .NET and Node are bundled/not required at runtime.

1. Open Mission Control from the Start menu or tray. Switch **Demo → Live**.
2. In **Settings ? Monitoring**, enable the local Claude/Codex session folders you want observed.
3. In **Settings ? Approved projects**, approve specific project roots. Discovery alone never grants permission. An observed parent folder such as `SP` groups its nested Git repositories into one project; repository/worktree metadata supplies attribution.
4. Resume collection. Search, Sessions and Timeline work without an AI host running. Close the dashboard to keep the lightweight collector in the tray; **Quit completely** stops it.

Only user messages and reliably identified final assistant responses enter history. Reasoning, tool output and delegated subagent prompts are excluded. Unknown projects are excluded. Approval revocation immediately purges the project's indexed payload and annotations. **Clear all history** pauses collection; resuming can recover still-available approved source files.

**Sessions** groups whole conversations into cards. **Search** finds messages, decisions and private notes after entering keywords. **Timeline** shows individual messages across conversations, grouped by day and ordered by their original timestamps. Refresh queues reconciliation in the background; source progress appears under **Overview → Collection freshness**.

**Overview** and **Settings → Privacy & storage** show live saved-entry and conversation totals, entry-type counts, disk usage, active database space and the storage budget. Lightweight local statistics refresh every two seconds while that view is visible and the native window is not minimised; they do not reload conversations, restart collection or contact GitHub. Counts cover all approved indexed history rather than only the dashboard's 250 recent rows. Disk usage includes the active database and current write files; active data excludes reusable free pages, so disk allocation can stay flat or fluctuate during indexing. Backups and original session files are separate. The sidebar and footer show the version reported by the running desktop executable.

**Overview → Open loops** shows local branch reminders from approved projects and drafts left in coding conversations. Opening or refreshing this panel reads only local data; it does not contact GitHub or invoke GitHub CLI. Branch status reflects locally saved remote references and can be older than the remote service. The desktop no longer shows PRs or review requests.

## Connect Claude Code or Codex

See [the integration guide](integrations/README.md) for exact installation, optional GitHub/Slack setup, updates and removal. Installing a skill alone is insufficient: register the MCP server explicitly using **Settings ? MCP integration**, or apply the generated native configuration.

The server exposes `search_history`, `get_session_digest`, `get_exchange`, `get_project_activity` and `get_session_details`. It starts on demand over stdio, opens the existing database read-only and exits when its host disconnects. No dashboard is needed to query an already-created index; collection requires the desktop collector or portable CLI.

Example questions: ?Where did I leave off on SP??, ?What did I promise Jordan last week??, ?Find the decision about session timers.? The packaged `developer-memory` skill retrieves bounded evidence and distinguishes claims, observations and current external state.

## Upgrade and privacy

The default data location remains `%LOCALAPPDATA%\MissionControl`. Existing theme, collection preferences, project/repository grouping and relevant conversation history are retained. The initial upgrade from 0.3 to 0.4 requires explicit project approval; 0.4.1 preserves approvals already granted. Before a schema upgrade, the app creates and integrity-checks a SQLite backup under each database's `migration-backups` directory. Original history files are never edited.

The old custom model/GitHub/Slack configurations, cached results and external data are retired. Scoped old credential references are queued for deletion through an upgrade-only native vault adapter; failures remain visible with a retry control. Tokens are never copied into the new integration. Official servers authenticate in the AI host. This is an intentional change from the earlier custom-token setup.

**Backups can contain excluded history and retired configuration.** They are outside MCP access, are not removed by history purge, and should be archived privately or explicitly deleted after checking the upgrade. The pre-refactor source and local `.env` were preserved in ignored `artifacts/refactor-backup`; neither ships in the release. The optional [upgrade helper](scripts/update-installed.ps1) requires the old app to be fully quit, makes a DPAPI-protected workspace backup and runs the selected installer. The installed Windows copy was subsequently upgraded from 0.3.3 to 0.4.0 after a confirmed full quit. The encrypted backup, schema migration, unchanged local preferences, preserved Live/paused state and removal of AI setup were verified; see `artifacts/installed-upgrade-verification/`.

## Build and verify

Development tools: .NET SDK pinned in `global.json`, Node/npm, Rust/Cargo, Windows build tools and WebView2. On Windows use `codex.cmd` if PowerShell blocks the npm shim.

```powershell
dotnet restore MissionControl.slnx --packages .local/nuget
dotnet test MissionControl.slnx --no-restore
npm ci --prefix ui
npm run build --prefix ui
node scripts/build-tauri.mjs
cargo test --manifest-path src-tauri/Cargo.toml --locked
python -X utf8 -m unittest discover -s integrations/tests -p "test_*.py"
# Windows, real app: clicks every Settings button in an isolated instance with a new empty data folder
$env:MC_NATIVE_TEST_DATA_ROOT = "$env:TEMP\mc-settings-buttons-$(Get-Random)"; node ui/tests/settings-buttons.mjs
powershell -ExecutionPolicy Bypass -File scripts/crash-recovery.ps1
powershell -ExecutionPolicy Bypass -File scripts/replay-restart.ps1
```

The native credential test creates only randomly named disposable test credentials and needs a normal Windows logon session. The symlink fixture is opt-in where Windows privileges allow it. See [verification](FINAL_INTEGRATION_CHECKLIST.md), [benchmarks](BENCHMARKS.md), [architecture](ARCHITECTURE.md), [research](RESEARCH.md) and [limitations](KNOWN_LIMITATIONS.md).

## WSL / portability

Do not point Windows monitoring at `\\wsl$` / `\\wsl.localhost`. Publish the portable collector and MCP inside Linux, explicitly approve Linux project paths, then collect there:

```bash
dotnet publish src/MissionControl.Cli -c Release -r linux-x64 --self-contained true -o artifacts/linux/collector
dotnet publish src/MissionControl.Mcp -c Release -r linux-x64 --self-contained true -o artifacts/linux/mcp
artifacts/linux/collector/MissionControl.Cli approve --root /home/me/work/SP --data-dir /home/me/.local/share/MissionControl
artifacts/linux/collector/MissionControl.Cli collect --watch --data-dir /home/me/.local/share/MissionControl
```

Configure a Linux AI host to launch that Linux MCP binary with the same data directory. Windows and Linux approvals/indexes remain separate; no transparent cross-environment path translation is claimed. This workflow and macOS packaging have not been executed here.

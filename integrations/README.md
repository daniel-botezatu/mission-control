Current release: **0.4.19** for the desktop, memory plugin and memory server. 0.4.19 changes the recap instructions for both Claude Code and Codex and, on Windows, the GitHub launcher: install the desktop first, update the plugin and launcher, then reload the hosts.

Recap quality still has gaps on the full 24-item comparison; see [Known limitations](../KNOWN_LIMITATIONS.md). Reloading hosts uses the updated package and server but does not close those gaps.

# Claude Code / Codex integration

For a Mac pilot, use [the Mac setup guide](../docs/mac-team-setup.md) and `setup.py` on the recipient's computer. It detects Mac bundle locations, supports custom data/host paths, preserves choices and registers only selected hosts with `--install`. New users default to their local timezone and `ask` for private Slack. Never distribute packages generated from the builder's profile.

Use absolute paths and the existing data directory. After a separate memory-plugin update, use the versioned executable reported by `scripts/update-memory-plugin.ps1` or recorded in `%LOCALAPPDATA%\MissionControl\host-packages\installation.json`.

## Prepare native packages

Python 3.11+ is needed only for installation. Replace the executable path below with the path shown by the installed desktop. Choose a stable output directory; the hosts reference or cache that package.

```powershell
python integrations/manage.py prepare `
  --output "C:\Tools\MissionControl\host-packages" `
  --mcp "C:\path\to\installed\mission-control-mcp.exe" `
  --data-dir "$env:LOCALAPPDATA\MissionControl"
```

This creates separate `claude/` and `codex/` packages from the shared workflow. No host settings or account credentials change. Re-running the same command updates unchanged owned files and refuses edited/colliding files. Do not edit generated files; change the source or choose a new output directory.

## Claude Code

For immediate local use (tested on 2.1.292):

```powershell
claude --plugin-dir "C:\Tools\MissionControl\host-packages\claude"
```

The plugin supplies `/mission-control:developer-memory` and the restricted `memory-reader` agent. Ask, for example, `/mission-control:developer-memory Where did I leave off on SP last week?`. For recaps, Claude runs the skill in a fresh `mission-control:recap-coordinator` agent with its own context, which spawns up to three fresh `mission-control:memory-reader` agents concurrently and returns only the finished recap. The readers' enforced allowlist contains only local history reads and ToolSearch; no shell, edits, external providers or further delegation. The coordinator cannot run shell commands, edit files or load other skills; it reads, delegates and checks GitHub and Slack. Recall instructions prohibit mutations. Codex coordinates in the main conversation and retains the full-stream fallback until a per-reader tool allowlist is verified.

For persistent user installation, run:

```powershell
claude plugin marketplace add "C:\Tools\MissionControl\host-packages\claude" --scope user
claude plugin install mission-control@mission-control-local --scope user
```

Native trust/path checks apply. Persistent user installation was verified from the normal Windows user profile with a stable package directory. The native CLI loaded the skill, restricted agent, and all five history tools without a model turn. An earlier isolated sandbox check rejected its local path; that does not describe the verified user installation.

For deterministic native MCP registration independent of plugin auto-loading, apply the generated `.mcp.json` entries to an explicit project configuration:

```powershell
python integrations/manage.py apply-config --host claude `
  --bundle "C:\Tools\MissionControl\host-packages" --target "C:\Work\SP\.mcp.json"
```

Claude will ask to approve that project's MCP servers. Alternatively copy the `claude mcp add ...` command from the desktop and register it in your desired host scope. Do not apply a project JSON fragment over Claude's credential/settings files. The installer preserves unrelated server entries, makes a pre-install backup, journals updates and refuses collisions or edits.

## Codex

Tested CLI: 0.160.1. The portable root manifest and compatibility overlay are packaged separately from Claude's manifest.

```powershell
codex.cmd plugin marketplace add "C:\Tools\MissionControl\host-packages\codex"
codex.cmd plugin add mission-control@mission-control-local
python integrations/manage.py apply-config --host codex `
  --bundle "C:\Tools\MissionControl\host-packages" --target "$env:USERPROFILE\.codex\config.toml"
```

Restart Codex; invoke `$developer-memory` with your question. The explicit configuration registers `mission-control` with only the five read tools. The plugin is not assumed to register MCP automatically. The installer appends a marked owned block and preserves other configuration. If `mission-control` already exists, keep that connection or rename/remove it using the host before applying; collisions are deliberately refused.

The packaged workflow forbids shell/coding actions. For a whole-day recap it first discovers every in-scope session, then reads `get_project_activity` with `view: "messages"` and the exact original-message date bounds. Signed `nextCursor` continuations finish long messages before advancing; chats spanning days and topics remain separate evidence records. Each response budgets metadata, JSON escaping and one text representation (16,000 characters by default; configurable from 6,000 to 48,000). The workflow tracks actual review coverage rather than stopping at an arbitrary call count. It does not remove tools or permissions from unrelated Codex workflows. Mission Control's MCP itself has no writes or execution tools.

Compatible memory-server/plugin updates can use the desktop's existing schema-7 index without a reindex or collector shutdown. Prepare the host packages with a versioned MCP path such as `%LOCALAPPDATA%\MissionControl\mcp\<version>\mission-control-mcp.exe`, apply the owned host configuration, then refresh the native plugin cache and restart/reconnect the host. Keep old versioned binaries while existing clients use them; do not kill their processes during updates. A cursor is valid for the server process that issued it; after reconnecting, restart the bounded window. See `scripts/update-memory-plugin.ps1` for the owned local update.

## Add official GitHub and Slack

The Mission Control plugin supplies local history. GitHub and Slack are separate official MCP connections; installing packages does not authenticate an account. Prefer the host's official provider plugins when available:

```powershell
claude plugin install slack@claude-plugins-official --scope user
codex.cmd plugin add github@openai-curated
codex.cmd plugin add slack@openai-curated
```

Confirm that the native host can read the marketplace, load each provider server, and complete sign-in. A successful package-cache command alone does not prove a working account connection. Existing native provider connections can be reused. Provider plugins can expose broader workflows outside Mission Control's restricted memory reader.

The official Windows GitHub MCP executable also supports browser sign-in without a saved PAT. Verify the archive against its official release checksum, register its `stdio` command, and restrict it with `--read-only --toolsets= --tools=...` using the GitHub list in `shared/read-tools.json`. Limit OAuth scopes with `--oauth-scopes=repo,read:org`. Sign-in starts on the first authenticated tool call, and this server retains the token only for its running process. Tool discovery alone does not verify GitHub account authorization. See the [official OAuth guide](https://github.com/github/github-mcp-server/blob/main/docs/oauth-login.md).

For explicit remote configuration instead of a provider plugin:

Regenerate with `--github` for GitHub and, if available, `--slack-client-id YOUR_NUMERIC_CLIENT_ID` for an approved internal Slack MCP app; then repeat `apply-config` for the desired host.

GitHub uses `https://api.githubcopilot.com/mcp/`, server-side `X-MCP-Readonly: true` and an exact read-tool subset. Provide `MISSION_CONTROL_GITHUB_TOKEN` through your normal environment/host credential setup before launching the AI host. The script only writes its environment-variable name. Limit the token to the repositories and read permissions needed; do not paste it into a skill, package or chat. Existing official GitHub connections may be used instead if their read tools are available and scoped appropriately.

Slack uses `https://mcp.slack.com/mcp` and the host's OAuth flow. Your workspace/admin must allow it. Supply your own registered internal app client ID with the documented loopback callback and PKCE support; the client ID is public metadata, not an `xoxp`/`xoxb` token. After applying Codex configuration, run `codex.cmd mcp login mc_slack`. In Claude use `/mcp` to authenticate. Slack has no documented server-wide read-only switch: Codex `enabled_tools` limits the managed server to listed reads. In Claude, the coordinator is instructed to use reads only; child readers cannot access Slack at all. The optional official Claude Slack plugin can supply its supported onboarding (`/plugin install slack@claude-plugins-official`); that plugin has broader workflows outside Mission Control's reader.

Keep OAuth metadata with the host application it was registered for: Claude's official plugin preset belongs in Claude; Codex's belongs in Codex. For an internal app, use your own client ID. The old custom Mission Control Slack/GitHub tokens are not imported. Native server discovery was verified; account authorization and external reads remain separate checks. Official setup details: [GitHub MCP](https://github.com/github/github-mcp-server), [Slack MCP harness guide](https://docs.slack.dev/ai/slack-mcp-server/connect-to-harnesses/).

## Update and remove

Regenerate at the same stable output with `prepare`; repeat `apply-config` and refresh/reinstall the native plugin cache through the host. If an owned block/file was edited, preserve it and resolve the diff explicitly; the installer refuses to overwrite it. An interrupted config write recovers from its journal when the next operation runs.

```powershell
python integrations/manage.py remove-config --host codex --target "$env:USERPROFILE\.codex\config.toml"
python integrations/manage.py remove-config --host claude --target "C:\Work\SP\.mcp.json"
codex.cmd plugin remove mission-control@mission-control-local
# In Claude: /plugin uninstall mission-control@mission-control-local
python integrations/manage.py remove-package --output "C:\Tools\MissionControl\host-packages"
```

Only unchanged owned entries/files are removed. Other MCP servers, unrelated files, native OAuth storage and pre-install backups remain. Remove account authorizations through each host/provider if desired. When moving the executable or data directory, regenerate packages and update native configuration rather than editing generated ownership state.

## Validation boundaries

Native Codex plugin installation, five-tool discovery and actual `search_history` execution passed through its app-server without a model turn. Claude loaded the skill/agent and discovered all five tools through its native CLI control protocol; model-mediated Claude execution was not performed. Standard MCP client/protocol and packaged executable tests exercised all five tools. Seven synthetic multi-source scenarios reviewed the workflow, not actual model behavior. Real GitHub/Slack OAuth, external tool calls and host-model cross-source answers remain account-dependent checks.

The generated Claude skill coordinates named `mission-control:memory-reader` children with history-only tool allowlists. Provider reads occur in the coordinator. Save private Slack/DM recap consent with `manage.py prepare --private-slack allow` (or `public` / `ask`); subsequent package updates preserve the choice. Public-only remains the scope until consent is supplied. This is search permission, never permission to send messages.

For whole-day recaps, use `get_project_activity(view: "messages", format: "compact", bodyCharacters: 48000, maxOutputCharacters: 48000, limit: 50)` with explicit original-message date bounds. Continue every cursor. Conversation groups are per page; all messages are retained. Coverage is sent initially and when changed. Compact record references can be opened with `get_exchange(recordId)`. First/last snippets cannot establish complete coverage.

## Retrieval and sign-in behavior

Use `prepare --time-zone Europe/Amsterdam` to save the user's recap timezone. Updates retain this choice. Every history tool accepts `timeZone`; date-only bounds are local calendar dates in that zone and `before` remains exclusive. Explicit timestamps require an offset or Z. Returned message times include their local offset. Date boundaries account for DST; do not add the offset again in the recap.

The memory server returns one JSON text block, without a duplicate `structuredContent`. Existing clients should parse that block once. For full message windows use `bodyCharacters: 48000, maxOutputCharacters: 48000`; the wire budget still includes escaping and framing. Coverage appears on the first message/outline page and again only when approvals, source state, backlog or gap evidence changes. Routine sync timestamps do not repeat it.

The legacy `view: "outline"` API remains compatible, but 0.4.9 recaps never call it. All assigned messages are read in full, including continuations. Required tests, blockers and ordering constraints remain in the final topic ledger until later evidence resolves them. This does not guarantee a model will never omit an item or meet a fixed token budget.

On Windows, `mission-control-providers.exe github --gh-executable ABS\gh.exe --github-server ABS\github-mcp-server.exe --github-sha256 SHA256` launches the verified official GitHub server with the existing `gh auth login` credential. Only the fixed read-tool set is enabled. The launcher captures the credential in memory, passes it only to the child environment, and suppresses credential/upstream diagnostics. It does not copy tokens into plugin files or alter the GitHub CLI credential store. `configure_github_provider.py` updates only a recognized existing `mc_github` registration and checks that unrelated settings are unchanged. Sign-in can still be required if the saved login expires or is revoked; fresh server processes no longer start their own OAuth login while the existing gh credential works.

The launcher starts the server, and reads the gh credential, only on a session's first GitHub call, and stops it again after 10 minutes without a request in progress; the next call starts it again and replays the session's handshake. Until then it answers the host's startup requests from `github-mcp-server.exe.startup-cache.json` next to the pinned server: initialize and the tool, prompt and resource lists, and, since 0.4.19, the `server/discover` probe that 2026-07-28 hosts such as current Claude Code send instead of initialize. These are the server's own earlier answers, which it marks public and which contain tool descriptions only. The cache is keyed by the server's SHA-256 and the read-tool set, and each answer by protocol revision (and, for per-request metadata, by the declared client capabilities). A session the cache cannot answer starts the server at once and refills it; concurrent sessions merge their answers. A failed start answers that call with the fixed sign-in message, and the next call tries again.

Claude's plugin applies a best-effort secret filter to Slack results. Codex still receives results directly. Host transcripts can retain sensitive values the filter misses. Existing Slack authentication and consent are unchanged.

`maintain-plugin-cache.ps1` keeps current and immediately prior Mission Control cache versions, validates ownership and rejects redirected paths before deleting older entries. In-use memory-server binaries are retained until their host reconnects and a later cleanup runs.

## 0.4.9 full-read partitions

`get_project_activity(view: "partitions", from, before, timeZone)` returns a body-free, paginated manifest. Default target is 80000 UTF-8 body bytes per conversation slice. Equal-timestamp messages remain together; oversized groups are flagged and still read in full. Copy each `readArguments` object to the messages view. `expectedRevision` rejects changed assignments; compare the overall `manifestRevision` on every manifest page and again before merging to catch added or removed partitions. `sessionId` is now supported in activity reads and bound into continuation cursors. Lifecycle-only sessions remain separately visible in the session inventory. No schema migration or ledger persistence is introduced.

Recap windows at or below 80000 body bytes use one fresh reader and fullReadArguments; larger windows use partitioned readers. The threshold is not a measured break-even guarantee. Claude readers return receipts and cited topic/checklist ledgers, and the parent merges all completed receipts before external verification. Cross-partition completion, conflicting evidence and dependencies require reconciliation; original exchanges can be reopened. Full delivery does not prove perfect semantic recall, as the real-day comparison demonstrates. Codex uses the same full-message protocol without outlines, sequentially. Memory-only updates need no collector shutdown.

Reproducible probes: `integrations/tests/recap_delivery_probe.py` compares exact content fingerprints without logging bodies; `recap_model_probe.py` measures actual native Claude usage against isolated synthetic data with external servers disabled and a per-run budget cap. Character accumulation is not token usage or billed cost. A supplied real checklist/recap is needed for the requested blind real-day evaluation.

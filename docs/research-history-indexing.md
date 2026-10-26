# Conversation history indexing research

Verified 21 October 2026 against the public repositories, their current release lists, issue lists, and official host documentation. These are research references, not runtime dependencies; no upstream application was copied or installed.

## Reference implementations inspected

| Reference | Inspected HEAD | Latest release observed | Useful approach and limits |
| --- | --- | --- | --- |
| [nicknisi/sessions](https://github.com/nicknisi/sessions) | `9293eb6a8ed7a92c27c9c531061bd20e5b8bd1cb`, 20 October | `v1.32.0` | Shared SQLite/FTS message index for CLI and MCP; real cwd from transcript metadata; genuine human-turn filtering; avoid repeated full refreshes during bursts. Its wider reports, tool indexing and transcript vault are outside this product. |
| [Tien-Lam/agent-history](https://github.com/Tien-Lam/agent-history) | `db16c16c9721eeb950a60583438aed38c81e855f`, 29 May | `v0.3.1` | Stable provider/session/turn references and small stdio MCP interface. The inspected provider implementations distinguish content types. We retain SQLite FTS5 instead of introducing Tantivy, embeddings, remote rsync or LLM extraction. |
| [jhlee0409/claude-code-history-viewer](https://github.com/jhlee0409/claude-code-history-viewer) | `67979b401e936ed503c941a3f3294012f51d730e`, 20 October | `v1.32.2` | Explicit providers and bounded history opening. Current issues include Windows path/open guards, image detection and question-answer classification. Relevant Windows extended-length watch-path repair was released in v1.31.0. We preserve native path validation and text blocks without image-string heuristics. |
| [LinnkLabs/AgentHistory](https://github.com/LinnkLabs/AgentHistory) | `389a70dea14672baef66f5331cabba489b1d7189`, 20 October | `v0.1.7` | Its `service/src/codex.mjs` reads real cwd and excludes generated user envelopes. It indexes more assistant/tool/reasoning categories than Mission Control should retain; we deliberately use stricter final-answer admission. |
| [campbellcharlie/momento](https://github.com/campbellcharlie/momento) | `c12277cfbbee5e2cc26d84f26a0a3cf3e584c23b`, 6 October | No GitHub release published | Lightweight MCP plus SQLite; parser UUIDs and source tool-result error flags. We keep message references but do not persist terminal output or infer verification from prose/tool names. |

The release and issue lists were queried through the public GitHub REST API after checking the repository pages. Open issue lists were empty for sessions, AgentHistory and momento. Agent-history's only returned open item was dependency PR #10. Viewer items included [#715](https://github.com/jhlee0409/claude-code-history-viewer/issues/715) about overbroad image detection, [#712](https://github.com/jhlee0409/claude-code-history-viewer/issues/712) about file-opening guards, and [#709](https://github.com/jhlee0409/claude-code-history-viewer/issues/709) about question replies. These reports informed conservative filtering; they are not claims that every upstream issue is reproduced or resolved here.

Pinned source entry points:

- [sessions parser](https://github.com/nicknisi/sessions/blob/9293eb6a8ed7a92c27c9c531061bd20e5b8bd1cb/src/parser.ts)
- [agent-history Codex messages](https://github.com/Tien-Lam/agent-history/blob/db16c16c9721eeb950a60583438aed38c81e855f/src/provider/codex_cli/parse/messages.rs)
- [viewer Claude provider](https://github.com/jhlee0409/claude-code-history-viewer/blob/67979b401e936ed503c941a3f3294012f51d730e/src-tauri/src/providers/claude.rs)
- [AgentHistory Codex parser](https://github.com/LinnkLabs/AgentHistory/blob/389a70dea14672baef66f5331cabba489b1d7189/service/src/codex.mjs)
- [momento parser](https://github.com/campbellcharlie/momento/blob/c12277cfbbee5e2cc26d84f26a0a3cf3e584c23b/src/parser.ts)

## Official host contracts and extraction decisions

[Claude Code's session documentation](https://code.claude.com/docs/en/sessions) documents continuously saved local JSONL transcripts, session/project association, resuming and retention. [The Agent SDK session documentation](https://code.claude.com/docs/en/agent-sdk/sessions) describes local persistence and the distinction between session history and filesystem state. It does not make every internal JSONL field a permanently versioned public API. Our reader treats the raw schema as compatibility-sensitive and reports malformed/unsupported records instead of calling the SDK or starting an agent. [Anthropic issue #25461](https://github.com/anthropics/claude-code/issues/25461) also illustrates why “latest modified transcript” must never stand in for session identity: we use recorded session IDs, not the most recent global file.

[OpenAI's protocol source](https://github.com/openai/codex/blob/main/codex-rs/protocol/src/protocol.rs) currently specifies session metadata, turn-context IDs and `TurnCompleteEvent`, serialized as `task_complete` with `turn_complete` accepted as an alias. Its `last_agent_message` is a bounded fallback for older phase-less final responses. [Protocol models](https://github.com/openai/codex/blob/main/codex-rs/protocol/src/models.rs) distinguish assistant `commentary` and `final_answer` phases. These source definitions are more precise than assuming every assistant message ends a turn.

Implemented admission rules:

- Codex: genuine `response_item` user messages and assistant `phase=final_answer`. Phase-less assistant text is ignored; explicit `task_complete`/`turn_complete.last_agent_message` may provide the final reply. Mirrored `user_message`/`agent_message` events are not indexed a second time.
- Claude Code: genuine user text, excluding `tool_result`, `isMeta`, generated instructions, hook notices and interruption placeholders. Text from the last assistant message is a bounded pending candidate until `stop_reason=end_turn`, a recorded `system/turn_duration` event, or the next genuine user request. `tool_use` invalidates the preceding intermediate candidate. A bare end-of-file never proves finality.
- Thinking/reasoning blocks, tool input/output, terminal logs, attachments and image data are omitted. Plain user text containing markup, Markdown headings or image URLs remains text.
- Explicit start, complete, abort and error events are small lifecycle records. `task_complete` means the host ended a turn, not that code is correct or tests passed. No execution/test success is inferred from an assistant's final prose. Optional structured verification extraction is currently omitted rather than guessing from arbitrary output.
- Each admitted message carries `schema=memory-v1`, `phase=user|final`, `turnId`, deterministic `exchangeId`, `sourceRecordId`, source writer version and a finality-source flag. Existing source/stream/session IDs and timestamps provide the original-file reference. A turn's duplicated final-response/event envelopes map to one stable final record ID. Lifecycle rows use `phase=lifecycle`.

Claude finality is conservative rather than complete: transcripts ending with phase-less assistant text and no reliable boundary leave that text pending. Unknown Claude forks/rewinds and Codex paginated/referenced history are not reconstructed into an authoritative active branch. Available messages are source observations, with gaps reported where detected. No conversation is controlled, resumed, rewritten or deleted at the source.

## Privacy and incremental recovery

A cwd must match an explicitly approved native project root, worktree or workspace alias before content is extracted into a retained message or pending checkpoint. Unknown cwd metadata can drive project discovery, but never authorizes indexing. Ingestion rechecks the current allowlist at commit; retrieval independently enforces it against cwd and project approval. Changing a displayed project label cannot grant access. Source JSONL is parsed in a bounded transient buffer; excluded message content is never saved as an evidence row, pending text, FTS entry or derivation queue.

The existing byte-offset, prefix/anchor, bounded-queue and SQLite transaction design is retained. Complete lines and their checkpoint commit atomically. Partial last lines wait for their newline; oversized lines are discarded across bounded passes; rotation/truncation replays available records with stable IDs. Parser-version changes trigger one replay. Pending approved Claude text is redacted, limited to 12,000 characters, and persisted with the offset so a restart does not lose the final candidate. Approval changes clear checkpoints so newly admitted available history can be replayed and revoked pending text is removed.

No new parser, watcher, indexing or AI dependency is introduced.

## Windows and WSL

Native Windows histories are read from enabled local Claude/Codex source folders; configurable host roots respect `CLAUDE_CONFIG_DIR` and `CODEX_HOME`. Codex active and archived sessions are separate discovery suggestions. Sources begin disabled.

WSL is a different filesystem/environment. The Windows observer rejects UNC paths, reparse boundaries and `environment=wsl`; it does not claim Windows FileSystemWatcher reliably observes Linux files. The same portable core can run in the Linux/WSL collector process with `environment=wsl`, native Linux paths and its own explicitly approved roots/database. MCP must then run in that environment or be launched using an explicit WSL command. [Official Codex Windows guidance](https://developers.openai.com/codex/windows) distinguishes native and WSL execution. Actual WSL distro execution must be tested separately; Windows-only unit fixtures are not evidence of a live WSL integration test.

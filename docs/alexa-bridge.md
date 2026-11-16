# Optional Alexa history bridge

`MissionControl.Cli alexa-bridge --data-dir <dedicated-directory> --config <config.json>`
exposes the existing approved local-history collector over authenticated loopback
HTTP. It does not start from the desktop app. Use a separate database, never the
desktop application's data directory. Original Claude/Codex transcripts are read
only; source account identities cannot be rebound in an existing store.

Configuration contains `port`, `token_file`, `sources` and optional
`allow_project_sync` (default false). Each source has a stable `id`, `tool`
(`claude` or `codex`), `account` (`shared` for Claude, `work` or `personal` for
Codex) and an absolute local `root`. The token file contains a random 32–256
character bearer token. Browser Origin requests are rejected and request logging
is disabled. This is a local trusted-client API, not an unauthenticated LAN API.

Endpoints:

- `GET /projects`: approved project IDs and roots.
- `GET /find_sessions`: bounded, project/account/tool/branch/text-filtered sessions.
- `GET /events`: bounded SSE lifecycle notifications; an observation is never proof
  that a coding task passed tests.
- `GET /observations?after=<sequence>`: durable, approval-filtered reconciliation
  with a journal ID. Consumers reset their cursor when the journal changes.
- `POST /ingest`: request an immediate read of a source-scoped transcript; ordinary
  watching remains available. Outside paths and source rebinding are rejected.
- `GET /health`: readiness and whether managed scope synchronization is enabled.

With `allow_project_sync: true`, `POST /project-scopes` accepts the complete desired
array of `{ "name": "Project", "root_path": "C:\\Projects\\example", "approved": true }`.
The client must include denied child roots as well as approved parents. Alexa
derives these decisions from Mission Control's existing approvals; importing a
name/path alone grants no history access. This endpoint changes only the dedicated
bridge store. All other scope approvals are revoked, including removed roots;
denied roots remain so an approved parent cannot absorb them. Revocation purges
retained records, annotations, checkpoints and old WAL pages. Unchanged snapshots
renew the lease without rescanning.

Managed stores require `.alexa-bridge-owned` containing `alexa-bridge-store-v1`.
A fresh directory receives it automatically. An existing unmarked database is
rejected before opening it writable. Migration of a known old Alexa bridge store
requires the owner to establish that marker explicitly; never mark the desktop DB.
On startup no old approval is active until synchronization succeeds. A 75-second
renewable lease blocks history reads when expired; the collector then stops and
revokes the stale scopes. The desktop store and its settings are not edited.

Validation: core tests cover durable journal replay, source/approval filtering,
atomic rollback on invalid scopes, denied nested roots, legacy ID preservation,
canonical broad-root rejection, existing-store refusal and byte-level DB/WAL purge.
The local Alexa integration also exercises real loopback auth, approval/revocation,
reindexing, unchanged transcript hashes and lease expiry with synthetic data.

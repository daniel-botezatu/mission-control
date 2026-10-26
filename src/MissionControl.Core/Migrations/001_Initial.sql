CREATE TABLE sources (
    id TEXT PRIMARY KEY, kind TEXT NOT NULL, name TEXT NOT NULL,
    root_path TEXT NOT NULL, enabled INTEGER NOT NULL DEFAULT 0,
    environment TEXT NOT NULL DEFAULT 'windows'
);
CREATE TABLE source_status (
    source_id TEXT PRIMARY KEY, state TEXT NOT NULL,
    last_synced TEXT, pending INTEGER NOT NULL DEFAULT 0, gap TEXT
);
CREATE TABLE projects (
    id TEXT PRIMARY KEY, name TEXT NOT NULL, color TEXT NOT NULL,
    description TEXT NOT NULL, root_path TEXT
);
CREATE TABLE evidence (
    id TEXT PRIMARY KEY, source_id TEXT NOT NULL, stream_id TEXT NOT NULL,
    session_id TEXT NOT NULL, project_id TEXT, source_kind TEXT NOT NULL,
    kind TEXT NOT NULL, title TEXT NOT NULL, body TEXT NOT NULL,
    author TEXT NOT NULL, timestamp TEXT NOT NULL, ingested_at TEXT NOT NULL,
    working_directory TEXT, branch TEXT, state TEXT, url TEXT, data_json TEXT
);
CREATE INDEX evidence_timestamp ON evidence(timestamp DESC);
CREATE INDEX evidence_project_timestamp ON evidence(project_id, timestamp DESC);
CREATE INDEX evidence_source_stream ON evidence(source_id, stream_id);
CREATE INDEX evidence_session ON evidence(source_id, session_id);
CREATE INDEX evidence_kind_state ON evidence(kind, state);
CREATE TABLE checkpoints (
    source_id TEXT NOT NULL, stream_id TEXT NOT NULL, offset INTEGER NOT NULL,
    fingerprint TEXT NOT NULL, anchor TEXT NOT NULL, length INTEGER NOT NULL,
    modified_ticks INTEGER NOT NULL, metadata_json TEXT,
    updated_at TEXT NOT NULL, PRIMARY KEY(source_id, stream_id)
);
CREATE TABLE corrections (
    record_id TEXT PRIMARY KEY, project_id TEXT, task TEXT,
    dismissed INTEGER NOT NULL DEFAULT 0, snoozed_until TEXT, note TEXT
);
CREATE TABLE settings (key TEXT PRIMARY KEY, value TEXT NOT NULL);
CREATE TABLE cache (
    key TEXT PRIMARY KEY, source_version TEXT NOT NULL,
    value TEXT NOT NULL, created_at TEXT NOT NULL
);
CREATE TABLE workspace_meta (
    id INTEGER PRIMARY KEY CHECK(id = 1), evidence_version INTEGER NOT NULL
);
INSERT INTO workspace_meta(id, evidence_version) VALUES(1, 0);
CREATE VIRTUAL TABLE evidence_fts USING fts5(
    title, body, author, content='evidence', content_rowid='rowid',
    tokenize='unicode61 remove_diacritics 2'
);
INSERT INTO evidence_fts(evidence_fts,rank) VALUES('secure-delete',1);
CREATE TRIGGER evidence_ai AFTER INSERT ON evidence BEGIN
    INSERT INTO evidence_fts(rowid, title, body, author)
    VALUES(new.rowid, new.title, new.body, new.author);
END;
CREATE TRIGGER evidence_ad AFTER DELETE ON evidence BEGIN
    INSERT INTO evidence_fts(evidence_fts, rowid, title, body, author)
    VALUES('delete', old.rowid, old.title, old.body, old.author);
END;
CREATE TRIGGER evidence_au AFTER UPDATE ON evidence BEGIN
    INSERT INTO evidence_fts(evidence_fts, rowid, title, body, author)
    VALUES('delete', old.rowid, old.title, old.body, old.author);
    INSERT INTO evidence_fts(rowid, title, body, author)
    VALUES(new.rowid, new.title, new.body, new.author);
END;

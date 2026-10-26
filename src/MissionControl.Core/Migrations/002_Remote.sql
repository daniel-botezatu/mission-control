ALTER TABLE evidence ADD COLUMN source_version TEXT;

CREATE TABLE remote_connections (
    id TEXT PRIMARY KEY, kind TEXT NOT NULL, name TEXT NOT NULL,
    endpoint TEXT NOT NULL, account_id TEXT, account_name TEXT,
    poll_minutes INTEGER NOT NULL, lookback_days INTEGER NOT NULL,
    enabled INTEGER NOT NULL, last_connected_at TEXT, workspace_id TEXT
);
CREATE TABLE remote_selections (
    source_id TEXT PRIMARY KEY, connection_id TEXT NOT NULL,
    remote_id TEXT NOT NULL, name TEXT NOT NULL, project_id TEXT,
    enabled INTEGER NOT NULL,
    FOREIGN KEY(connection_id) REFERENCES remote_connections(id) ON DELETE CASCADE,
    UNIQUE(connection_id,remote_id)
);
CREATE INDEX remote_selections_connection ON remote_selections(connection_id,source_id);
CREATE TABLE remote_checkpoints (
    source_id TEXT NOT NULL, stream_id TEXT NOT NULL, cursor TEXT,
    high_watermark TEXT, overlap_start TEXT, updated_at TEXT NOT NULL,
    PRIMARY KEY(source_id,stream_id)
);
CREATE INDEX evidence_source_stream_session ON evidence(source_id,stream_id,session_id);

CREATE TABLE derived_commitments (
    id TEXT PRIMARY KEY, record_id TEXT NOT NULL, kind TEXT NOT NULL,
    title TEXT NOT NULL, quote TEXT NOT NULL, due_text TEXT, is_self INTEGER NOT NULL,
    fingerprint TEXT NOT NULL, active INTEGER NOT NULL,
    FOREIGN KEY(record_id) REFERENCES evidence(id) ON DELETE CASCADE
);
CREATE INDEX derived_commitments_record ON derived_commitments(record_id);
CREATE INDEX derived_commitments_active ON derived_commitments(active,record_id);
CREATE TABLE commitment_corrections (
    commitment_id TEXT PRIMARY KEY, status TEXT NOT NULL, title TEXT, project_id TEXT,
    task TEXT, snoozed_until TEXT, note TEXT, reviewed_fingerprint TEXT NOT NULL,
    FOREIGN KEY(commitment_id) REFERENCES derived_commitments(id) ON DELETE CASCADE
);
CREATE TABLE commitment_derivation_queue (
    record_id TEXT PRIMARY KEY, revision INTEGER NOT NULL,
    FOREIGN KEY(record_id) REFERENCES evidence(id) ON DELETE CASCADE
);
INSERT INTO commitment_derivation_queue(record_id,revision) SELECT id,1 FROM evidence;
CREATE TRIGGER evidence_commitment_insert AFTER INSERT ON evidence BEGIN
    INSERT INTO commitment_derivation_queue(record_id,revision) VALUES(new.id,1)
    ON CONFLICT(record_id) DO UPDATE SET revision=revision+1;
END;
CREATE TRIGGER evidence_commitment_update AFTER UPDATE ON evidence BEGIN
    UPDATE derived_commitments SET active=0,title='[Source message deleted]',quote='',due_text=NULL
    WHERE record_id=new.id AND new.state='deleted';
    INSERT INTO commitment_derivation_queue(record_id,revision) VALUES(new.id,1)
    ON CONFLICT(record_id) DO UPDATE SET revision=revision+1;
END;

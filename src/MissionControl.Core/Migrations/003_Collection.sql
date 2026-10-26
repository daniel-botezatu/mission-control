ALTER TABLE evidence ADD COLUMN changed_at TEXT NOT NULL DEFAULT '';
UPDATE evidence SET changed_at=COALESCE(source_version,timestamp);
CREATE INDEX evidence_changed ON evidence(changed_at DESC,id);
CREATE TABLE session_corrections (
    source_id TEXT NOT NULL,
    session_id TEXT NOT NULL,
    project_id TEXT NOT NULL,
    PRIMARY KEY(source_id,session_id)
);

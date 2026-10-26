-- Local developer memory: discovery never grants indexing permission.
ALTER TABLE projects ADD COLUMN is_approved INTEGER NOT NULL DEFAULT 0 CHECK(is_approved IN (0,1));
ALTER TABLE projects ADD COLUMN approved_at TEXT;
CREATE TABLE retired_credentials (key TEXT PRIMARY KEY);
INSERT INTO retired_credentials(key) SELECT 'live-integrations' || char(10) || '*' WHERE EXISTS(SELECT 1 FROM remote_connections);
INSERT INTO retired_credentials(key) SELECT 'live-integrations' || char(10) || 'remote:' || id || ':token' FROM remote_connections;
INSERT INTO retired_credentials(key) SELECT 'live-integrations' || char(10) || 'remote:' || id || ':socket' FROM remote_connections;

-- Preserve eligible local human history in quarantine, with no searchable entries until approval.
-- Old assistant bodies lack a reliable final-response marker and must be re-extracted from source.
DELETE FROM corrections WHERE record_id IN (
    SELECT id FROM evidence WHERE source_kind NOT IN ('claude','codex') OR kind NOT IN ('message','lifecycle')
        OR author NOT IN ('user','assistant','system')
        OR (author='assistant' AND CASE WHEN json_valid(data_json) THEN COALESCE(json_extract(data_json,'$.phase'),'') ELSE '' END<>'final')
        OR (kind='lifecycle' AND CASE WHEN json_valid(data_json) THEN COALESCE(json_extract(data_json,'$.schema'),'') ELSE '' END<>'memory-v1')
);
DELETE FROM evidence WHERE source_kind NOT IN ('claude','codex') OR kind NOT IN ('message','lifecycle')
    OR author NOT IN ('user','assistant','system')
    OR (author='assistant' AND CASE WHEN json_valid(data_json) THEN COALESCE(json_extract(data_json,'$.phase'),'') ELSE '' END<>'final')
    OR (kind='lifecycle' AND CASE WHEN json_valid(data_json) THEN COALESCE(json_extract(data_json,'$.schema'),'') ELSE '' END<>'memory-v1');
DELETE FROM session_corrections WHERE NOT EXISTS(SELECT 1 FROM evidence WHERE evidence.source_id=session_corrections.source_id AND evidence.session_id=session_corrections.session_id);
DELETE FROM checkpoints;
DELETE FROM source_status WHERE source_id IN (SELECT id FROM sources WHERE kind NOT IN ('claude','codex','fixture'));
DELETE FROM sources WHERE kind NOT IN ('claude','codex','fixture');
DELETE FROM settings WHERE key LIKE 'ai.%' OR key LIKE 'remote.%' OR key LIKE 'commitments.%' OR key LIKE 'mcp.%';
DROP TRIGGER evidence_commitment_insert;
DROP TRIGGER evidence_commitment_update;
DROP TRIGGER commitment_corrections_ai;
DROP TRIGGER commitment_corrections_ad;
DROP TRIGGER commitment_corrections_au;
DROP TABLE commitment_corrections_fts;
DROP TABLE commitment_corrections;
DROP TABLE commitment_derivation_queue;
DROP TABLE derived_commitments;
DROP TABLE remote_checkpoints;
DROP TABLE remote_selections;
DROP TABLE remote_connections;
DROP TABLE cache;

-- Contentless-delete indexes safely tolerate quarantined rows which were never indexed.
DROP TRIGGER evidence_ai;
DROP TRIGGER evidence_ad;
DROP TRIGGER evidence_au;
DROP TABLE evidence_fts;
CREATE VIRTUAL TABLE evidence_fts USING fts5(title,body,author,content='',contentless_delete=1,tokenize='unicode61 remove_diacritics 2');
INSERT INTO evidence_fts(evidence_fts,rank) VALUES('secure-delete',1);
CREATE TRIGGER evidence_ai AFTER INSERT ON evidence BEGIN
    INSERT INTO evidence_fts(rowid,title,body,author) SELECT new.rowid,new.title,new.body,new.author
        WHERE EXISTS(SELECT 1 FROM projects WHERE id=new.project_id AND is_approved=1);
END;
CREATE TRIGGER evidence_ad AFTER DELETE ON evidence BEGIN
    DELETE FROM evidence_fts WHERE rowid=old.rowid;
    DELETE FROM corrections WHERE record_id=old.id;
END;
CREATE TRIGGER evidence_au AFTER UPDATE ON evidence BEGIN
    DELETE FROM evidence_fts WHERE rowid=old.rowid;
    INSERT INTO evidence_fts(rowid,title,body,author) SELECT new.rowid,new.title,new.body,new.author
        WHERE EXISTS(SELECT 1 FROM projects WHERE id=new.project_id AND is_approved=1);
END;
DROP TRIGGER corrections_ai;
DROP TRIGGER corrections_ad;
DROP TRIGGER corrections_au;
DROP TABLE corrections_fts;
CREATE VIRTUAL TABLE corrections_fts USING fts5(task,note,content='',contentless_delete=1,tokenize='unicode61 remove_diacritics 2');
INSERT INTO corrections_fts(corrections_fts,rank) VALUES('secure-delete',1);
CREATE TRIGGER corrections_ai AFTER INSERT ON corrections BEGIN
    INSERT INTO corrections_fts(rowid,task,note) SELECT new.rowid,new.task,new.note
        WHERE EXISTS(SELECT 1 FROM evidence r JOIN projects p ON p.id=r.project_id WHERE r.id=new.record_id AND p.is_approved=1);
END;
CREATE TRIGGER corrections_ad AFTER DELETE ON corrections BEGIN
    DELETE FROM corrections_fts WHERE rowid=old.rowid;
END;
CREATE TRIGGER corrections_au AFTER UPDATE ON corrections BEGIN
    DELETE FROM corrections_fts WHERE rowid=old.rowid;
    INSERT INTO corrections_fts(rowid,task,note) SELECT new.rowid,new.task,new.note
        WHERE EXISTS(SELECT 1 FROM evidence r JOIN projects p ON p.id=r.project_id WHERE r.id=new.record_id AND p.is_approved=1);
END;
UPDATE workspace_meta SET evidence_version=evidence_version+1 WHERE id=1;

-- Stores pointers only. Project revocation and retention remove these with evidence.
CREATE TABLE alexa_push_events (
    sequence INTEGER PRIMARY KEY AUTOINCREMENT,
    record_id TEXT NOT NULL REFERENCES evidence(id) ON DELETE CASCADE
);
CREATE INDEX idx_alexa_push_record ON alexa_push_events(record_id);

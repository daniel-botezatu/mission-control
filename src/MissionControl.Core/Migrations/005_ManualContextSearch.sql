CREATE VIRTUAL TABLE corrections_fts USING fts5(
    task, note, content='corrections', content_rowid='rowid',
    tokenize='unicode61 remove_diacritics 2'
);
INSERT INTO corrections_fts(corrections_fts,rank) VALUES('secure-delete',1);
INSERT INTO corrections_fts(corrections_fts) VALUES('rebuild');
CREATE TRIGGER corrections_ai AFTER INSERT ON corrections BEGIN
    INSERT INTO corrections_fts(rowid,task,note) VALUES(new.rowid,new.task,new.note);
END;
CREATE TRIGGER corrections_ad AFTER DELETE ON corrections BEGIN
    INSERT INTO corrections_fts(corrections_fts,rowid,task,note)
    VALUES('delete',old.rowid,old.task,old.note);
END;
CREATE TRIGGER corrections_au AFTER UPDATE ON corrections BEGIN
    INSERT INTO corrections_fts(corrections_fts,rowid,task,note)
    VALUES('delete',old.rowid,old.task,old.note);
    INSERT INTO corrections_fts(rowid,task,note) VALUES(new.rowid,new.task,new.note);
END;

CREATE VIRTUAL TABLE commitment_corrections_fts USING fts5(
    title, task, note, content='commitment_corrections', content_rowid='rowid',
    tokenize='unicode61 remove_diacritics 2'
);
INSERT INTO commitment_corrections_fts(commitment_corrections_fts,rank) VALUES('secure-delete',1);
INSERT INTO commitment_corrections_fts(commitment_corrections_fts) VALUES('rebuild');
CREATE TRIGGER commitment_corrections_ai AFTER INSERT ON commitment_corrections BEGIN
    INSERT INTO commitment_corrections_fts(rowid,title,task,note)
    VALUES(new.rowid,new.title,new.task,new.note);
END;
CREATE TRIGGER commitment_corrections_ad AFTER DELETE ON commitment_corrections BEGIN
    INSERT INTO commitment_corrections_fts(commitment_corrections_fts,rowid,title,task,note)
    VALUES('delete',old.rowid,old.title,old.task,old.note);
END;
CREATE TRIGGER commitment_corrections_au AFTER UPDATE ON commitment_corrections BEGIN
    INSERT INTO commitment_corrections_fts(commitment_corrections_fts,rowid,title,task,note)
    VALUES('delete',old.rowid,old.title,old.task,old.note);
    INSERT INTO commitment_corrections_fts(rowid,title,task,note)
    VALUES(new.rowid,new.title,new.task,new.note);
END;

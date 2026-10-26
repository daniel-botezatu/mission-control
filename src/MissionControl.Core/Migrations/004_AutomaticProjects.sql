ALTER TABLE projects ADD COLUMN origin TEXT NOT NULL DEFAULT 'manual';
CREATE TABLE project_workspaces (
    root_path TEXT PRIMARY KEY,
    project_id TEXT NOT NULL,
    FOREIGN KEY(project_id) REFERENCES projects(id) ON DELETE CASCADE
);
CREATE INDEX project_workspaces_project ON project_workspaces(project_id);
CREATE TABLE project_repositories (
    local_root TEXT PRIMARY KEY,
    project_id TEXT NOT NULL,
    FOREIGN KEY(project_id) REFERENCES projects(id) ON DELETE CASCADE
);
CREATE INDEX project_repositories_project ON project_repositories(project_id);
CREATE TABLE project_repository_remotes (
    local_root TEXT NOT NULL,
    github_repository TEXT NOT NULL COLLATE NOCASE,
    PRIMARY KEY(local_root,github_repository),
    FOREIGN KEY(local_root) REFERENCES project_repositories(local_root) ON DELETE CASCADE
);
CREATE INDEX project_repository_remotes_identity ON project_repository_remotes(github_repository,local_root);
ALTER TABLE remote_selections ADD COLUMN project_assignment TEXT NOT NULL DEFAULT 'auto';
UPDATE remote_selections SET project_assignment='manual' WHERE project_id IS NOT NULL;

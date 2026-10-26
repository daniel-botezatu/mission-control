import { X } from "lucide-react";
import type { ProjectInfo, SourceConfig } from "./bridge";

type Save = (
  label: string,
  method: string,
  args?: unknown,
  message?: string,
) => Promise<unknown>;

export function SourceDialog({
  source,
  busy,
  onChange,
  onClose,
  act,
}: {
  source: SourceConfig;
  busy: string;
  onChange: (source: SourceConfig) => void;
  onClose: () => void;
  act: Save;
}) {
  return (
    <div className="modal-backdrop" onClick={onClose}>
      <section
        className="form-modal"
        role="dialog"
        aria-modal="true"
        aria-labelledby="source-dialog-title"
        onClick={(e) => e.stopPropagation()}
      >
        <div className="drawer-header">
          <h2 id="source-dialog-title">Configure local source</h2>
          <button
            className="icon-button"
            onClick={onClose}
            aria-label="Close source editor"
          >
            <X size={18} />
          </button>
        </div>
        <form
          onSubmit={async (e) => {
            e.preventDefault();
            const result = await act(
              "source",
              "saveSource",
              { source },
              "Source configuration saved.",
            );
            if (result !== undefined) {
              onClose();
            }
          }}
        >
          <label>
            Name
            <input
              autoFocus
              required
              maxLength={120}
              value={source.name}
              onChange={(e) => onChange({ ...source, name: e.target.value })}
              placeholder="My Codex sessions"
            />
          </label>
          <div className="form-grid">
            <label>
              Source format
              <select
                value={source.kind}
                onChange={(e) => onChange({ ...source, kind: e.target.value })}
              >
                <option value="codex">Codex</option>
                <option value="claude">Claude Code</option>
              </select>
            </label>
            <label>
              Environment
              <select
                value={source.environment}
                onChange={(e) =>
                  onChange({
                    ...source,
                    environment: e.target.value,
                  })
                }
              >
                <option value="windows">Windows</option>
                <option value="macos">macOS</option>
                <option value="wsl" disabled>
                  WSL · unsupported in this release
                </option>
              </select>
            </label>
          </div>
          <label>
            Session directory
            <input
              required
              value={source.rootPath}
              onChange={(e) =>
                onChange({ ...source, rootPath: e.target.value })
              }
              placeholder={
                source.environment === "macos"
                  ? "/Users/you/.codex/sessions"
                  : "C:\\Users\\you\\.codex\\sessions"
              }
            />
          </label>
          <p className="field-hint">
            Use the verified source directory. Mission Control will not scan
            your drives or edit source files.
          </p>
          <label className="checkbox-label">
            <input
              type="checkbox"
              checked={source.enabled}
              onChange={(e) =>
                onChange({ ...source, enabled: e.target.checked })
              }
            />
            Enable read-only collection from this directory
          </label>
          <div className="form-actions">
            <button type="submit" className="button primary" disabled={!!busy}>
              Save source
            </button>
            <button
              type="button"
              className="button secondary"
              onClick={onClose}
            >
              Cancel
            </button>
          </div>
        </form>
      </section>
    </div>
  );
}

export function ProjectDialog({
  project,
  busy,
  onChange,
  onClose,
  act,
}: {
  project: ProjectInfo;
  busy: string;
  onChange: (project: ProjectInfo) => void;
  onClose: () => void;
  act: Save;
}) {
  return (
    <div className="modal-backdrop" onClick={onClose}>
      <section
        className="form-modal"
        role="dialog"
        aria-modal="true"
        aria-labelledby="project-dialog-title"
        onClick={(e) => e.stopPropagation()}
      >
        <div className="drawer-header">
          <h2 id="project-dialog-title">Project root</h2>
          <button
            className="icon-button"
            aria-label="Close project editor"
            onClick={onClose}
          >
            <X size={18} />
          </button>
        </div>
        <form
          onSubmit={async (e) => {
            e.preventDefault();
            const result = await act(
              "project-save",
              "saveProject",
              { project: { ...project, autoDiscovered: false } },
              "Project saved. Approve it in Settings to allow indexing.",
            );
            if (result !== undefined) onClose();
          }}
        >
          <label>
            Name
            <input
              autoFocus
              required
              maxLength={100}
              value={project.name}
              onChange={(e) => onChange({ ...project, name: e.target.value })}
            />
          </label>
          <label>
            Root directory
            <input
              required
              value={project.rootPath || ""}
              onChange={(e) =>
                onChange({
                  ...project,
                  rootPath: e.target.value,
                })
              }
              placeholder="C:\Projects\my-project"
            />
          </label>
          <label>
            Short description
            <input
              maxLength={250}
              value={project.description}
              onChange={(e) =>
                onChange({
                  ...project,
                  description: e.target.value,
                })
              }
            />
          </label>
          <label>
            Project color
            <input
              type="color"
              value={project.color}
              onChange={(e) => onChange({ ...project, color: e.target.value })}
            />
          </label>
          <p className="field-hint">
            This directory groups its sessions and repositories. Saving does not
            approve collection. Approve the project separately in Settings after
            reviewing its directory.
          </p>
          <div className="form-actions">
            <button className="button primary" disabled={!!busy}>
              Save project
            </button>
            <button
              type="button"
              className="button secondary"
              onClick={onClose}
            >
              Cancel
            </button>
          </div>
        </form>
      </section>
    </div>
  );
}

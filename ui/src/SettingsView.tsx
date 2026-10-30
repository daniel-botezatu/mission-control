import { useEffect, useState } from "react";
import {
  Copy,
  Pause,
  Play,
  Plus,
  RefreshCw,
  ShieldCheck,
  Square,
  Trash2,
  X,
} from "lucide-react";
import CollectionSettings from "./CollectionSettings";
import LiveIndexStats from "./LiveIndexStats";
import ProjectRepositories, { ProjectOrigin } from "./ProjectRepositories";
import {
  rpc,
  type AppSnapshot,
  type SourceConfig,
  type ProjectInfo,
  type Settings,
} from "./bridge";
import {
  Badge,
  Empty,
  ProjectDot,
  SourceHealth,
  SourceIcon,
  dateTime,
  errorText,
  titleCase,
} from "./presentation";
type Action = (
  label: string,
  method: string,
  args?: unknown,
  message?: string,
) => Promise<unknown>;
type McpConfig = {
  executablePath: string;
  dataDirectory: string;
  claudeCommand: string;
  codexCommand: string;
  configurationPath?: string;
};
export default function SettingsView({
  snapshot,
  paused,
  busy,
  settingsTab,
  setSettingsTab,
  setError,
  setToast,
  setNewSource,
  setProjectDraft,
  queryVersion,
  refresh,
  act,
}: {
  snapshot: AppSnapshot;
  paused: boolean;
  busy: string;
  settingsTab: string;
  setSettingsTab: (tab: string) => void;
  setError: (value: string) => void;
  setToast: (value: string) => void;
  setNewSource: (source: SourceConfig) => void;
  setProjectDraft: (project: ProjectInfo) => void;
  queryVersion: number;
  refresh: () => Promise<AppSnapshot>;
  act: Action;
}) {
  const [sources, setSources] = useState<SourceConfig[]>([]);
  const [projects, setProjects] = useState<ProjectInfo[]>([]);
  const [settings, setSettings] = useState<Settings | null>(null);
  const [config, setConfig] = useState<McpConfig | null>(null);
  const [loadError, setLoadError] = useState("");
  const [attempt, setAttempt] = useState(0);
  const [exclude, setExclude] = useState<ProjectInfo | null>(null);
  const [approving, setApproving] = useState("");
  useEffect(() => {
    let active = true;
    Promise.all([
      rpc<SourceConfig[]>("sources"),
      rpc<ProjectInfo[]>("projects"),
      rpc<Settings>("settings"),
    ])
      .then(([s, p, v]) => {
        if (active) {
          setSources(s);
          setProjects(p);
          setSettings(v);
          setLoadError("");
        }
      })
      .catch((e) => {
        if (active) setLoadError(errorText(e));
      });
    return () => {
      active = false;
    };
  }, [queryVersion, attempt]);
  useEffect(() => {
    if (settingsTab !== "MCP integration") return;
    let active = true;
    rpc<McpConfig>("mcpConfig")
      .then((v) => {
        if (active) setConfig(v);
      })
      .catch((e) => {
        if (active) setError(errorText(e));
      });
    return () => {
      active = false;
    };
  }, [settingsTab, setError]);
  useEffect(() => {
    if (!exclude || approving) return;
    const cancel = (event: KeyboardEvent) => {
      if (event.key === "Escape") setExclude(null);
    };
    window.addEventListener("keydown", cancel);
    return () => window.removeEventListener("keydown", cancel);
  }, [exclude, approving]);
  const approval = async (project: ProjectInfo, approved: boolean) => {
    setApproving(project.id);
    try {
      const result = await rpc<{ purged?: number }>("projects.approve", {
        projectId: project.id,
        approved,
      });
      await refresh();
      setProjects(await rpc<ProjectInfo[]>("projects"));
      setToast(
        approved
          ? `${project.name} approved. Available local history can now be indexed.`
          : `${project.name} excluded. Retained history and search entries were purged${result?.purged !== undefined ? ` (${result.purged} records)` : ""}.`,
      );
      setExclude(null);
    } catch (e) {
      setError(errorText(e));
    } finally {
      setApproving("");
    }
  };
  const copy = async (value: string) => {
    try {
      await navigator.clipboard.writeText(value);
      setToast("Copied. Review and run this command in your host terminal.");
    } catch {
      setError(
        "Clipboard access is unavailable. Select and copy the command shown below.",
      );
    }
  };
  if (loadError && !settings)
    return (
      <div className="notice error" role="alert">
        <span>{loadError}</span>
        <button onClick={() => setAttempt((v) => v + 1)}>Retry settings</button>
      </div>
    );
  if (!settings) return <p role="status">Reading saved settings...</p>;
  return (
    <>
      <div className="page-heading">
        <div>
          <div className="eyebrow">PRIVATE BY APPROVAL</div>
          <h1>A quiet, private memory.</h1>
          <p>
            Approve project directories, monitor local history, and connect your
            AI host through MCP.
          </p>
        </div>
      </div>
      <div
        className="settings-tabs"
        role="tablist"
        aria-label="Settings categories"
      >
        {[
          "Approved projects",
          "Monitoring",
          "Privacy & storage",
          "MCP integration",
        ].map((t) => (
          <button
            key={t}
            role="tab"
            aria-selected={settingsTab === t}
            className={settingsTab === t ? "active" : ""}
            onClick={() => setSettingsTab(t)}
          >
            {t}
          </button>
        ))}
      </div>
      {loadError && (
        <div className="notice error" role="alert">
          {loadError}
        </div>
      )}
      {settingsTab === "Approved projects" && (
        <div className="settings-content">
          <div className="section-header">
            <div>
              <h2>Approved projects</h2>
              <p>
                Only explicitly approved project directories can be indexed or
                exposed through MCP. Discovered candidates stay excluded until
                approved.
              </p>
            </div>
            <div className="form-actions">
              <button
                className="button secondary"
                disabled={!!busy}
                onClick={() =>
                  act(
                    "discover",
                    "projects.discover",
                    {},
                    "Project metadata discovery finished. Review candidates before approving.",
                  )
                }
              >
                <RefreshCw size={14} />
                Discover projects
              </button>
              <button
                className="button secondary"
                onClick={() =>
                  setProjectDraft({
                    id: `project-${Date.now()}`,
                    name: "",
                    description: "",
                    color: "#648674",
                    rootPath: "",
                    isApproved: false,
                    autoDiscovered: false,
                  })
                }
              >
                <Plus size={14} />
                Add project
              </button>
            </div>
          </div>
          <div className="notice info">
            <ShieldCheck size={16} />
            <span>
              A workspace such as SP can group several Git repositories into one
              project. Approving its root covers conversations observed inside
              that workspace. Unknown projects and unassigned conversations
              remain excluded.
            </span>
          </div>
          <div className="panel project-roots">
            {projects.map((p) => (
              <div className="project-approval-row" key={p.id}>
                <div className="project-approval-main">
                  <div className="project-name-line">
                    <ProjectDot project={p} />
                    <strong>{p.name}</strong>
                    <ProjectOrigin project={p} />
                    <Badge tone={p.isApproved ? "green" : "neutral"}>
                      {p.isApproved ? "Approved" : "Excluded"}
                    </Badge>
                  </div>
                  <code>{p.rootPath || "No directory set"}</code>
                  <ProjectRepositories project={p} />
                  {p.approvedAt && p.isApproved && (
                    <small>Approved {dateTime(p.approvedAt)}</small>
                  )}
                </div>
                <div className="form-actions">
                  <button
                    className="button secondary small"
                    onClick={() => setProjectDraft(p)}
                  >
                    Edit
                  </button>
                  {p.isApproved ? (
                    <button
                      className="button danger-button small"
                      disabled={!!approving || !!busy}
                      onClick={() => setExclude(p)}
                    >
                      <Trash2 size={13} />
                      Exclude & purge
                    </button>
                  ) : (
                    <button
                      className="button primary small"
                      disabled={!p.rootPath || !!approving || !!busy}
                      onClick={() => approval(p, true)}
                    >
                      <ShieldCheck size={13} />
                      {approving === p.id ? "Approving..." : "Approve project"}
                    </button>
                  )}
                </div>
              </div>
            ))}
            {!projects.length && (
              <Empty title="No project candidates yet">
                Enable a local session source to discover observed workspace
                directories, or add a project root yourself.
              </Empty>
            )}
          </div>
        </div>
      )}
      {settingsTab === "Monitoring" && (
        <div className="settings-content">
          <CollectionSettings
            settings={settings}
            onSaved={(v) => {
              setSettings(v);
              void refresh();
            }}
          />
          <div className="section-header">
            <div>
              <h2>Local session sources</h2>
              <p>
                Read existing Claude Code and Codex history. Enabling a source
                does not approve the projects found inside it.
              </p>
            </div>
            <button
              className="button secondary"
              onClick={() =>
                setNewSource({
                  id: `local-${Date.now()}`,
                  kind: "codex",
                  name: "",
                  rootPath: "",
                  enabled: false,
                  environment: /Mac/i.test(navigator.platform)
                    ? "macos"
                    : "windows",
                })
              }
            >
              <Plus size={14} />
              Add source
            </button>
          </div>
          {sources.map((s) => (
            <div className="source-config panel" key={s.id}>
              <div className="source-config-heading">
                <span className={`source-logo source-${s.kind}`}>
                  <SourceIcon kind={s.kind} />
                </span>
                <div>
                  <strong>{s.name}</strong>
                  <code>{s.rootPath}</code>
                </div>
                <Badge>{s.enabled ? "Enabled" : "Disabled"}</Badge>
              </div>
              <SourceHealth
                source={
                  snapshot.sources.find((v) => v.id === s.id) || {
                    ...s,
                    state: "unknown",
                    lastSynced: null,
                    pending: 0,
                    gap: null,
                  }
                }
              />
              <div className="form-actions">
                <button
                  className="button secondary small"
                  onClick={() => setNewSource(s)}
                >
                  Edit source
                </button>
                <button
                  className="button secondary small"
                  disabled={!!busy}
                  onClick={() =>
                    act(
                      "source",
                      "saveSource",
                      { source: { ...s, enabled: !s.enabled } },
                      s.enabled
                        ? "Source disabled."
                        : "Source enabled. Only approved projects will be indexed.",
                    )
                  }
                >
                  {s.enabled ? "Disable" : "Enable"}
                </button>
              </div>
            </div>
          ))}
          <section className="panel settings-section">
            <h2>Collection lifecycle</h2>
            <p>
              Pause stops monitoring and queued indexing. Resume recovers
              available missed history. Closing the dashboard keeps monitoring
              in the tray; Quit exits it.
            </p>
            <div className="form-actions">
              <button
                className="button secondary"
                disabled={!!busy}
                onClick={() =>
                  act("monitoring", "control", {
                    action: paused ? "resume" : "pause",
                  })
                }
              >
                {paused ? <Play size={14} /> : <Pause size={14} />}{" "}
                {paused ? "Resume collection" : "Pause all"}
              </button>
              <button
                className="button secondary"
                onClick={() => act("hide", "control", { action: "hide" })}
              >
                Close dashboard to tray
              </button>
              <button
                className="button danger-button"
                onClick={() => act("quit", "control", { action: "quit" })}
              >
                <Square size={12} />
                Quit completely
              </button>
            </div>
          </section>
        </div>
      )}
      {settingsTab === "Privacy & storage" && (
        <div className="settings-content">
          <LiveIndexStats snapshot={snapshot} />
          <section className="panel settings-section">
            <h2>Local memory, strict project approval</h2>
            <p>
              Retained conversations, search indexes, source references, and
              your notes stay in the local database. MCP exposes only approved
              project history. Excluding a project purges its retained records,
              search entries, and associated corrections.
            </p>
            <div className="privacy-feature">
              <ShieldCheck size={20} />
              <div>
                <strong>Read-only source access</strong>
                <p>
                  No repository changes, task execution, Slack messages, or
                  GitHub writes. Mission Control contains no model or inference
                  service.
                </p>
              </div>
              <Badge tone="green">Always on</Badge>
            </div>
            <div className="privacy-feature">
              <ShieldCheck size={20} />
              <div>
                <strong>Remove excluded historical data</strong>
                <p>
                  Purge retained history that belongs to projects outside the
                  approved list, including matching search entries. Original
                  session files remain unchanged.
                </p>
              </div>
              <button
                className="button secondary small"
                disabled={!!busy}
                onClick={() =>
                  act(
                    "purge-excluded",
                    "projects.purgeExcluded",
                    {},
                    "Excluded project history purged.",
                  )
                }
              >
                Purge excluded history
              </button>
            </div>
            <label className="checkbox-label startup-option">
              <input
                type="checkbox"
                checked={settings.startWithWindows}
                onChange={async (e) => {
                  const checked = e.target.checked;
                  try {
                    await rpc("saveSettings", { startWithWindows: checked });
                    setSettings({ ...settings, startWithWindows: checked });
                    setToast(
                      checked
                        ? "Start at sign-in enabled."
                        : "Start at sign-in disabled.",
                    );
                  } catch (err) {
                    setError(errorText(err));
                  }
                }}
              />
              Start Mission Control at sign-in
            </label>
            <form
              onSubmit={async (e) => {
                e.preventDefault();
                await act(
                  "retention",
                  "retention",
                  { days: settings.retentionDays },
                  "Retention applied to stored conversation history and search data.",
                );
              }}
            >
              <label>
                Retain conversation history for
                <select
                  value={settings.retentionDays}
                  onChange={(e) =>
                    setSettings({
                      ...settings,
                      retentionDays: Number(e.target.value),
                    })
                  }
                >
                  {[30, 90, 180, 365, 3650].map((n) => (
                    <option key={n} value={n}>
                      {n === 3650
                        ? "10 years"
                        : n === 365
                          ? "1 year"
                          : `${n} days`}
                    </option>
                  ))}
                </select>
              </label>
              <p className="field-hint">
                Messages older than the saved period are removed automatically
                once a day, judged by when each message was written. Apply saves
                the period and removes them now. Annotated messages are kept, and
                original Claude Code and Codex session files remain unchanged.
              </p>
              <button className="button secondary" disabled={!!busy}>
                Apply retention
              </button>
            </form>
          </section>
          <section className="panel settings-section">
            <h2>Storage & freshness</h2>
            <dl className="diagnostics-list">
              <div>
                <dt>Collector</dt>
                <dd>{titleCase(snapshot.monitoring)}</dd>
              </div>
              <div>
                <dt>Storage engine</dt>
                <dd>SQLite {snapshot.sqliteVersion}</dd>
              </div>
              <div>
                <dt>Telemetry</dt>
                <dd>Off</dd>
              </div>
            </dl>
            {snapshot.sources.map((s) => (
              <SourceHealth source={s} key={s.id} />
            ))}
          </section>
        </div>
      )}
      {settingsTab === "MCP integration" && (
        <div className="settings-content">
          <section className="panel settings-section">
            <h2>Use memory in Claude Code or Codex</h2>
            <p>
              The standalone Mission Control MCP server runs on demand and reads
              approved local history. Claude Code and Codex provide reasoning
              through their own subscriptions. The dashboard remains useful when
              neither host is running.
            </p>
            {!config ? (
              <p role="status">Reading local MCP configuration...</p>
            ) : (
              <>
                <label>
                  Mission Control MCP executable
                  <input
                    readOnly
                    value={config.executablePath}
                    onFocus={(e) => e.target.select()}
                  />
                </label>
                <label>
                  Local memory directory
                  <input
                    readOnly
                    value={config.dataDirectory}
                    onFocus={(e) => e.target.select()}
                  />
                </label>
                {[
                  ["Claude Code", config.claudeCommand],
                  ["Codex", config.codexCommand],
                ].map(([name, command]) => (
                  <div className="mcp-command" key={name}>
                    <h3>{name}</h3>
                    <p className="field-hint">
                      Review this command, then run it in your terminal.
                      Registration is explicit and does not replace existing
                      host configuration.
                    </p>
                    <pre tabIndex={0}>{command}</pre>
                    <button
                      className="button secondary small"
                      onClick={() => copy(command)}
                    >
                      <Copy size={13} />
                      Copy {name} command
                    </button>
                  </div>
                ))}
              </>
            )}
          </section>
          <section className="panel settings-section">
            <h2>GitHub and Slack belong to your AI host</h2>
            <p>
              Install the official GitHub MCP and official Slack MCP using the
              host-specific integration package included with Mission Control.
              These connections run in Claude Code or Codex, independently of
              this desktop. The desktop does not connect to GitHub or request or
              store provider tokens.
            </p>
            <p>
              Use the included read-only memory workflow skills to retrieve
              local conversation evidence and correlate it with the official
              servers. Review each host's external tool permissions before
              enabling them.
            </p>
            <p className="field-hint">
              See the integration README bundled with this application for
              installation, updates, removal, verified compatibility, and
              external service limitations.
            </p>
          </section>
        </div>
      )}
      {exclude && (
        <div
          className="modal-backdrop"
          onClick={() => !approving && setExclude(null)}
        >
          <section
            className="form-modal"
            role="dialog"
            aria-modal="true"
            aria-labelledby="exclude-title"
            onClick={(e) => e.stopPropagation()}
          >
            <div className="drawer-header">
              <h2 id="exclude-title">
                Exclude {exclude.name} and purge history?
              </h2>
              <button
                className="icon-button"
                aria-label="Cancel exclusion"
                disabled={!!approving}
                onClick={() => setExclude(null)}
              >
                <X size={18} />
              </button>
            </div>
            <div className="approval-confirmation">
              <p>
                Mission Control will stop indexing this project and remove its
                retained conversation records, search entries, and associated
                notes from the local memory database.
              </p>
              <code>{exclude.rootPath}</code>
              <p>
                Original conversation files and repositories remain unchanged.
                You can approve this project again to re-index available
                history.
              </p>
              <div className="form-actions">
                <button
                  autoFocus
                  className="button secondary"
                  disabled={!!approving}
                  onClick={() => setExclude(null)}
                >
                  Cancel
                </button>
                <button
                  className="button danger-button"
                  disabled={!!approving}
                  onClick={() => approval(exclude, false)}
                >
                  {approving ? "Purging..." : "Exclude & purge history"}
                </button>
              </div>
            </div>
          </section>
        </div>
      )}
    </>
  );
}

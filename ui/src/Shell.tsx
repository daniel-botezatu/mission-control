import { useState, type RefObject } from "react";
import {
  Activity,
  Bell,
  Check,
  ChevronDown,
  ChevronRight,
  Clock3,
  Database,
  FolderKanban,
  Layers3,
  LayoutDashboard,
  LoaderCircle,
  MessageSquare,
  Moon,
  Pause,
  Play,
  Search,
  Settings2,
  ShieldCheck,
  Sun,
  X,
} from "lucide-react";
import type { AppSnapshot } from "./bridge";
import { ProjectDot } from "./presentation";

export type View =
  | "Overview"
  | "Projects"
  | "Sessions"
  | "Search"
  | "Timeline"
  | "Settings";
type IconType = typeof Activity;
const nav: { name: View; icon: IconType }[] = [
  { name: "Overview", icon: LayoutDashboard },
  { name: "Projects", icon: FolderKanban },
  { name: "Sessions", icon: MessageSquare },
  { name: "Search", icon: Search },
  { name: "Timeline", icon: Clock3 },
];

export function Sidebar({
  snapshot,
  version,
  view,
  selectedProject,
  paused,
  catchingUp,
  busy,
  go,
  onSelectProject,
  onToggleMonitoring,
}: {
  snapshot: AppSnapshot | null;
  version: string;
  view: View;
  selectedProject: string;
  paused: boolean;
  catchingUp: boolean;
  busy: string;
  go: (next: View) => void;
  onSelectProject: (id: string) => void;
  onToggleMonitoring: () => void;
}) {
  return (
    <aside className="sidebar" aria-label="Main navigation">
      <button
        className="brand"
        onClick={() => go("Overview")}
        aria-label="Mission Control overview"
      >
        <span className="brand-icon">
          <Layers3 size={21} strokeWidth={1.7} />
        </span>
        <span>
          mission control
          <span className="brand-sub">YOUR WORK, IN CONTEXT</span>
        </span>
      </button>
      <div className="workspace-label">
        PERSONAL WORKSPACE
        <ChevronDown size={12} />
      </div>
      <nav>
        {nav.map(({ name, icon: Icon }) => (
          <button
            key={name}
            className={`nav-item ${view === name ? "selected" : ""}`}
            onClick={() => go(name)}
            aria-current={view === name ? "page" : undefined}
            aria-label={name}
            title={name}
          >
            <Icon size={18} />
            <span>{name}</span>
          </button>
        ))}
      </nav>
      <div className="sidebar-divider" />
      <div className="workspace-label project-label">
        PROJECTS<span>{snapshot?.projects.length ?? "—"}</span>
      </div>
      <div className="project-nav">
        {snapshot?.projects.map((p) => (
          <button
            key={p.id}
            className={`project-link ${selectedProject === p.id ? "active" : ""}`}
            onClick={() => onSelectProject(p.id)}
          >
            <ProjectDot project={p} />
            <span>{p.name}</span>
          </button>
        ))}
      </div>
      <div className="sidebar-bottom">
        <div className="collector-status">
          <span className={`health-dot ${paused ? "warning" : "good"}`} />
          <span>
            {!snapshot
              ? "Collector unavailable"
              : paused
                ? "Collection paused"
                : catchingUp
                  ? "Catching up"
                  : "Collector running"}
          </span>
          <button
            title={paused ? "Resume collection" : "Pause all collection"}
            aria-label={paused ? "Resume collection" : "Pause all collection"}
            disabled={!!busy || !snapshot}
            onClick={onToggleMonitoring}
          >
            {busy === "monitoring" ? (
              <LoaderCircle size={14} className="spin" />
            ) : paused ? (
              <Play size={14} />
            ) : (
              <Pause size={14} />
            )}
          </button>
        </div>
        <button
          className={`nav-item settings-nav ${view === "Settings" ? "selected" : ""}`}
          aria-label="Settings"
          title="Settings"
          onClick={() => go("Settings")}
        >
          <Settings2 size={18} />
          <span>Settings</span>
        </button>
        <div className="profile">
          <span className="profile-avatar">M</span>
          <div>
            <strong>My workspace</strong>
            <span
              title="Version of the running desktop application"
              data-sidebar-version={version}
            >
              {version
                ? `v${version} · Local memory`
                : "Local-first · Private by default"}
            </span>
          </div>
          <ShieldCheck size={16} />
        </div>
      </div>
    </aside>
  );
}

export function Topbar({
  view,
  search,
  searchRef,
  onSearch,
  theme,
  onToggleTheme,
}: {
  view: View;
  search: string;
  searchRef: RefObject<HTMLInputElement | null>;
  onSearch: (value: string) => void;
  theme: string;
  onToggleTheme: () => void;
}) {
  return (
    <header className="topbar">
      <div className="breadcrumb">
        <span>Workspace</span>
        <ChevronRight size={13} />
        <strong>{view}</strong>
      </div>
      <div className="topbar-actions">
        <label className="global-search">
          <Search size={15} />
          <input
            ref={searchRef}
            aria-label="Search approved conversation history"
            placeholder="Search conversation history"
            value={search}
            onChange={(e) => onSearch(e.target.value)}
          />
          <kbd>Ctrl K</kbd>
        </label>
        <button
          className="icon-button theme-toggle"
          title={`Use ${theme === "light" ? "dark" : "light"} theme`}
          aria-label={`Use ${theme === "light" ? "dark" : "light"} theme`}
          onClick={onToggleTheme}
        >
          {theme === "light" ? <Moon size={17} /> : <Sun size={17} />}
        </button>
      </div>
    </header>
  );
}

/** Collection-state notices shown above every view. */
export function WorkspaceNotices({
  snapshot,
  paused,
  busy,
  act,
  onOpenSettings,
  onReviewSources,
}: {
  snapshot: AppSnapshot;
  paused: boolean;
  busy: string;
  act: (label: string, method: string, args?: unknown) => Promise<unknown>;
  onOpenSettings: () => void;
  onReviewSources: () => void;
}) {
  const [dismissedGaps, setDismissedGaps] = useState<string[]>(() => {
    try {
      const saved: unknown = JSON.parse(localStorage.getItem("mission-control-source-notices") || "[]");
      return Array.isArray(saved) ? saved.filter((v): v is string => typeof v === "string").slice(-128) : [];
    } catch { return []; }
  });
  const gapKey = (source: AppSnapshot["sources"][number]) => `live:${source.id}:${source.gap}`;
  const gapSources = snapshot.sources.filter(s => s.enabled && s.gap && !dismissedGaps.includes(gapKey(s)));
  const dismissGaps = () => {
    const next = [...new Set([...dismissedGaps, ...gapSources.map(gapKey)])].slice(-128);
    setDismissedGaps(next);
    try { localStorage.setItem("mission-control-source-notices", JSON.stringify(next)); } catch { /* This visit still acknowledges the notice. */ }
  };
  return (
    <>
      {snapshot.isPartial && (
        <div className="notice info" role="status">
          <span>
            The overview shows a limited set of summaries. Search Timeline or
            Sessions to browse retained evidence, and open Settings for source
            and project details.
          </span>
        </div>
      )}
      {snapshot.monitoring === "storage-full" && (
        <div className="notice error">
          <Database size={16} />
          <span>
            Collection is suspended at the storage budget. Increase it or remove
            old data in Settings, then resume.
          </span>
          <button onClick={onOpenSettings}>Open Settings</button>
          <button
            onClick={() => act("monitoring", "control", { action: "resume" })}
          >
            Resume
          </button>
        </div>
      )}
      {paused && (
        <div className="notice pause-notice">
          <Pause size={16} />
          <span>
            <strong>Monitoring is paused.</strong> You're viewing cached
            evidence. Resume to catch up on available history.
          </span>
          <button
            disabled={!!busy}
            onClick={() => act("monitoring", "control", { action: "resume" })}
          >
            Resume collection
          </button>
        </div>
      )}
      {gapSources.length > 0 && (
        <div className="notice warning">
          <Bell size={16} />
          <span>
            <strong>{gapSources.map(s => s.name).join(", ")}: </strong>
            {gapSources.every(s => s.gap?.includes("Referenced Codex history"))
              ? "Some referenced messages are unavailable. Retained messages remain searchable."
              : "This source reports unavailable history. Review its details for the affected source."}
          </span>
          <button onClick={onReviewSources}>Review sources</button>
          <button onClick={dismissGaps} aria-label="Dismiss source notice">Dismiss</button>
        </div>
      )}
    </>
  );
}

export function Toast({
  message,
  onDismiss,
}: {
  message: string;
  onDismiss: () => void;
}) {
  return (
    <div className="toast" role="status">
      <span className="toast-check">
        <Check size={15} />
      </span>
      {message}
      <button aria-label="Dismiss notification" onClick={onDismiss}>
        <X size={14} />
      </button>
    </div>
  );
}

import {
  ArrowRight,
  FolderKanban,
  RefreshCw,
  Settings2,
  ShieldCheck,
} from "lucide-react";
import type { AppSnapshot, EvidenceRecord } from "./bridge";
import { RecordRow, SectionHeader, type EvidenceLookup } from "./EvidenceRows";
import { Empty, SourceHealth } from "./presentation";
import OpenLoopsPanel from "./OpenLoopsPanel";
import LiveIndexStats from "./LiveIndexStats";

export default function OverviewView({
  snapshot,
  visible,
  paused,
  busy,
  lookup,
  act,
  onOpenView,
  onSelectProject,
  onConfigureSources,
}: {
  snapshot: AppSnapshot;
  visible: EvidenceRecord[];
  paused: boolean;
  busy: string;
  lookup: EvidenceLookup;
  act: (
    label: string,
    method: string,
    args?: unknown,
    message?: string,
  ) => Promise<unknown>;
  onOpenView: (view: "Timeline" | "Projects" | "Sessions") => void;
  onSelectProject: (id: string) => void;
  onConfigureSources: () => void;
}) {
  return (
    <>
      <div className="page-heading overview-heading">
        <div>
          <div className="eyebrow">LOCAL DEVELOPER MEMORY</div>
          <h1>Pick up where you left off.</h1>
          <p>
            Your approved projects and retained Claude Code / Codex
            conversations.
          </p>
        </div>
        <button
          className="button secondary small"
          disabled={!!busy || paused}
          onClick={() =>
            act(
              "refresh",
              "control",
              { action: "refresh" },
              "History refresh queued. Progress appears under Collection freshness.",
            )
          }
        >
          <RefreshCw size={14} className={busy === "refresh" ? "spin" : ""} />
          Refresh
        </button>
      </div>
      <LiveIndexStats snapshot={snapshot} />
      <div className="dashboard-grid">
        <div className="dashboard-main">
          <OpenLoopsPanel mode={snapshot.mode} lookup={lookup} />
          <section className="panel recent-panel">
            <SectionHeader
              label="Recent conversation activity"
              action={
                <button
                  className="text-button"
                  onClick={() => onOpenView("Timeline")}
                >
                  Open timeline <ArrowRight size={14} />
                </button>
              }
            />
            <div className="section-description">
              Original user messages and final responses, with source
              references.
            </div>
            {visible.length ? (
              visible
                .slice(0, 8)
                .map((r) => (
                  <RecordRow record={r} key={r.id} lookup={lookup} compact />
                ))
            ) : (
              <Empty
                title="Choose what Mission Control remembers"
                icon={FolderKanban}
              >
                Approve a project in Settings and enable its local session
                source. Unknown projects stay excluded.
                <button
                  className="button secondary small"
                  onClick={onConfigureSources}
                >
                  Review approved projects
                </button>
              </Empty>
            )}
            <button
              className="panel-footer"
              onClick={() => onOpenView("Sessions")}
            >
              Browse sessions <ArrowRight size={14} />
            </button>
          </section>
          <div className="privacy-note memory-host-note">
            <ShieldCheck size={15} />
            <span>
              Mission Control stores and retrieves local memory. Use Claude Code
              or Codex with Mission Control MCP to ask questions about your
              work.
            </span>
          </div>
        </div>
        <div className="dashboard-aside">
          <section className="panel project-panel">
            <SectionHeader
              label="Approved projects"
              action={
                <button
                  className="icon-button"
                  aria-label="View all projects"
                  onClick={() => onOpenView("Projects")}
                >
                  <ArrowRight size={16} />
                </button>
              }
            />
            {snapshot.projects.map((p) => (
              <button
                className="project-summary"
                key={p.id}
                onClick={() => onSelectProject(p.id)}
              >
                <span
                  className="project-monogram"
                  style={{ background: `${p.color}18`, color: p.color }}
                >
                  {p.name.slice(0, 1)}
                </span>
                <span>
                  <strong>{p.name}</strong>
                  <small>
                    {p.repositories?.length || 0} local repositories
                  </small>
                </span>
                <ArrowRight size={14} />
              </button>
            ))}
            {!snapshot.projects.length && (
              <div className="small-empty">No projects are approved yet.</div>
            )}
          </section>
          <section className="panel sources-panel">
            <SectionHeader
              label="Collection freshness"
              action={
                <button
                  className="icon-button"
                  aria-label="Configure source health"
                  onClick={onConfigureSources}
                >
                  <Settings2 size={15} />
                </button>
              }
            />
            {snapshot.sources.slice(0, 5).map((s) => (
              <SourceHealth source={s} key={s.id} />
            ))}
            <div className="privacy-note">
              <ShieldCheck size={14} />
              <span>Stored on this device. Telemetry is off.</span>
            </div>
          </section>
        </div>
      </div>
    </>
  );
}

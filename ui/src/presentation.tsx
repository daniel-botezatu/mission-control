import { type ReactNode } from "react";
import { Activity, Sparkles, Terminal, Inbox } from "lucide-react";
import {
  type EvidenceRecord,
  type ProjectInfo,
  type SourceStatus,
} from "./bridge";
type IconType = typeof Activity;
export const errorText = (e: unknown) =>
  e instanceof Error ? e.message : String(e);
export const shortTime = (iso: string | null | undefined) =>
  iso
    ? new Date(iso).toLocaleTimeString([], {
        hour: "2-digit",
        minute: "2-digit",
      })
    : "Not yet";
export const dateTime = (iso: string | null | undefined) =>
  iso
    ? new Date(iso).toLocaleString([], {
        month: "short",
        day: "numeric",
        hour: "2-digit",
        minute: "2-digit",
      })
    : "Not yet observed";
/** Keep source timestamps intact while making timestamps in generated prose readable. */
export const formatEvidenceText = (text: string) =>
  text.replace(
    /\b\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+-]\d{2}:\d{2})\b/g,
    (value) => {
      const date = new Date(value);
      return Number.isNaN(date.getTime())
        ? value
        : date.toLocaleString([], {
            year: "numeric",
            month: "short",
            day: "numeric",
            hour: "2-digit",
            minute: "2-digit",
            timeZoneName: "short",
          });
    },
  );
export const relative = (iso: string | null | undefined) => {
  if (!iso) return "Not yet observed";
  const m = Math.max(
    0,
    Math.floor((Date.now() - new Date(iso).getTime()) / 60000),
  );
  return m < 1
    ? "Just now"
    : m < 60
      ? `${m}m ago`
      : m < 1440
        ? `${Math.floor(m / 60)}h ago`
        : `${Math.floor(m / 1440)}d ago`;
};
export const titleCase = (text: string) =>
  text.replace(/[_-]/g, " ").replace(/\b\w/g, (x) => x.toUpperCase());
export const sourceName = (s: string) =>
  ({ codex: "Codex", claude: "Claude Code" })[s.toLowerCase()] ?? titleCase(s);
export function SourceIcon({
  kind,
  size = 16,
}: {
  kind: string;
  size?: number;
}) {
  const Icon = /claude/i.test(kind) ? Sparkles : Terminal;
  return <Icon size={size} />;
}
export function Empty({
  icon: Icon = Inbox,
  title,
  children,
  action,
}: {
  icon?: IconType;
  title: string;
  children?: ReactNode;
  action?: ReactNode;
}) {
  return (
    <div className="empty-state">
      <span className="empty-icon">
        <Icon size={25} />
      </span>
      <h3>{title}</h3>
      <p>{children}</p>
      {action}
    </div>
  );
}
export function Badge({
  children,
  tone = "neutral",
}: {
  children: ReactNode;
  tone?: string;
}) {
  return <span className={`badge ${tone}`}>{children}</span>;
}
export function ProjectDot({ project }: { project?: ProjectInfo }) {
  return (
    <span
      className="project-dot"
      style={{ background: project?.color || "#9299a6" }}
    />
  );
}

export function SourceHealth({ source: s }: { source: SourceStatus }) {
  return (
    <div className="source-health">
      <span className={`source-logo source-${s.kind.toLowerCase()}`}>
        <SourceIcon kind={s.kind} size={17} />
      </span>
      <div>
        <strong>{s.name}</strong>
        <span>
          {s.enabled
            ? s.gap
              ? s.gap
              : `${titleCase(s.state)}${s.lastSynced ? ` · ${relative(s.lastSynced)}` : ""}`
            : "Not enabled"}
        </span>
        {s.pending > 0 && (
          <small>
            {s.pending} file{s.pending === 1 ? "" : "s"} waiting
            {s.progress && ` · ${(s.progress.pendingBytes / 1024 / 1024).toFixed(1)} MB remaining`}
          </small>
        )}
      </div>
      <span
        className={`health-dot ${s.gap ? "warning" : s.enabled && /synced|current|watch|ready|idle/i.test(s.state) ? "good" : ""}`}
      />
    </div>
  );
}

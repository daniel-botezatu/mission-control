import type { ReactNode } from "react";
import { ChevronRight } from "lucide-react";
import type { Correction, EvidenceRecord, ProjectInfo } from "./bridge";
import {
  Badge,
  ProjectDot,
  SourceIcon,
  dateTime,
  relative,
  sourceName,
  titleCase,
} from "./presentation";

/** Snapshot lookups and the evidence drawer opener shared by every evidence view. */
export interface EvidenceLookup {
  projectFor: (id: string | null | undefined) => ProjectInfo | undefined;
  correctionFor: (id: string) => Correction | undefined;
  openRecord: (id: string) => Promise<void>;
  openSession?: (id: string, title?: string) => Promise<void>;
}

/** Claude/Codex records grouped by conversation, in snapshot order. */
export function sessionGroups(records: EvidenceRecord[]) {
  const groups = new Map<string, EvidenceRecord[]>();
  for (const r of records.filter((r) => /claude|codex/i.test(r.sourceKind))) {
    const key = `${r.sourceId}:${r.sessionId}`;
    groups.set(key, [...(groups.get(key) ?? []), r]);
  }
  return [...groups.values()];
}

export function RecordRow({
  record: r,
  lookup,
  compact = false,
  highlight = "",
}: {
  record: EvidenceRecord;
  lookup: EvidenceLookup;
  compact?: boolean;
  highlight?: string;
}) {
  const c = lookup.correctionFor(r.id);
  const project = lookup.projectFor(c?.projectId || r.projectId);
  const needle = highlight.trim().split(/\s+/)[0]?.toLocaleLowerCase() || "";
  const normalizedBody = r.body.replace(/\s+/g, " ");
  const match = needle
    ? normalizedBody.toLocaleLowerCase().indexOf(needle)
    : -1;
  const previewStart = Math.max(0, match - 50);
  const preview =
    (previewStart ? "…" : "") +
    normalizedBody.slice(previewStart, previewStart + 200);
  const mark = (value: string) => {
    const index = needle ? value.toLocaleLowerCase().indexOf(needle) : -1;
    return index < 0 ? (
      value
    ) : (
      <>
        {value.slice(0, index)}
        <mark>{value.slice(index, index + needle.length)}</mark>
        {value.slice(index + needle.length)}
      </>
    );
  };
  return (
    <button
      className={`record-row ${compact ? "compact" : ""}`}
      onClick={() => void lookup.openRecord(r.id)}
    >
      <span className={`record-icon source-${r.sourceKind.toLowerCase()}`}>
        <SourceIcon kind={r.sourceKind} size={compact ? 16 : 18} />
      </span>
      <span className="record-main">
        <span className="record-title">{mark(c?.task || r.title)}</span>
        {!compact && <span className="record-preview">{mark(preview)}</span>}
        <span className="record-meta">
          <span>{sourceName(r.sourceKind)}</span>
          <span className="meta-separator">·</span>
          <span>{r.author === "assistant" ? "Final response" : "You"}</span>
          <span className="meta-separator">·</span>
          <span>
            <ProjectDot project={project} />
            {project?.name ?? "Approved project"}
          </span>
          {r.state && (
            <>
              <span className="meta-separator">·</span>
              <span>{titleCase(r.state)}</span>
            </>
          )}
          {c?.dismissed && <Badge>Dismissed</Badge>}
          {c?.snoozedUntil && new Date(c.snoozedUntil) > new Date() && (
            <Badge>Snoozed</Badge>
          )}
        </span>
      </span>
      <span className="record-time" title={dateTime(r.timestamp)}>
        {relative(r.timestamp)}
        <ChevronRight size={14} />
      </span>
    </button>
  );
}

export function SectionHeader({
  label,
  count,
  action,
}: {
  label: string;
  count?: number;
  action?: ReactNode;
}) {
  return (
    <div className="section-header">
      <h2>
        {label}
        {count !== undefined && <span className="section-count">{count}</span>}
      </h2>
      {action}
    </div>
  );
}

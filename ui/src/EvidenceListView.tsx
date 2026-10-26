import { useEffect, useRef, useState } from "react";
import {
  ArrowRight,
  ChevronLeft,
  ChevronRight,
  Clock3,
  Filter,
  Folder,
  LoaderCircle,
  MessageSquare,
  Search,
  X,
} from "lucide-react";
import {
  rpc,
  type AppSnapshot,
  type EvidenceRecord,
  type QueryResult,
} from "./bridge";
import { RecordRow, type EvidenceLookup } from "./EvidenceRows";
import type { View } from "./Shell";
import {
  Badge,
  Empty,
  ProjectDot,
  SourceIcon,
  errorText,
  relative,
  sourceName,
  titleCase,
} from "./presentation";

export interface EvidenceFilters {
  search: string;
  project: string;
  source: string;
  person: string;
  kind: string;
  state: string;
}

/**
 * Debounced, paged storage query for the list views. A newer request or an explicit cancel
 * supersedes older replies, so a slow search can never overwrite a newer page.
 */
export function useEvidenceQuery(
  view: View,
  filters: EvidenceFilters,
  offset: number,
  version: number,
) {
  const [results, setResults] = useState<QueryResult>({
    records: [],
    total: 0,
  });
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState("");
  const generation = useRef(0);
  const loadedKey = useRef<string | null>(null);
  const { search, project, source, person, kind, state } = filters;
  useEffect(() => {
    const current = ++generation.current;
    if (!["Timeline", "Sessions", "Search"].includes(view)) {
      setLoading(false);
      return;
    }
    if (view === "Search" && !search.trim()) {
      setResults({ records: [], total: 0 });
      setLoading(false);
      setError("");
      loadedKey.current = null;
      return;
    }
    const key = JSON.stringify([
      view,
      search,
      project,
      source,
      person,
      kind,
      state,
      offset,
    ]);
    // Collector notifications refresh rows quietly. Only a new view/filter/page
    // changes the visible search state; late replies cannot replace newer rows.
    setLoading(loadedKey.current !== key);
    if (loadedKey.current !== key) setResults({ records: [], total: 0 });
    const t = window.setTimeout(() => {
      rpc<QueryResult>(view === "Sessions" ? "sessions" : "query", {
        search: search || null,
        projectId: project || null,
        sourceKind: source || null,
        person: person || null,
        kind: kind || null,
        state: state || null,
        limit: 50,
        offset,
        byTimestamp: view === "Timeline",
      })
        .then((r) => {
          if (current === generation.current) {
            setResults(r);
            loadedKey.current = key;
            setError("");
          }
        })
        .catch((e) => {
          if (current === generation.current) setError(errorText(e));
        })
        .finally(() => {
          if (current === generation.current) setLoading(false);
        });
    }, 220);
    return () => {
      clearTimeout(t);
      generation.current++;
    };
  }, [view, search, project, source, person, kind, state, offset, version]);
  const cancel = () => {
    generation.current++;
    setLoading(false);
  };
  return { results, loading, error, cancel };
}

export default function EvidenceListView({
  view,
  snapshot,
  filters,
  onFilter,
  offset,
  onOffset,
  query,
  lookup,
  onRetry,
}: {
  view: View;
  snapshot: AppSnapshot;
  filters: EvidenceFilters;
  onFilter: (change: Partial<EvidenceFilters>) => void;
  offset: number;
  onOffset: (update: (value: number) => number) => void;
  query: ReturnType<typeof useEvidenceQuery>;
  lookup: EvidenceLookup;
  onRetry: () => void;
}) {
  const { search, project, source, person, kind } = filters;
  const { results, loading: queryLoading, error: queryError } = query;
  const { projectFor, correctionFor, openRecord } = lookup;
  const emptySearch = view === "Search" && !search.trim();
  const days = new Map<string, EvidenceRecord[]>();
  if (view === "Timeline")
    for (const record of results.records) {
      const label = new Date(record.timestamp).toLocaleDateString([], {
        weekday: "long",
        year: "numeric",
        month: "long",
        day: "numeric",
      });
      days.set(label, [...(days.get(label) || []), record]);
    }
  return (
    <>
      <div className="page-heading">
        <div>
          <div className="eyebrow">
            {view === "Sessions"
              ? "PICK UP THE CONVERSATION"
              : view === "Search"
                ? "FIND YOUR DEVELOPER MEMORY"
                : "FOLLOW THE LOCAL HISTORY"}
          </div>
          <h1>
            {view === "Sessions"
              ? "Your sessions"
              : view === "Search"
                ? "Search conversation history"
                : "Workspace timeline"}
          </h1>
          <p>
            {view === "Sessions"
              ? "One card per conversation. Open a session to read its user messages and final responses together."
              : view === "Search"
                ? "Find a specific message, decision or note by keyword, then open its original context."
                : "Follow messages across conversations, grouped by day. Newest source timestamps appear first."}
          </p>
        </div>
        <Badge tone={snapshot.mode === "demo" ? "amber" : "neutral"}>
          {snapshot.mode === "demo"
            ? "Fixture-backed evidence"
            : "Stored evidence"}
        </Badge>
      </div>
      <div className="filter-bar">
        {view !== "Timeline" ? (
          <label className="timeline-search">
            <Search size={16} />
            <input
              aria-label={
                view === "Search" ? "Search messages" : "Find a conversation"
              }
              placeholder={
                view === "Search"
                  ? "Search messages, decisions and notes…"
                  : "Find a conversation…"
              }
              value={search}
              onChange={(e) => onFilter({ search: e.target.value })}
            />
          </label>
        ) : (
          <span className="timeline-order">
            <Clock3 size={15} /> Newest messages first
          </span>
        )}
        <label className="select-wrap">
          <Folder size={14} />
          <select
            aria-label="Filter by project"
            value={project}
            onChange={(e) => onFilter({ project: e.target.value })}
          >
            <option value="">All projects</option>
            {snapshot.projects.map((p) => (
              <option key={p.id} value={p.id}>
                {p.name}
              </option>
            ))}
          </select>
        </label>
        <label className="select-wrap">
          <Filter size={14} />
          <select
            aria-label="Filter by source"
            value={source}
            onChange={(e) => onFilter({ source: e.target.value })}
          >
            <option value="">All sources</option>
            {["claude", "codex"].map((s) => (
              <option key={s} value={s}>
                {sourceName(s)}
              </option>
            ))}
          </select>
        </label>
        {(search || project || source || person || kind) && (
          <button
            className="icon-button"
            aria-label="Clear filters"
            onClick={() =>
              onFilter({
                search: "",
                project: "",
                source: "",
                person: "",
                kind: "",
                state: "",
              })
            }
          >
            <X size={16} />
          </button>
        )}
      </div>
      <section className="panel timeline-panel">
        <div className="list-summary">
          <span>
            {emptySearch
              ? "Search your approved local history"
              : queryLoading
                ? "Finding evidence…"
                : `${results.total.toLocaleString()} ${view === "Sessions" ? "conversations" : view === "Search" ? "matching messages" : "messages across conversations"}`}
          </span>
          {queryLoading && (
            <button className="text-button" onClick={query.cancel}>
              Cancel search
            </button>
          )}
        </div>
        {results.searchNotice && <p className="field-hint">{results.searchNotice}</p>}
        {emptySearch ? (
          <Empty title="Find something you remember" icon={Search}>
            Enter a keyword from a conversation or a note. For example: a
            feature name, an error message or a decision.
          </Empty>
        ) : queryError ? (
          <Empty title="This search could not finish">
            {queryError}
            <button className="button secondary" onClick={onRetry}>
              Retry search
            </button>
          </Empty>
        ) : queryLoading && !results.records.length ? (
          <div className="list-loading">
            <LoaderCircle size={22} className="spin" />
            <span>Searching local evidence</span>
          </div>
        ) : view === "Sessions" ? (
          <div className="session-grid">
            {results.records.map((r) => {
              return (
                <button
                  className="session-card"
                  key={`${r.sourceId}:${r.sessionId}`}
                  onClick={() =>
                    void (lookup.openSession
                      ? lookup.openSession(r.id, r.title)
                      : openRecord(r.id))
                  }
                >
                  <span className="session-card-top">
                    <span className={`source-logo source-${r.sourceKind}`}>
                      <SourceIcon kind={r.sourceKind} />
                    </span>
                    <span>{sourceName(r.sourceKind)}</span>
                    <Badge>
                      {r.state
                        ? `Last observed: ${titleCase(r.state)}`
                        : "Conversation"}
                    </Badge>
                  </span>
                  <h3>{correctionFor(r.id)?.task || r.title}</h3>
                  <p>{r.body.slice(0, 160)}</p>
                  <span className="session-meta">
                    <ProjectDot project={projectFor(r.projectId)} />
                    {projectFor(r.projectId)?.name || "Approved project"}
                  </span>
                  <div className="session-card-footer">
                    <span>
                      <Clock3 size={12} />
                      Updated {relative(r.changedAt || r.timestamp)}
                    </span>
                    <span>
                      Open conversation <ArrowRight size={13} />
                    </span>
                  </div>
                </button>
              );
            })}
            {!results.records.length && (
              <Empty title="No sessions in this page" icon={MessageSquare}>
                Choose a Claude Code or Codex source, adjust your filters, or
                try the next page.
              </Empty>
            )}
          </div>
        ) : view === "Timeline" ? (
          <div className="timeline-days">
            {[...days].map(([day, records]) => (
              <section className="timeline-day" key={day}>
                <h2>{day}</h2>
                {records.map((r) => (
                  <div className="timeline-event" key={r.id}>
                    <time dateTime={r.timestamp}>
                      {new Date(r.timestamp).toLocaleTimeString([], {
                        hour: "2-digit",
                        minute: "2-digit",
                      })}
                    </time>
                    <RecordRow record={r} lookup={lookup} />
                  </div>
                ))}
              </section>
            ))}
            {!results.records.length && (
              <Empty title="No messages in this timeline" icon={Clock3}>
                Choose another project or source, or check collection freshness
                on Overview.
              </Empty>
            )}
          </div>
        ) : (
          <>
            {results.records.map((r) => (
              <RecordRow
                record={r}
                key={r.id}
                lookup={lookup}
                highlight={search}
              />
            ))}
            {!results.records.length && (
              <Empty title="No matching evidence" icon={Search}>
                Try another filter or enable sources in Settings.
              </Empty>
            )}
          </>
        )}
        {!emptySearch && (
          <div className="pagination">
            <span>
              {results.total
                ? `${offset + 1}–${Math.min(offset + 50, results.total)} of ${results.total.toLocaleString()} ${view === "Sessions" ? "sessions" : "records"}`
                : "No results"}
              <small>
                50 {view === "Sessions" ? "sessions" : "records"} per page ·
                bounded local search
              </small>
            </span>
            <div>
              <button
                className="button secondary small"
                disabled={offset === 0 || queryLoading}
                onClick={() => onOffset((v) => Math.max(0, v - 50))}
              >
                <ChevronLeft size={14} />
                Previous
              </button>
              <button
                className="button secondary small"
                disabled={offset + 50 >= results.total || queryLoading}
                onClick={() => onOffset((v) => v + 50)}
              >
                Next
                <ChevronRight size={14} />
              </button>
            </div>
          </div>
        )}
      </section>
    </>
  );
}

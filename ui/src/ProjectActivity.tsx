import { useEffect, useState, type ReactNode } from "react";
import { LoaderCircle } from "lucide-react";
import { rpc, type EvidenceRecord, type QueryResult } from "./bridge";
import { useThrottledVersion } from "./useThrottledVersion";

export interface ProjectSummary {
  sessions: number;
  lastObserved?: string;
}

const errorText = (error: unknown) =>
  error instanceof Error ? error.message : String(error);

// Throttle changing versions so queries continue during a long rebuild.

/**
 * Session counts and last activity per project, read from storage. The dashboard snapshot
 * holds only the newest records, so it cannot describe projects that were quiet recently.
 */
export function useProjectSummaries(projectIds: string[], version: number) {
  const refreshVersion = useThrottledVersion(version);
  const [summaries, setSummaries] = useState<Record<string, ProjectSummary>>(
    {},
  );
  const key = projectIds.join("\n");
  useEffect(() => {
    let cancelled = false;
    const timer = window.setTimeout(async () => {
      const next: Record<string, ProjectSummary> = {};
      for (const id of key ? key.split("\n") : []) {
        if (cancelled) return;
        try {
          const result = await rpc<QueryResult>("sessions", {
            projectId: id,
            limit: 1,
            offset: 0,
          });
          next[id] = {
            sessions: result.total,
            lastObserved: result.records[0]?.timestamp,
          };
        } catch {
          // Keep the previous value; a transient failure must not show an empty project.
        }
      }
      if (!cancelled) setSummaries((previous) => ({ ...previous, ...next }));
    }, 0);
    return () => {
      cancelled = true;
      window.clearTimeout(timer);
    };
  }, [key, refreshVersion]);
  return summaries;
}

/** The project's newest evidence from storage, not limited to the dashboard snapshot. */
export function ProjectRecentEvidence({
  projectId,
  version,
  renderRecord,
}: {
  projectId: string;
  version: number;
  renderRecord: (record: EvidenceRecord) => ReactNode;
}) {
  const refreshVersion = useThrottledVersion(version);
  const [result, setResult] = useState<{
    projectId: string;
    records: EvidenceRecord[];
    total: number;
  } | null>(null);
  const [error, setError] = useState("");
  useEffect(() => {
    let cancelled = false;
    const timer = window.setTimeout(
      () => {
        rpc<QueryResult>("query", { projectId, limit: 25, offset: 0, byTimestamp: true })
          .then((r) => {
            if (cancelled) return;
            setResult({ projectId, records: r.records, total: r.total });
            setError("");
          })
          .catch((e) => {
            if (!cancelled) setError(errorText(e));
          });
      },
      // Open a newly selected project immediately; only refreshes wait for quiet.
      0,
    );
    return () => {
      cancelled = true;
      window.clearTimeout(timer);
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [projectId, refreshVersion]);

  const current = result?.projectId === projectId ? result : null;
  if (error && !current)
    return (
      <p className="project-evidence-note" role="alert">
        {error}
      </p>
    );
  if (!current)
    return (
      <p className="project-evidence-note" role="status">
        <LoaderCircle size={13} className="spin" /> Reading this project's
        evidence.
      </p>
    );
  if (current.records.length === 0)
    return (
      <p className="project-evidence-note">
        No evidence is attributed to this project yet.
      </p>
    );
  return (
    <>
      {current.records.map(renderRecord)}
      {current.total > current.records.length && (
        <p className="project-evidence-note">
          Showing the newest {current.records.length} of{" "}
          {current.total.toLocaleString()} records. Use Timeline to browse older
          evidence.
        </p>
      )}
    </>
  );
}

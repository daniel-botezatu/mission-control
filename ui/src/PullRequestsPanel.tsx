import { useEffect, useRef, useState } from "react";
import { ExternalLink, GitPullRequest, RefreshCw } from "lucide-react";
import { rpc, type PullRequestSummary } from "./bridge";
import { SectionHeader } from "./EvidenceRows";
import { Badge, dateTime, errorText, relative } from "./presentation";

export default function PullRequestsPanel({ mode }: { mode: string }) {
  const [summary, setSummary] = useState<PullRequestSummary | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState("");
  const generation = useRef(0);
  const load = async (refresh = false) => {
    const current = ++generation.current;
    setLoading(true);
    setError("");
    try {
      const result = await rpc<PullRequestSummary>("pullRequests", { refresh });
      if (current === generation.current) setSummary(result);
    } catch (e) {
      if (current === generation.current) setError(errorText(e));
    } finally {
      if (current === generation.current) setLoading(false);
    }
  };
  useEffect(() => {
    setSummary(null);
    setLoading(false);
    if (mode === "live") void load();
    return () => {
      generation.current++;
    };
  }, [mode]);
  const open = async (url: string) => {
    try {
      await rpc("pullRequests.open", { url });
    } catch (e) {
      setError(errorText(e));
    }
  };
  return (
    <section
      className="panel pull-requests-panel"
      aria-label="Your open pull requests"
    >
      <SectionHeader
        label="Your open pull requests"
        count={summary?.state === "ready" ? summary.total : undefined}
        action={
          <button
            className="icon-button"
            aria-label="Refresh pull requests"
            disabled={loading || mode !== "live"}
            onClick={() => void load(true)}
          >
            <RefreshCw size={15} className={loading ? "spin" : ""} />
          </button>
        }
      />
      <div className="section-description">
        {summary?.login
          ? `Authored by ${summary.login} · all repositories`
          : "A quick look at the pull requests you authored."}
      </div>
      {mode !== "live" ? (
        <div className="small-empty">
          Switch to Live to show your GitHub pull requests.
        </div>
      ) : error || summary?.state === "unavailable" ? (
        <div className="small-empty" role="status">
          {error || summary?.error}
        </div>
      ) : loading && !summary ? (
        <div className="small-empty" role="status">
          Loading your open pull requests…
        </div>
      ) : summary?.state === "ready" && !summary.items.length ? (
        <div className="small-empty">You have no open pull requests.</div>
      ) : (
        summary?.items.slice(0, 6).map((pr) => (
          <button
            className="pull-request-row"
            key={pr.url}
            onClick={() => void open(pr.url)}
          >
            <GitPullRequest
              size={18}
              className={pr.isDraft ? "pr-draft" : "pr-open"}
            />
            <span className="pull-request-main">
              <strong>{pr.title}</strong>
              <small>
                {pr.repository} #{pr.number} · updated {relative(pr.updatedAt)}
              </small>
            </span>
            <Badge
              tone={
                pr.isDraft
                  ? "neutral"
                  : pr.reviewDecision === "CHANGES_REQUESTED"
                    ? "amber"
                    : pr.reviewDecision === "APPROVED"
                      ? "green"
                      : "neutral"
              }
            >
              {pr.isDraft
                ? "Draft"
                : pr.reviewDecision === "APPROVED"
                  ? "Approved"
                  : pr.reviewDecision === "CHANGES_REQUESTED"
                    ? "Changes requested"
                    : pr.reviewDecision === "REVIEW_REQUIRED"
                      ? "Review required"
                      : "Open"}
            </Badge>
            <ExternalLink size={13} />
          </button>
        ))
      )}
      {summary?.state === "ready" && !error && (
        <div className="pull-requests-footer">
          <small title={summary.checkedAt || ""}>
            Checked {dateTime(summary.checkedAt)}
            {loading ? " · refreshing…" : ""}
          </small>
          {summary.allUrl && (
            <button
              className="text-button"
              onClick={() => void open(summary.allUrl!)}
            >
              View all on GitHub <ExternalLink size={12} />
            </button>
          )}
        </div>
      )}
    </section>
  );
}

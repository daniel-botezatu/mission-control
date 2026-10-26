import { useEffect, useRef, useState, type ReactNode } from "react";
import {
  ExternalLink,
  GitBranch,
  GitPullRequest,
  RefreshCw,
} from "lucide-react";
import {
  rpc,
  type LocalOpenLoops,
  type PullRequestItem,
  type PullRequestSummary,
} from "./bridge";
import { RecordRow, SectionHeader, type EvidenceLookup } from "./EvidenceRows";
import { Badge, dateTime, errorText, relative } from "./presentation";

const SHOWN = 5;

// Most urgent first within "On you".
const STATUS: Record<string, { label: string; tone: string; rank: number }> = {
  review_requested: { label: "Review requested", tone: "amber", rank: 0 },
  changes_requested: { label: "Changes requested", tone: "amber", rank: 1 },
  conflict: { label: "Merge conflict", tone: "amber", rank: 2 },
  checks_failing: { label: "Checks failing", tone: "amber", rank: 3 },
  approved: { label: "Approved", tone: "green", rank: 4 },
  draft: { label: "Draft", tone: "neutral", rank: 5 },
  review_required: { label: "Waiting for review", tone: "neutral", rank: 6 },
  open: { label: "Open", tone: "neutral", rank: 7 },
};

const BRANCH: Record<string, { label: string; tone: string }> = {
  ahead: { label: "Not pushed", tone: "amber" },
  never_pushed: { label: "Never pushed", tone: "amber" },
  unverified: { label: "Not verified", tone: "neutral" },
};

/**
 * Things waiting for a move: pull requests and review requests (GitHub CLI, cached 5 minutes), local branches not
 * pushed and drafts left at the end of a conversation. Loads only while the overview is open; nothing runs in the
 * background and no AI is involved.
 */
export default function OpenLoopsPanel({
  mode,
  lookup,
}: {
  mode: string;
  lookup: EvidenceLookup;
}) {
  const [summary, setSummary] = useState<PullRequestSummary | null>(null);
  const [local, setLocal] = useState<LocalOpenLoops | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState("");
  const [localError, setLocalError] = useState("");
  const generation = useRef(0);
  const load = async (refresh = false) => {
    const current = ++generation.current;
    setLoading(true);
    setError("");
    setLocalError("");
    await Promise.all([
      rpc<PullRequestSummary>("pullRequests", { refresh }).then(
        (result) => current === generation.current && setSummary(result),
        (e) => current === generation.current && setError(errorText(e)),
      ),
      rpc<LocalOpenLoops>("openLoops.local").then(
        (result) => current === generation.current && setLocal(result),
        (e) => current === generation.current && setLocalError(errorText(e)),
      ),
    ]);
    if (current === generation.current) setLoading(false);
  };
  useEffect(() => {
    setSummary(null);
    setLocal(null);
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
  const ready = summary?.state === "ready";
  const pullRequests = ready
    ? [...(summary.reviewRequests ?? []), ...summary.items]
    : [];
  const onYou = pullRequests
    .filter((pr) => pr.loop === "you")
    .sort((a, b) => rank(a) - rank(b));
  const onOthers = pullRequests.filter((pr) => pr.loop !== "you");
  const reviewRequestTotal = ready ? (summary.reviewRequestTotal ?? 0) : 0;
  const branches = local?.branches ?? [];
  const drafts = local?.drafts ?? [];
  const githubProblem =
    error || (summary?.state === "unavailable" ? summary.error : "");
  const nothing =
    ready &&
    local &&
    !onYou.length &&
    !onOthers.length &&
    !branches.length &&
    !drafts.length;
  const row = (pr: PullRequestItem) => (
    <button
      className="pull-request-row"
      data-kind="pr"
      key={`${pr.status}:${pr.url}`}
      onClick={() => void open(pr.url)}
    >
      <GitPullRequest
        size={18}
        className={pr.isDraft ? "pr-draft" : "pr-open"}
      />
      <span className="pull-request-main">
        <strong>{pr.title}</strong>
        <small>
          {pr.repository} #{pr.number}
          {pr.author ? ` · by ${pr.author}` : ""} · updated{" "}
          {relative(pr.updatedAt)}
        </small>
      </span>
      <Badge tone={STATUS[pr.status]?.tone ?? "neutral"}>
        {STATUS[pr.status]?.label ?? "Open"}
      </Badge>
      <ExternalLink size={13} />
    </button>
  );
  return (
    <section
      className="panel pull-requests-panel open-loops-panel"
      aria-label="Open loops"
    >
      <SectionHeader
        label="Open loops"
        count={
          ready && local
            ? onYou.length + onOthers.length + branches.length + drafts.length
            : undefined
        }
        action={
          <button
            className="icon-button"
            aria-label="Refresh open loops"
            disabled={loading || mode !== "live"}
            onClick={() => void load(true)}
          >
            <RefreshCw size={15} className={loading ? "spin" : ""} />
          </button>
        }
      />
      <div className="section-description">
        {summary?.login ? `GitHub as ${summary.login}` : "GitHub"}, local
        branches of approved projects, and drafts the AI left at the end of a
        conversation.
      </div>
      {mode !== "live" ? (
        <div className="small-empty">
          Switch to Live to show your open loops.
        </div>
      ) : loading && !summary && !local ? (
        <div className="small-empty" role="status">
          Looking for open loops…
        </div>
      ) : nothing ? (
        <div className="small-empty">
          Nothing waiting: no pull request needs you, every branch is pushed and
          no draft was left unsent.
        </div>
      ) : (
        <>
          {githubProblem && (
            <div className="small-empty" role="status">
              {githubProblem}
            </div>
          )}
          <Group label="On you" items={onYou} render={row}>
            {ready && reviewRequestTotal > 0 && summary.reviewRequestsUrl && (
              <button
                className="open-loops-more"
                onClick={() => void open(summary.reviewRequestsUrl!)}
              >
                {reviewRequestTotal > (summary.reviewRequests?.length ?? 0)
                  ? `All ${reviewRequestTotal} review requests on GitHub`
                  : "Review requests on GitHub"}{" "}
                <ExternalLink size={12} />
              </button>
            )}
          </Group>
          <Group label="Waiting on others" items={onOthers} render={row}>
            {ready &&
              summary.total > summary.items.length &&
              summary.allUrl && (
                <button
                  className="open-loops-more"
                  onClick={() => void open(summary.allUrl!)}
                >
                  All {summary.total} of your pull requests on GitHub{" "}
                  <ExternalLink size={12} />
                </button>
              )}
          </Group>
          {localError && (
            <div className="small-empty" role="status">
              {localError}
            </div>
          )}
          <Group
            label="Not pushed"
            items={branches}
            render={(branch) => (
              <div
                className="pull-request-row"
                data-kind="branch"
                key={`${branch.checkout}:${branch.branch}`}
                title={branch.checkout}
              >
                <GitBranch size={18} className="pr-draft" />
                <span className="pull-request-main">
                  <strong>{branch.branch}</strong>
                  <small>
                    {branch.repository}
                    {branch.commits != null
                      ? ` · ${branch.commits} ${branch.commits === 1 ? "commit" : "commits"} not pushed`
                      : branch.state === "unverified"
                        ? " · its history could not be read, so whether it was pushed is unknown"
                        : ""}{" "}
                    · last commit {relative(branch.lastCommitAt)}
                  </small>
                </span>
                <Badge tone={BRANCH[branch.state]?.tone ?? "neutral"}>
                  {BRANCH[branch.state]?.label ?? "Not pushed"}
                </Badge>
              </div>
            )}
          >
            {(local?.branchesOmitted ?? 0) > 0 && (
              <div className="open-loops-more">
                {local!.branchesOmitted} more not shown: the panel lists the 50
                most recently changed branches.
              </div>
            )}
          </Group>
          <Group
            label="Drafts not sent"
            items={drafts}
            render={(record) => (
              <RecordRow
                record={record}
                key={record.id}
                lookup={lookup}
                compact
              />
            )}
          />
        </>
      )}
      {(ready || local) && (
        <div className="pull-requests-footer">
          <small title={summary?.checkedAt || local?.checkedAt || ""}>
            Checked {dateTime(summary?.checkedAt || local?.checkedAt)}
            {loading ? " · refreshing…" : ""}
          </small>
          {summary?.allUrl && (
            <button
              className="text-button"
              onClick={() => void open(summary.allUrl!)}
            >
              Your pull requests on GitHub <ExternalLink size={12} />
            </button>
          )}
        </div>
      )}
    </section>
  );
}

function rank(pr: PullRequestItem) {
  return STATUS[pr.status]?.rank ?? 99;
}

// Shows the first few items; the rest open in place, so every loaded item can be reached.
function Group<T>({
  label,
  items,
  render,
  children,
}: {
  label: string;
  items: T[];
  render: (item: T) => ReactNode;
  children?: ReactNode;
}) {
  const [expanded, setExpanded] = useState(false);
  if (!items.length) return null;
  const hidden = items.length - SHOWN;
  return (
    <div className="open-loops-group" role="group" aria-label={label}>
      <h3>
        {label} <span>{items.length}</span>
      </h3>
      {(expanded ? items : items.slice(0, SHOWN)).map(render)}
      {hidden > 0 && (
        <button
          className="open-loops-more"
          aria-expanded={expanded}
          onClick={() => setExpanded(!expanded)}
        >
          {expanded ? "Show fewer" : `Show ${hidden} more`}
        </button>
      )}
      {children}
    </div>
  );
}

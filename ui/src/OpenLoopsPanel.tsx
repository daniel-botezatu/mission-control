import { useEffect, useRef, useState, type ReactNode } from "react";
import { GitBranch, RefreshCw } from "lucide-react";
import { rpc, type LocalOpenLoops } from "./bridge";
import { RecordRow, SectionHeader, type EvidenceLookup } from "./EvidenceRows";
import { Badge, dateTime, errorText, relative } from "./presentation";

const SHOWN = 5;
const BRANCH: Record<string, { label: string; tone: string }> = {
  ahead: { label: "Not pushed", tone: "amber" },
  never_pushed: { label: "Never pushed", tone: "amber" },
  unverified: { label: "Not verified", tone: "neutral" },
};

/** Local branch reminders and conversation drafts; loaded only while Overview is open. */
export default function OpenLoopsPanel({
  mode,
  lookup,
}: {
  mode: string;
  lookup: EvidenceLookup;
}) {
  const [local, setLocal] = useState<LocalOpenLoops | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState("");
  const generation = useRef(0);
  const load = async () => {
    const current = ++generation.current;
    setLoading(true);
    setError("");
    try {
      const result = await rpc<LocalOpenLoops>("openLoops.local");
      if (current === generation.current) setLocal(result);
    } catch (e) {
      if (current === generation.current) setError(errorText(e));
    } finally {
      if (current === generation.current) setLoading(false);
    }
  };
  useEffect(() => {
    setLocal(null);
    setError("");
    setLoading(false);
    if (mode === "live") void load();
    return () => {
      generation.current++;
    };
  }, [mode]);
  const branches = local?.branches ?? [];
  const drafts = local?.drafts ?? [];
  const nothing = local && !error && !branches.length && !drafts.length;
  return (
    <section className="panel open-loops-panel" aria-label="Open loops">
      <SectionHeader
        label="Open loops"
        count={local ? branches.length + drafts.length : undefined}
        action={
          <button
            className="icon-button"
            aria-label="Refresh open loops"
            disabled={loading || mode !== "live"}
            onClick={() => void load()}
          >
            <RefreshCw size={15} className={loading ? "spin" : ""} />
          </button>
        }
      />
      <div className="section-description">
        Local branches of approved projects and drafts the AI left at the end of
        a conversation. Branch status uses the remote references saved on this
        computer.
      </div>
      {mode !== "live" ? (
        <div className="small-empty">
          Switch to Live to show your open loops.
        </div>
      ) : loading && !local ? (
        <div className="small-empty" role="status">
          Looking for open loops...
        </div>
      ) : nothing ? (
        <div className="small-empty">
          No local branch reminders or conversation drafts found.
        </div>
      ) : (
        <>
          {error && (
            <div className="small-empty" role="status">
              {error}
            </div>
          )}
          <Group
            label="Not pushed"
            items={branches}
            render={(branch) => (
              <div
                className="open-loop-row"
                data-kind="branch"
                key={`${branch.checkout}:${branch.branch}`}
                title={branch.checkout}
              >
                <GitBranch size={18} className="branch-icon" />
                <span className="open-loop-main">
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
      {local && (
        <div className="open-loops-footer">
          <small title={local.checkedAt}>
            Checked {dateTime(local.checkedAt)}
            {loading ? " - refreshing..." : ""}
          </small>
        </div>
      )}
    </section>
  );
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

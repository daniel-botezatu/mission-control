import { useState, useEffect, useRef, type FormEvent } from "react";
import {
  FileText,
  X,
  GitBranch,
  Check,
  Copy,
  Undo2,
  CheckCheck,
  Clock3,
} from "lucide-react";
import SessionConversation from "./SessionConversation";
import MessageBody from "./MessageBody";
import {
  type EvidenceRecord,
  type Correction,
  type ProjectInfo,
} from "./bridge";
import {
  SourceIcon,
  Badge,
  titleCase,
  sourceName,
  dateTime,
  errorText,
} from "./presentation";
export default function EvidenceDrawer({
  record,
  correction,
  projects,
  onClose,
  onSave,
  onSessionProject,
  onUndo,
  busy,
  sessionView = false,
  sessionTitle,
}: {
  record: EvidenceRecord;
  correction?: Correction;
  projects: ProjectInfo[];
  onClose: () => void;
  onSave: (c: Correction) => Promise<void>;
  onSessionProject: (projectId: string | null) => Promise<void>;
  onUndo: () => Promise<void>;
  busy: boolean;
  sessionView?: boolean;
  sessionTitle?: string;
}) {
  const [projectId, setProjectId] = useState(
    correction?.projectId ?? record.projectId ?? "",
  );
  const [task, setTask] = useState(correction?.task ?? "");
  const [note, setNote] = useState(correction?.note ?? "");
  const [copied, setCopied] = useState(false);
  const [copyError, setCopyError] = useState("");
  const [actionError, setActionError] = useState("");
  const [actionMessage, setActionMessage] = useState("");
  const [saving, setSaving] = useState(false);
  const closeRef = useRef<HTMLButtonElement>(null);
  const drawerRef = useRef<HTMLElement>(null);
  useEffect(() => {
    const previous = document.activeElement as HTMLElement | null;
    closeRef.current?.focus();
    return () => previous?.focus();
  }, []);
  useEffect(() => {
    setProjectId(correction?.projectId ?? record.projectId ?? "");
    setTask(correction?.task ?? "");
    setNote(correction?.note ?? "");
  }, [record.id, record.projectId, correction]);
  useEffect(() => {
    setActionError("");
    setActionMessage("");
  }, [record.id]);
  const perform = async (action: () => Promise<void>, message: string) => {
    if (saving || busy) return;
    setSaving(true);
    setActionError("");
    setActionMessage("");
    try {
      await action();
      setActionMessage(message);
    } catch (reason) {
      setActionError(errorText(reason));
    } finally {
      setSaving(false);
    }
  };
  const update = (changes: Partial<Correction>) =>
    perform(
      () =>
        onSave({
          recordId: record.id,
          projectId: projectId || record.projectId,
          task: task || null,
          note: note || null,
          dismissed: correction?.dismissed ?? false,
          snoozedUntil: correction?.snoozedUntil ?? null,
          ...changes,
        }),
      "Your context is saved and will survive synchronization.",
    );
  return (
    <section
      className="evidence-drawer"
      ref={drawerRef}
      role="dialog"
      aria-modal="true"
      aria-labelledby="record-title"
      onClick={(e) => e.stopPropagation()}
      onKeyDown={(e) => {
        if (e.key !== "Tab") return;
        const nodes = drawerRef.current?.querySelectorAll<HTMLElement>(
          "button:not(:disabled), input, select, textarea, a[href]",
        );
        if (!nodes?.length) return;
        const first = nodes[0],
          last = nodes[nodes.length - 1];
        if (e.shiftKey && document.activeElement === first) {
          e.preventDefault();
          last.focus();
        } else if (!e.shiftKey && document.activeElement === last) {
          e.preventDefault();
          first.focus();
        }
      }}
    >
      <div className="drawer-header">
        <div>
          <FileText size={17} />
          <strong>{sessionView ? "Conversation" : "Original evidence"}</strong>
        </div>
        <button
          ref={closeRef}
          className="icon-button"
          aria-label="Close evidence details"
          onClick={onClose}
        >
          <X size={19} />
        </button>
      </div>
      <div className="evidence-scroll">
        <div className="evidence-source">
          <span className={`source-logo source-${record.sourceKind}`}>
            <SourceIcon kind={record.sourceKind} size={18} />
          </span>
          <span>{sourceName(record.sourceKind)}</span>
          <Badge>{sessionView ? "Conversation" : titleCase(record.kind)}</Badge>
          {record.state && <Badge>{titleCase(record.state)}</Badge>}
        </div>
        <h2 id="record-title">
          {sessionView ? sessionTitle || "Conversation" : record.title}
        </h2>
        <div className="evidence-byline">
          {sessionView ? "Last updated" : record.author} <span>·</span>{" "}
          {dateTime(
            sessionView
              ? record.changedAt || record.timestamp
              : record.timestamp,
          )}
        </div>
        {sessionView ? (
          <SessionConversation record={record} />
        ) : (
          <MessageBody record={record} />
        )}
        <dl className="record-metadata">
          <div>
            <dt>Source timestamp</dt>
            <dd>{dateTime(record.timestamp)}</dd>
          </div>
          <div>
            <dt>Ingested locally</dt>
            <dd>{dateTime(record.ingestedAt)}</dd>
          </div>
          <div>
            <dt>Session</dt>
            <dd>
              <code>{record.sessionId || "Not available"}</code>
            </dd>
          </div>
          <div>
            <dt>Source reference</dt>
            <dd>
              <code>{record.streamId}</code>
            </dd>
          </div>
          {record.branch && (
            <div>
              <dt>Branch / worktree</dt>
              <dd>
                <GitBranch size={12} />
                {record.branch}
              </dd>
            </div>
          )}
          {record.workingDirectory && (
            <div>
              <dt>Working directory</dt>
              <dd>
                <code>{record.workingDirectory}</code>
              </dd>
            </div>
          )}
          <div>
            <dt>Evidence ID</dt>
            <dd>
              <code>{record.id}</code>
            </dd>
          </div>
        </dl>
        <div className="evidence-navigation">
          <p>
            Use the stable record ID, session ID, and source directory to locate
            the original conversation.
          </p>
          <button
            className="button secondary small"
            onClick={async () => {
              try {
                await navigator.clipboard.writeText(
                  [
                    record.title,
                    `Evidence: ${record.id}`,
                    `Session: ${record.sessionId}`,
                    `Source: ${record.streamId}`,
                    record.workingDirectory ?? "",
                  ]
                    .filter(Boolean)
                    .join("\n"),
                );
                setCopied(true);
              } catch {
                setCopied(false);
                setCopyError(
                  "Clipboard access is unavailable. Select the visible evidence ID, session, and directory above to copy them manually.",
                );
              }
            }}
          >
            {copied ? <Check size={13} /> : <Copy size={13} />}
            {copied ? "Copied" : "Copy record reference"}
          </button>
          {copyError && <p>{copyError}</p>}
        </div>
        <div className="correction-heading">
          <h3>Your context</h3>
          {correction && (
            <button
              className="text-button"
              disabled={busy || saving}
              onClick={() =>
                perform(
                  onUndo,
                  "Record correction removed. The session or automatic project assignment applies again.",
                )
              }
            >
              <Undo2 size={13} />
              Undo correction
            </button>
          )}
        </div>
        <p className="field-hint">
          Your choices are stored separately and survive source reprocessing.
        </p>
        {actionError && (
          <div className="notice error" role="alert">
            {actionError}
          </div>
        )}
        {actionMessage && (
          <div className="notice info" role="status">
            {actionMessage}
          </div>
        )}
        <form
          onSubmit={(e: FormEvent) => {
            e.preventDefault();
            update({});
          }}
        >
          <label>
            Project
            <select
              aria-label="Project"
              value={projectId}
              onChange={(e) => setProjectId(e.target.value)}
            >
              <option value="" disabled>
                Choose an approved project
              </option>
              {projects.map((p) => (
                <option key={p.id} value={p.id}>
                  {p.name}
                </option>
              ))}
            </select>
          </label>
          <label>
            Task or thread label
            <input
              maxLength={200}
              value={task}
              onChange={(e) => setTask(e.target.value)}
              placeholder="Optional · give this thread a useful name"
            />
          </label>
          <label>
            Private context note
            <textarea
              rows={3}
              maxLength={2000}
              value={note}
              onChange={(e) => setNote(e.target.value)}
              placeholder="What would help future you?"
            />
          </label>
          <button
            type="submit"
            className="button primary"
            disabled={busy || saving}
          >
            Save context
          </button>
        </form>
        {/claude|codex/.test(record.sourceKind) && (
          <div className="evidence-actions">
            <button
              className="button secondary small"
              disabled={busy || saving}
              onClick={() =>
                perform(
                  () => onSessionProject(projectId || record.projectId),
                  "Session assignment saved. Individual record choices take precedence.",
                )
              }
            >
              Assign whole session to selected project
            </button>
            <button
              className="button secondary small"
              disabled={busy || saving}
              onClick={() =>
                perform(
                  () => onSessionProject(null),
                  "Automatic session assignment restored. Individual record choices take precedence.",
                )
              }
            >
              Use automatic session project
            </button>
          </div>
        )}
        <div className="evidence-actions">
          <button
            className="button secondary small"
            disabled={busy}
            onClick={() => update({ dismissed: !correction?.dismissed })}
          >
            <CheckCheck size={14} />
            {correction?.dismissed
              ? "Restore to overview"
              : "Dismiss from overview"}
          </button>
          <button
            className="button secondary small"
            disabled={busy}
            onClick={() =>
              update({
                snoozedUntil: new Date(Date.now() + 86400000).toISOString(),
              })
            }
          >
            <Clock3 size={14} />
            Snooze for a day
          </button>
        </div>
        {correction?.dismissed && (
          <div className="notice info">
            Dismissed from overview. The original evidence remains searchable.
          </div>
        )}
        {correction?.snoozedUntil &&
          new Date(correction.snoozedUntil) > new Date() && (
            <div className="notice info">
              Snoozed until {dateTime(correction.snoozedUntil)}.
              <button onClick={() => update({ snoozedUntil: null })}>
                Unsnooze
              </button>
            </div>
          )}
      </div>
    </section>
  );
}

import { useEffect, useRef, useState } from "react";
import {
  ChevronLeft,
  ChevronRight,
  LoaderCircle,
  RefreshCw,
} from "lucide-react";
import {
  rpc,
  subscribeChanged,
  type EvidenceRecord,
  type QueryResult,
} from "./bridge";
import { dateTime, errorText } from "./presentation";
import MessageBody from "./MessageBody";

/** Conversation text is fetched only after opening a session. Only user messages and final responses are retained. */
export default function SessionConversation({
  record,
}: {
  record: EvidenceRecord;
}) {
  return (
    <section className="session-conversation" aria-label="Session conversation">
      <ConversationPage record={record} />
      <p className="field-hint">
        Only retained user messages and final assistant responses are shown.
        Source claims are not independent verification of completion.
      </p>
    </section>
  );
}

function ConversationPage({ record }: { record: EvidenceRecord }) {
  const [page, setPage] = useState<QueryResult>({ records: [], total: 0 });
  const [offset, setOffset] = useState(0);
  const [version, setVersion] = useState(0);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState("");
  const generation = useRef(0);
  const loaded = useRef("");
  const pageSize = 50;
  useEffect(() => {
    let timer: ReturnType<typeof setTimeout>;
    const unsubscribe = subscribeChanged(() => {
      clearTimeout(timer);
      timer = setTimeout(() => setVersion((value) => value + 1), 400);
    });
    return () => {
      clearTimeout(timer);
      unsubscribe();
    };
  }, []);
  useEffect(() => {
    const request = ++generation.current;
    const key = `${record.sourceId}:${record.sessionId}:${offset}`;
    setLoading(loaded.current !== key);
    void rpc<QueryResult>("conversation", {
      sourceId: record.sourceId,
      sourceKind: record.sourceKind,
      sessionId: record.sessionId,
      limit: pageSize,
      offset,
    })
      .then((result) => {
        if (request !== generation.current) return;
        setPage(result);
        loaded.current = key;
        setError("");
      })
      .catch((reason) => {
        if (request === generation.current) setError(errorText(reason));
      })
      .finally(() => {
        if (request === generation.current) setLoading(false);
      });
    return () => {
      generation.current++;
    };
  }, [record.sourceId, record.sessionId, record.sourceKind, offset, version]);
  // Query pages are newest first. Read the displayed page in conversational order.
  const records = [...page.records].sort(
    (a, b) =>
      a.timestamp.localeCompare(b.timestamp) || a.id.localeCompare(b.id),
  );
  return (
    <div className="conversation-page" aria-busy={loading}>
      <div className="conversation-heading">
        <strong>Conversation messages</strong>
        <button
          className="icon-button"
          aria-label="Refresh conversation"
          onClick={() => setVersion((value) => value + 1)}
        >
          <RefreshCw size={14} />
        </button>
      </div>
      {error && (
        <div className="notice error" role="alert">
          {error}
        </div>
      )}
      {loading && !records.length && (
        <div className="list-loading">
          <LoaderCircle size={18} className="spin" />
          Loading conversation
        </div>
      )}
      {!loading && !records.length && (
        <p className="small-empty">No retained messages in this session.</p>
      )}
      {records.map((message) => (
        <article className="conversation-message" key={message.id}>
          <header>
            <strong>
              {message.author === "user"
                ? "You"
                : message.author === "assistant"
                  ? "Assistant"
                  : message.author}
            </strong>
            <time dateTime={message.timestamp}>
              {dateTime(message.timestamp)}
            </time>
          </header>
          <MessageBody record={message} />
        </article>
      ))}
      {page.total > pageSize && (
        <div className="conversation-pagination">
          <span>
            {offset + 1}–{Math.min(offset + pageSize, page.total)} of{" "}
            {page.total} · latest first by page
          </span>
          <div>
            <button
              className="button secondary small"
              disabled={loading || offset === 0}
              onClick={() =>
                setOffset((value) => Math.max(0, value - pageSize))
              }
            >
              <ChevronLeft size={13} />
              Newer messages
            </button>
            <button
              className="button secondary small"
              disabled={loading || offset + pageSize >= page.total}
              onClick={() => setOffset((value) => value + pageSize)}
            >
              Older messages
              <ChevronRight size={13} />
            </button>
          </div>
        </div>
      )}
    </div>
  );
}

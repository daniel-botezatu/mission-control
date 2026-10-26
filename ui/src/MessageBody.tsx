import { useEffect, useRef, useState } from "react";
import { rpc, type EvidenceRecord } from "./bridge";
import { errorText } from "./presentation";

export default function MessageBody({ record }: { record: EvidenceRecord }) {
  const [body, setBody] = useState(record.body);
  const [next, setNext] = useState(record.nextBodyOffset ?? null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState("");
  const generation = useRef(0);
  useEffect(() => {
    generation.current++;
    setBody(record.body); setNext(record.nextBodyOffset ?? null); setBusy(false); setError("");
    return () => { generation.current++; };
  }, [record.id, record.body, record.nextBodyOffset]);
  const more = async () => {
    if (next === null || busy) return;
    const current = generation.current, offset = next;
    setBusy(true);
    try {
      const page = await rpc<EvidenceRecord | null>("record", { id: record.id, bodyOffset: offset });
      if (current !== generation.current) return;
      if (!page) throw new Error("Message is no longer available. Check project approval.");
      setBody(previous => offset === 0 ? page.body : previous + page.body);
      setNext(page.nextBodyOffset ?? null); setError("");
    } catch (reason) {
      if (current === generation.current) setError(errorText(reason));
    } finally {
      if (current === generation.current) setBusy(false);
    }
  };
  return <>
    <div className="evidence-body">{body}</div>
    {next !== null && <button className="button secondary small" disabled={busy} onClick={() => void more()}>
      {busy ? "Loading message…" : "Read more of this message"}
    </button>}
    {error && <p role="alert">{error}</p>}
  </>;
}

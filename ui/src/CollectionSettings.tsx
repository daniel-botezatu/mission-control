import { useEffect, useState, type FormEvent } from "react";
import { rpc, type Settings } from "./bridge";

export default function CollectionSettings({
  settings,
  onSaved,
}: {
  settings: Settings;
  onSaved: (value: Settings) => void;
}) {
  const [draft, setDraft] = useState(settings);
  const [busy, setBusy] = useState(false);
  const [message, setMessage] = useState("");
  const [error, setError] = useState("");
  useEffect(() => setDraft(settings), [settings]);
  async function save(event: FormEvent) {
    event.preventDefault();
    setBusy(true);
    setMessage("");
    setError("");
    try {
      await rpc("saveSettings", {
        lookbackDays: draft.lookbackDays ?? 30,
        storageLimitMb: draft.storageLimitMb ?? 1024,
      });
      onSaved(await rpc<Settings>("settings"));
      setMessage(
        "Saved. Resume collection to apply the new scope and recover available history.",
      );
    } catch (error) {
      setError(error instanceof Error ? error.message : String(error));
    } finally {
      setBusy(false);
    }
  }
  return (
    <section className="panel settings-section collection-settings">
      <h2>Collection scope & storage</h2>
      <p>
        Only approved projects are indexed. User messages and final assistant
        responses are retained; reasoning traces and raw tool output are
        excluded. Original files remain unchanged.
      </p>
      <form onSubmit={save}>
        <label>
          History lookback (days)
          <input
            type="number"
            required
            disabled={busy}
            min={1}
            max={36500}
            value={draft.lookbackDays ?? 30}
            onChange={(e) =>
              setDraft({ ...draft, lookbackDays: Number(e.target.value) })
            }
          />
        </label>
        <label>
          Workspace storage budget (MB)
          <input
            type="number"
            required
            disabled={busy}
            min={64}
            max={8192}
            value={draft.storageLimitMb ?? 1024}
            onChange={(e) =>
              setDraft({ ...draft, storageLimitMb: Number(e.target.value) })
            }
          />
        </label>
        <button className="button primary" disabled={busy}>
          Save collection settings
        </button>
      </form>
      {message && <p role="status">{message}</p>}
      {error && (
        <p className="notice error" role="alert">
          {error}
        </p>
      )}
    </section>
  );
}

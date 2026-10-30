import { dashboardVisible, onDashboardVisibility } from "./visibility";
import { useEffect, useRef, useState } from "react";
import { Database, FolderKanban, HardDrive, MessageSquare } from "lucide-react";
import { rpc, type AppSnapshot, type CollectionStatistics } from "./bridge";
import { dateTime } from "./presentation";

function bytes(value: number) {
  if (value < 1024) return `${value.toLocaleString()} B`;
  if (value < 1024 * 1024) return `${(value / 1024).toFixed(1)} KB`;
  if (value < 1024 * 1024 * 1024)
    return `${(value / 1024 / 1024).toFixed(2)} MB`;
  return `${(value / 1024 / 1024 / 1024).toFixed(2)} GB`;
}

export default function LiveIndexStats({
  snapshot,
}: {
  snapshot: AppSnapshot;
}) {
  const [stats, setStats] = useState<CollectionStatistics | null>(null);
  const [error, setError] = useState(false);
  const baseline = useRef<number | null>(null);
  useEffect(() => {
    let active = true,
      inFlight = false;
    let timer: ReturnType<typeof setTimeout>;
    baseline.current = null;
    setStats(null);
    setError(false);
    const update = async () => {
      if (!active || !dashboardVisible() || inFlight) return;
      inFlight = true;
      try {
        const next = await rpc<CollectionStatistics>("collection.stats");
        if (active && dashboardVisible()) {
          if (baseline.current === null || next.savedEntries < baseline.current)
            baseline.current = next.savedEntries;
          setStats(next);
          setError(false);
        }
      } catch {
        if (active) setError(true);
      } finally {
        inFlight = false;
        if (active && dashboardVisible())
          timer = setTimeout(() => void update(), 2000);
      }
    };
    const visibility = () => {
      clearTimeout(timer);
      if (dashboardVisible()) void update();
    };
    const unsubscribeVisibility = onDashboardVisibility(visibility);
    void update();
    return () => {
      active = false;
      clearTimeout(timer);
      unsubscribeVisibility();
    };
  }, []);
  const paused = (stats?.monitoring || snapshot.monitoring) === "paused";
  const added =
    stats && baseline.current !== null
      ? Math.max(0, stats.savedEntries - baseline.current)
      : 0;
  const disk = stats?.databaseBytes ?? snapshot.databaseBytes;
  return (
    <section className="live-index-stats" aria-label="Live index statistics">
      <div className="overview-stats index-stat-grid">
        <div>
          <span className="stat-icon green">
            <Database size={20} />
          </span>
          <span>
            <strong data-stat="entries">
              {(stats?.savedEntries ?? snapshot.totalRecords).toLocaleString()}
            </strong>
            <small>saved entries</small>
            {added > 0 && (
              <span className="stat-increase">
                +{added.toLocaleString()} this visit
              </span>
            )}
          </span>
        </div>
        <div>
          <span className="stat-icon purple">
            <MessageSquare size={20} />
          </span>
          <span>
            <strong data-stat="sessions">
              {stats?.sessions.toLocaleString() ?? "…"}
            </strong>
            <small>conversations</small>
          </span>
        </div>
        <div title="The active workspace database and its current write files. Original session files and backups are separate.">
          <span className="stat-icon amber">
            <HardDrive size={20} />
          </span>
          <span>
            <strong data-stat="disk">{bytes(disk)}</strong>
            <small>index on disk</small>
            {stats && (
              <span className="stat-detail">
                {bytes(stats.activeDatabaseBytes)} active data
              </span>
            )}
          </span>
        </div>
        <div>
          <span className="stat-icon green">
            <FolderKanban size={20} />
          </span>
          <span>
            <strong data-stat="projects">{snapshot.projects.length}</strong>
            <small>approved projects</small>
          </span>
        </div>
      </div>
      <div className="index-progress-line">
        <span>
          <span
            className={`health-dot ${error || paused ? "warning" : "good"}`}
          />
          {error
            ? "Live counters unavailable · retrying"
            : !stats
              ? "Loading live counters…"
              : paused
                ? "Collection paused"
                : stats.catchingUp
                  ? `Indexing history · ${(stats.pendingFiles ?? 0).toLocaleString()} files · ${bytes(stats.pendingBytes ?? 0)} remaining`
                  : "Monitoring · live"}
        </span>
        {stats && (
          <span className="index-breakdown">
            {stats.userMessages.toLocaleString()} user messages ·{" "}
            {stats.finalResponses.toLocaleString()} final responses ·{" "}
            {stats.statusEvents.toLocaleString()} status events
          </span>
        )}
        <span title="Counters update every 2 seconds while Overview is visible.">
          {stats
            ? `${bytes(stats.storageLimitBytes)} storage budget · Saved ${dateTime(stats.lastCheckpoint)}`
            : "Updates automatically"}
        </span>
      </div>
    </section>
  );
}

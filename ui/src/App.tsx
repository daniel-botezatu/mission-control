import { dashboardVisible, onDashboardVisibility } from "./visibility";
import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { LoaderCircle, ShieldCheck, X } from "lucide-react";
import SettingsView from "./SettingsView";
import EvidenceDrawer from "./EvidenceDrawer";
import { useProjectSummaries } from "./ProjectActivity";
import OverviewView from "./OverviewView";
import ProjectsView from "./ProjectsView";
import EvidenceListView, {
  useEvidenceQuery,
  type EvidenceFilters,
} from "./EvidenceListView";
import { ProjectDialog, SourceDialog } from "./EditorDialogs";
import { type EvidenceLookup } from "./EvidenceRows";
import { Sidebar, Toast, Topbar, WorkspaceNotices, type View } from "./Shell";
import {
  rpc,
  subscribeChanged,
  type AppSnapshot,
  type EvidenceRecord,
  type SourceConfig,
  type Correction,
  type ProjectInfo,
} from "./bridge";

import { errorText, Empty } from "./presentation";

export default function App() {
  const [snapshot, setSnapshot] = useState<AppSnapshot | null>(null);
  const [view, setView] = useState<View>("Overview");
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState("");
  const [busy, setBusy] = useState("");
  const [toast, setToast] = useState("");
  const [theme, setTheme] = useState(
    () => localStorage.getItem("mission-control-theme") || "light",
  );
  const [version, setVersion] = useState("");
  const [search, setSearch] = useState("");
  const [projectFilter, setProjectFilter] = useState("");
  const [sourceFilter, setSourceFilter] = useState("");
  const [personFilter, setPersonFilter] = useState("");
  const [kindFilter, setKindFilter] = useState("");
  const [stateFilter, setStateFilter] = useState("");
  const [offset, setOffset] = useState(0);
  const [queryVersion, setQueryVersion] = useState(0);
  const [selectedProject, setSelectedProject] = useState("");
  const [loadedCorrection, setLoadedCorrection] = useState<
    Correction | undefined
  >();
  const [detail, setDetail] = useState<EvidenceRecord | null>(null);
  const [sessionDetail, setSessionDetail] = useState(false);
  const [sessionTitle, setSessionTitle] = useState("");
  const [settingsTab, setSettingsTab] = useState("Approved projects");
  const [newSource, setNewSource] = useState<SourceConfig | null>(null);
  const [projectDraft, setProjectDraft] = useState<ProjectInfo | null>(null);
  const searchRef = useRef<HTMLInputElement>(null);
  const snapshotGeneration = useRef(0);
  const recordGeneration = useRef(0);
  const closeDetail = useCallback(() => {
    recordGeneration.current++;
    setDetail(null);
    setLoadedCorrection(undefined);
  }, []);

  const refresh = useCallback(async () => {
    const generation = ++snapshotGeneration.current;
    const next = await rpc<AppSnapshot>("snapshot");
    if (generation === snapshotGeneration.current) {
      setSnapshot(next);
      setError("");
      setQueryVersion((v) => v + 1);
    }
    return next;
  }, []);
  useEffect(() => {
    rpc<{ version: string }>("host.version")
      .then((info) => setVersion(info.version))
      .catch(() => {});
  }, []);
  useEffect(() => {
    refresh()
      .catch((e) => setError(errorText(e)))
      .finally(() => setLoading(false));
    let dirty = false;
    let timer: ReturnType<typeof setTimeout> | undefined;
    const schedule = () => {
      dirty = true;
      if (!dashboardVisible() || timer) return;
      timer = setTimeout(() => {
        timer = undefined;
        if (!dashboardVisible()) return;
        dirty = false;
        refresh().catch((e) => setError(errorText(e)));
      }, 500);
    };
    const visibility = () => {
      if (dashboardVisible() && dirty) schedule();
    };
    const unsubscribe = subscribeChanged(schedule);
    const unsubscribeVisibility = onDashboardVisibility(visibility);
    return () => {
      unsubscribe();
      clearTimeout(timer);
      unsubscribeVisibility();
    };
  }, [refresh]);
  useEffect(() => {
    document.documentElement.dataset.theme = theme;
    localStorage.setItem("mission-control-theme", theme);
  }, [theme]);
  useEffect(() => {
    if (!toast) return;
    const t = window.setTimeout(() => setToast(""), 4500);
    return () => clearTimeout(t);
  }, [toast]);
  useEffect(() => {
    const handler = (e: KeyboardEvent) => {
      if ((e.ctrlKey || e.metaKey) && e.key === "k") {
        e.preventDefault();
        searchRef.current?.focus();
      }
      if (e.key === "Escape") {
        closeDetail();
        setNewSource(null);
        setProjectDraft(null);
      }
      if (e.key === "Tab") {
        const modal = document.querySelector(".form-modal");
        const controls = modal?.querySelectorAll<HTMLElement>(
          "button:not(:disabled), input, select, textarea, a[href]",
        );
        if (controls?.length) {
          const first = controls[0],
            last = controls[controls.length - 1];
          if (e.shiftKey && document.activeElement === first) {
            e.preventDefault();
            last.focus();
          } else if (!e.shiftKey && document.activeElement === last) {
            e.preventDefault();
            first.focus();
          }
        }
      }
    };
    window.addEventListener("keydown", handler);
    return () => window.removeEventListener("keydown", handler);
  }, []);
  useEffect(() => {
    setOffset(0);
  }, [
    search,
    projectFilter,
    sourceFilter,
    personFilter,
    kindFilter,
    stateFilter,
    view,
  ]);
  const filters: EvidenceFilters = {
    search,
    project: projectFilter,
    source: sourceFilter,
    person: personFilter,
    kind: kindFilter,
    state: stateFilter,
  };
  const query = useEvidenceQuery(view, filters, offset, queryVersion);
  const projectIds = useMemo(
    () =>
      view === "Projects"
        ? (snapshot?.projects
            .filter((p) => p.isApproved === true)
            .map((p) => p.id) ?? [])
        : [],
    [view, snapshot?.projects],
  );
  const projectSummaries = useProjectSummaries(projectIds, queryVersion);

  const act = async (
    label: string,
    method: string,
    args?: unknown,
    message?: string,
  ) => {
    setBusy(label);
    try {
      const result = await rpc(method, args);
      await refresh();
      if (message) setToast(message);
      return result;
    } catch (e) {
      setError(errorText(e));
      return undefined;
    } finally {
      setBusy("");
    }
  };
  const go = (next: View) => {
    closeDetail();
    setView(next);
    setSelectedProject("");
    setSearch("");
    setProjectFilter("");
    setSourceFilter("");
    setPersonFilter("");
    setKindFilter("");
    setStateFilter("");
  };
  const setFilters = (change: Partial<EvidenceFilters>) => {
    if (change.search !== undefined) setSearch(change.search);
    if (change.project !== undefined) setProjectFilter(change.project);
    if (change.source !== undefined) setSourceFilter(change.source);
    if (change.person !== undefined) setPersonFilter(change.person);
    if (change.kind !== undefined) setKindFilter(change.kind);
    if (change.state !== undefined) setStateFilter(change.state);
  };
  const selectProject = (id: string) => {
    closeDetail();
    setView("Projects");
    setSelectedProject(id);
  };
  const configureSources = () => {
    go("Settings");
    setSettingsTab("Approved projects");
  };
  const projectFor = (id: string | null | undefined) =>
    snapshot?.projects.find((p) => p.id === id);
  const correctionFor = (id: string) =>
    snapshot?.corrections.find((c) => c.recordId === id);
  const approvedProjects =
    snapshot?.projects.filter((p) => p.isApproved === true) ?? [];
  const approvedSnapshot = snapshot
    ? { ...snapshot, projects: approvedProjects }
    : null;
  const visible = useMemo(
    () =>
      snapshot?.records.filter((r) => {
        const c = snapshot.corrections.find((x) => x.recordId === r.id);
        return (
          snapshot.projects.some(
            (p) => p.id === r.projectId && p.isApproved === true,
          ) &&
          !c?.dismissed &&
          (!c?.snoozedUntil || new Date(c.snoozedUntil).getTime() < Date.now())
        );
      }) ?? [],
    [snapshot],
  );
  const paused = snapshot?.monitoring.toLowerCase() === "paused";
  const catchingUp =
    /catch|reconcil|sync/i.test(snapshot?.monitoring ?? "") ||
    !!snapshot?.sources.some(
      (source) => source.enabled && /catch|reconcil/i.test(source.state),
    );
  const current = selectedProject ? projectFor(selectedProject) : undefined;
  const detailCorrection = loadedCorrection;

  const openRecord = async (id: string, conversation = false) => {
    const generation = ++recordGeneration.current;
    try {
      const [record, correction] = await Promise.all([
        rpc<EvidenceRecord>("record", { id }),
        rpc<Correction | null>("recordCorrection", { id }),
      ]);
      if (generation !== recordGeneration.current) return;
      setLoadedCorrection(correction ?? undefined);
      setSessionDetail(conversation);
      setDetail(record);
    } catch (e) {
      if (generation === recordGeneration.current) setError(errorText(e));
    }
  };
  const lookup: EvidenceLookup = {
    projectFor,
    correctionFor,
    openRecord,
    openSession: async (id, title) => {
      setSessionTitle(title ?? "Conversation");
      await openRecord(id, true);
    },
  };
  const saveCorrection = async (correction: Correction) => {
    const generation = recordGeneration.current;
    setBusy("correction");
    try {
      await rpc("correction", { correction });
      await refresh();
      if (generation === recordGeneration.current)
        await openRecord(correction.recordId, sessionDetail);
    } finally {
      setBusy("");
    }
  };
  return (
    <div className="app-shell">
      <Sidebar
        snapshot={approvedSnapshot!}
        version={version}
        view={view}
        selectedProject={selectedProject}
        paused={paused}
        catchingUp={catchingUp}
        busy={busy}
        go={go}
        onSelectProject={selectProject}
        onToggleMonitoring={() =>
          act(
            "monitoring",
            "control",
            { action: paused ? "resume" : "pause" },
            paused
              ? "Collection resumed. Available history is being reconciled."
              : "All local monitoring paused.",
          )
        }
      />
      <div className="workspace">
        <Topbar
          view={view}
          search={search}
          searchRef={searchRef}
          onSearch={(value) => {
            setSearch(value);
            if (view !== "Search") setView("Search");
          }}
          theme={theme}
          onToggleTheme={() => setTheme(theme === "light" ? "dark" : "light")}
        />
        <main className="main-content" id="main-content">
          {error && (
            <div className="notice error" role="alert">
              <span>{error}</span>
              <button
                onClick={() => {
                  setError("");
                  refresh().catch((e) => setError(errorText(e)));
                }}
              >
                Retry
              </button>
              <button
                className="icon-button"
                aria-label="Dismiss error"
                onClick={() => setError("")}
              >
                <X size={16} />
              </button>
            </div>
          )}
          {loading ? (
            <div className="loading-state" role="status">
              <LoaderCircle className="spin" size={25} />
              <h2>Gathering your workspace</h2>
              <p>Reading locally stored evidence.</p>
            </div>
          ) : !snapshot ? (
            <Empty title="Your workspace is unavailable">
              {error || "The local collector did not respond."}
              <button
                className="button primary"
                onClick={() => {
                  setLoading(true);
                  refresh()
                    .catch((e) => setError(errorText(e)))
                    .finally(() => setLoading(false));
                }}
              >
                Try again
              </button>
            </Empty>
          ) : (
            <>
              <WorkspaceNotices
                snapshot={approvedSnapshot!}
                paused={paused}
                busy={busy}
                act={act}
                onOpenSettings={() => go("Settings")}
                onReviewSources={configureSources}
              />

              {view === "Overview" && (
                <OverviewView
                  snapshot={approvedSnapshot!}
                  visible={visible}
                  paused={paused}
                  busy={busy}
                  lookup={lookup}
                  act={act}
                  onOpenView={go}
                  onSelectProject={selectProject}
                  onConfigureSources={configureSources}
                />
              )}

              {view === "Projects" && (
                <ProjectsView
                  snapshot={approvedSnapshot!}
                  current={current}
                  visible={visible}
                  projectSummaries={projectSummaries}
                  queryVersion={queryVersion}
                  lookup={lookup}
                  onSelectProject={setSelectedProject}
                  onSearchProject={(id) => {
                    setProjectFilter(id);
                    setView("Timeline");
                  }}
                />
              )}

              {["Sessions", "Search", "Timeline"].includes(view) && (
                <EvidenceListView
                  view={view}
                  snapshot={approvedSnapshot!}
                  filters={filters}
                  onFilter={setFilters}
                  offset={offset}
                  onOffset={setOffset}
                  query={query}
                  lookup={lookup}
                  onRetry={() => setQueryVersion((v) => v + 1)}
                />
              )}

              {view === "Settings" && (
                <SettingsView
                  snapshot={approvedSnapshot!}
                  paused={paused}
                  busy={busy}
                  settingsTab={settingsTab}
                  setSettingsTab={setSettingsTab}
                  setError={setError}
                  setToast={setToast}
                  setNewSource={setNewSource}
                  setProjectDraft={setProjectDraft}
                  queryVersion={queryVersion}
                  refresh={refresh}
                  act={act}
                />
              )}
              <footer className="workspace-footer">
                <span>
                  <ShieldCheck size={12} />
                  Local evidence. Clear provenance. Your pace.
                </span>
                <span>
                  <span
                    title="Version reported by the running desktop application"
                    data-app-version={version}
                  >
                    Mission Control {version ? `v${version}` : "Desktop"}
                  </span>
                </span>
              </footer>
            </>
          )}
        </main>
      </div>

      {detail && (
        <div className="modal-backdrop" onClick={closeDetail}>
          <EvidenceDrawer
            sessionView={sessionDetail}
            sessionTitle={sessionTitle}
            record={detail}
            correction={detailCorrection}
            projects={approvedProjects}
            onClose={closeDetail}
            onSave={saveCorrection}
            onSessionProject={async (projectId) => {
              const generation = recordGeneration.current;
              setBusy("session-project");
              try {
                await rpc("sessionProject", {
                  sourceId: detail.sourceId,
                  sessionId: detail.sessionId,
                  projectId,
                });
                await refresh();
                if (generation === recordGeneration.current)
                  await openRecord(detail.id, sessionDetail);
              } finally {
                setBusy("");
              }
            }}
            onUndo={async () => {
              const generation = recordGeneration.current;
              setBusy("undo");
              try {
                await rpc("undoCorrection", { recordId: detail.id });
                await refresh();
                if (generation === recordGeneration.current)
                  await openRecord(detail.id, sessionDetail);
              } finally {
                setBusy("");
              }
            }}
            busy={!!busy}
          />
        </div>
      )}
      {newSource && (
        <SourceDialog
          source={newSource}
          busy={busy}
          onChange={setNewSource}
          onClose={() => setNewSource(null)}
          act={act}
        />
      )}
      {projectDraft && (
        <ProjectDialog
          project={projectDraft}
          busy={busy}
          onChange={setProjectDraft}
          onClose={() => setProjectDraft(null)}
          act={act}
        />
      )}
      {toast && <Toast message={toast} onDismiss={() => setToast("")} />}
    </div>
  );
}

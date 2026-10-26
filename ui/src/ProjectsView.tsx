import { ArrowRight, ChevronLeft, MessageSquare, Search } from "lucide-react";
import ProjectRepositories, {
  ProjectOrigin,
  RepositoryCount,
} from "./ProjectRepositories";
import { ProjectRecentEvidence, type ProjectSummary } from "./ProjectActivity";
import type { AppSnapshot, EvidenceRecord, ProjectInfo } from "./bridge";
import {
  RecordRow,
  SectionHeader,
  sessionGroups,
  type EvidenceLookup,
} from "./EvidenceRows";
import { Empty, relative } from "./presentation";
export default function ProjectsView({
  snapshot,
  current,
  visible,
  projectSummaries,
  queryVersion,
  lookup,
  onSelectProject,
  onSearchProject,
}: {
  snapshot: AppSnapshot;
  current: ProjectInfo | undefined;
  visible: EvidenceRecord[];
  projectSummaries: Record<string, ProjectSummary | undefined>;
  queryVersion: number;
  lookup: EvidenceLookup;
  onSelectProject: (id: string) => void;
  onSearchProject: (id: string) => void;
}) {
  return (
    <>
      <div className="page-heading">
        <div>
          {current && (
            <button
              className="text-button back-button"
              onClick={() => onSelectProject("")}
            >
              <ChevronLeft size={14} />
              All approved projects
            </button>
          )}
          <div className="eyebrow">APPROVED DEVELOPER MEMORY</div>
          <h1>{current?.name || "Your projects"}</h1>
          <p>
            {current?.description ||
              "Local conversations grouped by their observed workspace and repositories."}
          </p>
        </div>
      </div>
      {!current ? (
        <div className="projects-grid">
          {snapshot.projects.map((p) => {
            const records = visible.filter((r) => r.projectId === p.id);
            return (
              <button
                className="project-card"
                key={p.id}
                onClick={() => onSelectProject(p.id)}
              >
                <span className="project-card-top">
                  <span
                    className="project-monogram large"
                    style={{ color: p.color, background: `${p.color}16` }}
                  >
                    {p.name.slice(0, 1)}
                  </span>
                  <ArrowRight size={18} />
                </span>
                <span className="project-name-line">
                  <h2>{p.name}</h2>
                  <ProjectOrigin project={p} />
                </span>
                <p>{p.description || p.rootPath}</p>
                <RepositoryCount project={p} />
                <span className="project-card-bottom">
                  <span>
                    <MessageSquare size={13} />
                    {projectSummaries[p.id]?.sessions ??
                      sessionGroups(records).length}{" "}
                    sessions
                  </span>
                  <span>Approved</span>
                </span>
                <span className="project-last">
                  Last observed{" "}
                  {relative(
                    projectSummaries[p.id]?.lastObserved ??
                      records[0]?.timestamp,
                  )}
                </span>
              </button>
            );
          })}
          {!snapshot.projects.length && (
            <Empty title="No approved projects">
              Review discovered project directories in Settings before indexing
              their conversations.
            </Empty>
          )}
        </div>
      ) : (
        <div className="project-detail-grid">
          <section className="panel">
            <SectionHeader label="Recent conversation history" />
            <ProjectRecentEvidence
              projectId={current.id}
              version={queryVersion}
              renderRecord={(r) => (
                <RecordRow key={r.id} record={r} lookup={lookup} />
              )}
            />
          </section>
          <section className="panel project-context">
            <SectionHeader label="Project context" />
            <ProjectOrigin project={current} />
            <label>APPROVED LOCAL DIRECTORY</label>
            <code>{current.rootPath}</code>
            <ProjectRepositories project={current} />
            <p>
              Sessions in this folder and its repositories belong together.
              Repository metadata is read locally; live GitHub and Slack access
              belongs to their official MCP servers in your AI host.
            </p>
            <button
              className="button secondary"
              onClick={() => onSearchProject(current.id)}
            >
              Search project history <Search size={14} />
            </button>
          </section>
        </div>
      )}
    </>
  );
}

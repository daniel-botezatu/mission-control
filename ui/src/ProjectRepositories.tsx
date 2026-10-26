import { GitBranch } from "lucide-react";
import type { ProjectInfo } from "./bridge";
import "./project-repositories.css";

export function ProjectOrigin({ project }: { project: ProjectInfo }) {
  return (
    <span className="project-origin">
      {project.autoDiscovered ? "Automatic" : "Manual"}
    </span>
  );
}

export function RepositoryCount({ project }: { project: ProjectInfo }) {
  const count = project.repositories?.length ?? 0;
  if (!count) return null;
  return (
    <span className="project-repository-count">
      <GitBranch size={12} />
      {count} {count === 1 ? "repository" : "repositories"}
    </span>
  );
}

export default function ProjectRepositories({
  project,
}: {
  project: ProjectInfo;
}) {
  if (!project.repositories?.length) return null;
  return (
    <ul
      className="project-repositories"
      aria-label={`Repositories in ${project.name}`}
    >
      {project.repositories.map((repository) => (
        <li key={repository.localRoot}>
          <GitBranch size={12} aria-hidden="true" />
          <span>
            <strong>
              {repository.gitHubRepositories.length
                ? repository.gitHubRepositories.join(", ")
                : "Local Git repository"}
            </strong>
            <code>{repository.localRoot}</code>
          </span>
        </li>
      ))}
    </ul>
  );
}

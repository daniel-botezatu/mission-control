using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace MissionControl.Core;

public sealed partial class WorkspaceStore
{
    private static readonly StringComparison ProjectPathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private List<ProjectRoot>? projectRoots;

    public IReadOnlyList<string> GetObservedWorkingDirectories(int limit = 512)
    {
        lock (gate)
        {
            using var command = Command("SELECT working_directory FROM evidence WHERE source_kind IN ('claude','codex') AND working_directory IS NOT NULL AND working_directory<>'' GROUP BY working_directory ORDER BY MAX(changed_at) DESC,working_directory LIMIT $limit", ("$limit", Math.Clamp(limit, 1, 1024)));
            using var reader = command.ExecuteReader();
            List<string> result = [];
            while (reader.Read()) result.Add(reader.GetString(0));
            return result;
        }
    }

    /// <summary>Persists only local metadata. Existing account settings and explicit assignments remain authoritative.</summary>
    public bool ApplyProjectDiscovery(GitWorkspaceDiscoveryResult discovery)
    {
        var root = NormalizeDiscoveryPath(discovery.RootPath);
        if (root is null || BroadDiscoveryRoot(root)) return false;
        if (discovery.Repositories.Count > GitWorkspaceDiscovery.MaximumRepositories)
            throw new ArgumentException("Project discovery exceeded its repository limit.", nameof(discovery));
        lock (gate)
        {
            using var transaction = connection.BeginTransaction();
            projectRoots = null;
            try
            {
                var changed = false;
                var project = MatchProjectUnsafe(root, transaction);
                if (project is null)
                {
                    var identity = OperatingSystem.IsWindows() ? root.ToUpperInvariant() : root;
                    project = "auto-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..24].ToLowerInvariant();
                    changed |= ProjectExecute(transaction, "INSERT INTO projects(id,name,color,description,root_path,origin) VALUES($id,$name,'#7c8cf5','Discovered from local session working directories.',$root,'auto') ON CONFLICT(id) DO NOTHING",
                        ("$id", project), ("$name", Redactor.Clean(discovery.Name, 200)), ("$root", Path.TrimEndingDirectorySeparator(Path.GetFullPath(discovery.RootPath)))) > 0;
                }

                // A newly observed parent workspace absorbs automatic child groups. Manual groups and
                // saved manual record/session/source choices are never rewritten by this operation.
                var automaticChildren = ReadProjectRoots(transaction).Where(p => p.Priority == 2 && !IsProjectApprovedUnsafe(p.Project, transaction) && p.Project != project && IsWithin(p.Root, root)).Select(p => p.Project).Distinct().ToArray();
                foreach (var child in automaticChildren)
                {
                    changed |= ProjectExecute(transaction, "UPDATE project_workspaces SET project_id=$project WHERE project_id=$child; UPDATE project_repositories SET project_id=$project WHERE project_id=$child", ("$project", project), ("$child", child)) > 0;
                }
                changed |= ProjectExecute(transaction, "INSERT INTO project_workspaces(root_path,project_id) VALUES($root,$project) ON CONFLICT(root_path) DO UPDATE SET project_id=excluded.project_id WHERE project_id IS NOT excluded.project_id", ("$root", root), ("$project", project)) > 0;
                projectRoots = null;

                var observedCheckouts = new HashSet<string>(StringComparer.Ordinal);
                foreach (var checkout in discovery.Repositories)
                {
                    var localRoot = NormalizeDiscoveryPath(checkout.CheckoutPath);
                    if (localRoot is null || !IsWithin(localRoot, root)) continue;
                    observedCheckouts.Add(localRoot);
                    var owner = ReadProjectRoots(transaction).Where(p => p.Priority == 0 && IsWithin(localRoot, p.Root)).OrderByDescending(p => p.Root.Length).FirstOrDefault()?.Project ?? project;
                    changed |= ProjectExecute(transaction, "INSERT INTO project_repositories(local_root,project_id) VALUES($root,$project) ON CONFLICT(local_root) DO UPDATE SET project_id=excluded.project_id WHERE project_id IS NOT excluded.project_id", ("$root", localRoot), ("$project", owner)) > 0;
                    // The key is case-folded on Windows; keep the folder's own spelling for display.
                    ProjectExecute(transaction, "UPDATE project_repositories SET display_root=$display WHERE local_root=$root AND display_root IS NOT $display", ("$root", localRoot), ("$display", DisplayPath(checkout.CheckoutPath)));
                    var aliases = checkout.GitHubRepositories.Take(64).Select(NormalizeRepositoryIdentity).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToArray();
                    List<string> oldAliases = [];
                    using (var command = Command("SELECT github_repository FROM project_repository_remotes WHERE local_root=$root ORDER BY github_repository", ("$root", localRoot)))
                    {
                        command.Transaction = transaction;
                        using var reader = command.ExecuteReader();
                        while (reader.Read()) oldAliases.Add(reader.GetString(0));
                    }
                    if (!oldAliases.SequenceEqual(aliases, StringComparer.OrdinalIgnoreCase))
                    {
                        ProjectExecute(transaction, "DELETE FROM project_repository_remotes WHERE local_root=$root", ("$root", localRoot));
                        foreach (var alias in aliases)
                            ProjectExecute(transaction, "INSERT INTO project_repository_remotes(local_root,github_repository) VALUES($root,$alias)", ("$root", localRoot), ("$alias", alias));
                        changed = true;
                    }
                }
                // Linked worktrees still attribute their sessions, but are not repositories of the project.
                foreach (var worktree in (discovery.LinkedWorktrees ?? []).Take(GitWorkspaceDiscovery.MaximumRepositories))
                {
                    if (NormalizeDiscoveryPath(worktree) is not { } worktreeRoot) continue;
                    changed |= ProjectExecute(transaction, "DELETE FROM project_repositories WHERE local_root=$root", ("$root", worktreeRoot)) > 0;
                    var owner = ReadProjectRoots(transaction).Where(p => p.Priority == 0 && IsWithin(worktreeRoot, p.Root)).OrderByDescending(p => p.Root.Length).FirstOrDefault()?.Project ?? project;
                    changed |= ProjectExecute(transaction, "INSERT INTO project_workspaces(root_path,project_id) VALUES($root,$project) ON CONFLICT(root_path) DO UPDATE SET project_id=excluded.project_id WHERE project_id IS NOT excluded.project_id", ("$root", worktreeRoot), ("$project", owner)) > 0;
                }
                projectRoots = null;
                // A complete scan can establish that a checkout disappeared. A permission error or
                // scan limit cannot, so an incomplete result retains previously established mappings.
                if (discovery.Gaps.Count == 0)
                {
                    List<string> knownCheckouts = [];
                    using (var command = Command("SELECT local_root FROM project_repositories"))
                    {
                        command.Transaction = transaction;
                        using var reader = command.ExecuteReader();
                        while (reader.Read()) knownCheckouts.Add(reader.GetString(0));
                    }
                    foreach (var localRoot in knownCheckouts.Where(path => IsWithin(path, root) && !observedCheckouts.Contains(path)))
                        changed |= ProjectExecute(transaction, "DELETE FROM project_repositories WHERE local_root=$root", ("$root", localRoot)) > 0;
                }
                projectRoots = null;
                if (changed)
                {
                    ReassignProjectEvidenceUnsafe(transaction);
                    foreach (var child in automaticChildren)
                        ProjectExecute(transaction, "DELETE FROM projects WHERE id=$id AND origin='auto' AND is_approved=0 AND NOT EXISTS(SELECT 1 FROM evidence WHERE project_id=$id) AND NOT EXISTS(SELECT 1 FROM corrections WHERE project_id=$id) AND NOT EXISTS(SELECT 1 FROM session_corrections WHERE project_id=$id) AND NOT EXISTS(SELECT 1 FROM project_workspaces WHERE project_id=$id) AND NOT EXISTS(SELECT 1 FROM project_repositories WHERE project_id=$id)", ("$id", child));
                    Invalidate(transaction);
                }
                transaction.Commit();
                return changed;
            }
            finally { projectRoots = null; }
        }
    }

    /// <summary>
    /// Repairs automatic grouping saved by earlier versions: linked worktrees listed as repositories, and agent
    /// scratch folders listed as projects. Manual projects and explicitly assigned projects are never removed.
    /// </summary>
    public bool NormalizeAutomaticProjects(IGitWorkspaceDiscovery policy)
    {
        lock (gate)
        {
            List<(string Key, string Path, string Project)> repositories = [];
            using (var command = Command("SELECT local_root,COALESCE(display_root,local_root),project_id FROM project_repositories"))
            using (var reader = command.ExecuteReader())
                while (reader.Read()) repositories.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            List<string> workspaces = [];
            using (var command = Command("SELECT root_path FROM project_workspaces"))
            using (var reader = command.ExecuteReader())
                while (reader.Read()) workspaces.Add(reader.GetString(0));
            List<(string Id, string Root)> automatic = [];
            using (var command = Command("SELECT id,root_path FROM projects WHERE origin='auto' AND root_path IS NOT NULL AND root_path<>''"))
            using (var reader = command.ExecuteReader())
                while (reader.Read()) automatic.Add((reader.GetString(0), reader.GetString(1)));

            using var transaction = connection.BeginTransaction();
            projectRoots = null;
            try
            {
                var changed = false;
                foreach (var repository in repositories)
                {
                    if (policy.IsExcludedWorkspace(repository.Path))
                        changed |= ProjectExecute(transaction, "DELETE FROM project_repositories WHERE local_root=$root", ("$root", repository.Key)) > 0;
                    else if (policy.IsLinkedWorktree(repository.Path))
                    {
                        changed |= ProjectExecute(transaction, "DELETE FROM project_repositories WHERE local_root=$root", ("$root", repository.Key)) > 0;
                        ProjectExecute(transaction, "INSERT INTO project_workspaces(root_path,project_id) VALUES($root,$project) ON CONFLICT(root_path) DO NOTHING", ("$root", repository.Key), ("$project", repository.Project));
                    }
                }
                foreach (var workspace in workspaces.Where(policy.IsExcludedWorkspace))
                    changed |= ProjectExecute(transaction, "DELETE FROM project_workspaces WHERE root_path=$root", ("$root", workspace)) > 0;
                foreach (var project in automatic.Where(p => policy.IsExcludedWorkspace(p.Root)))
                    changed |= ProjectExecute(transaction, "DELETE FROM projects WHERE id=$id AND origin='auto' AND is_approved=0 AND NOT EXISTS(SELECT 1 FROM corrections WHERE project_id=$id) AND NOT EXISTS(SELECT 1 FROM session_corrections WHERE project_id=$id)", ("$id", project.Id)) > 0;
                if (changed)
                {
                    ReassignProjectEvidenceUnsafe(transaction);
                    Invalidate(transaction);
                }
                transaction.Commit();
                return changed;
            }
            finally { projectRoots = null; }
        }
    }

    private IReadOnlyList<ProjectInfo> ReadProjects()
    {
        List<ProjectInfo> result = [];
        using (var command = Command("SELECT id,name,color,description,root_path,origin,is_approved,approved_at FROM projects ORDER BY name,id"))
        using (var reader = command.ExecuteReader())
            while (reader.Read()) result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), NullableString(reader, 4) is { } root && Path.IsPathFullyQualified(root) ? DisplayPath(root) : NullableString(reader, 4), reader.GetString(5) == "auto", IsApproved: reader.GetBoolean(6), ApprovedAt: NullableString(reader, 7)));
        var repositories = new Dictionary<string, Dictionary<string, List<string>>>(StringComparer.Ordinal);
        using (var command = Command("SELECT p.project_id,COALESCE(p.display_root,p.local_root),r.github_repository FROM project_repositories p LEFT JOIN project_repository_remotes r ON r.local_root=p.local_root ORDER BY p.local_root,r.github_repository"))
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
            {
                var project = reader.GetString(0); var root = reader.GetString(1);
                if (!repositories.TryGetValue(project, out var roots)) repositories[project] = roots = new(StringComparer.Ordinal);
                if (!roots.TryGetValue(root, out var aliases)) roots[root] = aliases = [];
                if (!reader.IsDBNull(2)) aliases.Add(reader.GetString(2));
            }
        }
        return result.Select(p => p with { Repositories = repositories.TryGetValue(p.Id, out var roots) ? roots.Select(r => new ProjectRepository(r.Key, r.Value)).ToArray() : [] }).ToArray();
    }

    private EvidenceRecord AssignImportedProject(EvidenceRecord record, SqliteTransaction transaction)
        => record with { ProjectId = MatchProjectUnsafe(record.WorkingDirectory, transaction) };

    private string? MatchProjectUnsafe(string? cwd, SqliteTransaction? transaction)
    {
        if (string.IsNullOrWhiteSpace(cwd)) return null;
        projectRoots ??= ReadProjectRoots(transaction);
        return projectRoots.Where(p => PrivacyPathWithin(cwd, p.Root)).OrderBy(p => p.Priority).ThenByDescending(p => p.Root.Length).ThenBy(p => p.Project, StringComparer.Ordinal).FirstOrDefault()?.Project;
    }

    private List<ProjectRoot> ReadProjectRoots(SqliteTransaction? transaction)
    {
        using var command = Command("SELECT id,root_path,CASE WHEN origin='manual' THEN 0 ELSE 2 END FROM projects WHERE root_path IS NOT NULL AND root_path<>'' UNION ALL SELECT project_id,root_path,1 FROM project_workspaces UNION ALL SELECT project_id,local_root,1 FROM project_repositories");
        command.Transaction = transaction;
        using var reader = command.ExecuteReader();
        List<ProjectRoot> result = [];
        while (reader.Read()) result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetInt32(2)));
        return result;
    }

    private void ReassignProjectEvidenceUnsafe(SqliteTransaction transaction)
    {
        projectRoots = null;
        List<(string Root, string Project)> repositories = [];
        using (var command = Command("SELECT local_root,project_id FROM project_repositories"))
        {
            command.Transaction = transaction;
            using var reader = command.ExecuteReader();
            while (reader.Read()) repositories.Add((reader.GetString(0), reader.GetString(1)));
        }
        foreach (var repository in repositories)
        {
            var project = MatchProjectUnsafe(repository.Root, transaction);
            if (project is not null && project != repository.Project)
                ProjectExecute(transaction, "UPDATE project_repositories SET project_id=$project WHERE local_root=$root", ("$root", repository.Root), ("$project", project));
        }
        projectRoots = null;
        List<string> directories = [];
        using (var command = Command("SELECT DISTINCT working_directory FROM evidence WHERE source_kind IN ('claude','codex') AND working_directory IS NOT NULL"))
        {
            command.Transaction = transaction;
            using var reader = command.ExecuteReader();
            while (reader.Read()) directories.Add(reader.GetString(0));
        }
        foreach (var cwd in directories)
            ProjectExecute(transaction, "UPDATE evidence SET project_id=$project WHERE working_directory=$cwd AND source_kind IN ('claude','codex') AND project_id IS NOT $project", ("$cwd", cwd), ("$project", MatchProjectUnsafe(cwd, transaction)));
        RebuildApprovedSearchIndexes(transaction);
    }

    private int ProjectExecute(SqliteTransaction transaction, string sql, params (string, object?)[] parameters)
    {
        using var command = Command(sql, parameters);
        command.Transaction = transaction;
        return command.ExecuteNonQuery();
    }

    private static string? NormalizeRepositoryIdentity(string value) => GitWorkspaceDiscovery.NormalizeGitHubRepository(value.Contains("://", StringComparison.Ordinal) || value.StartsWith("git@", StringComparison.Ordinal) ? value : "https://github.com/" + value);

    private static bool IsWithin(string candidate, string parent)
    {
        candidate = candidate.Replace('\\', '/').TrimEnd('/');
        parent = parent.Replace('\\', '/').TrimEnd('/');
        return candidate.Equals(parent, ProjectPathComparison) || candidate.StartsWith(parent + "/", ProjectPathComparison);
    }

    private static string? NormalizeDiscoveryPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 32768 || path.Contains('\0') || !Path.IsPathFullyQualified(path) || path.StartsWith("\\\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal)) return null;
        try
        {
            var canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            return OperatingSystem.IsWindows() ? canonical.ToUpperInvariant() : canonical;
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException) { return null; }
    }

    /// <summary>The folder's own spelling; transcripts can record a lower-case Windows drive letter.</summary>
    private static string DisplayPath(string path)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        return OperatingSystem.IsWindows() && full.Length >= 2 && full[1] == ':' ? char.ToUpperInvariant(full[0]) + full[1..] : full;
    }

    private static bool BroadDiscoveryRoot(string path) =>
        path.Equals(Path.GetPathRoot(path), ProjectPathComparison)
        || new[] { Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.DesktopDirectory }.Select(Environment.GetFolderPath).Where(p => !string.IsNullOrEmpty(p)).Any(p => path.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(p)), ProjectPathComparison));

    private sealed record ProjectRoot(string Project, string Root, int Priority);
}

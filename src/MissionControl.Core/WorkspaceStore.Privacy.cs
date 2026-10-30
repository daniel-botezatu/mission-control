using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace MissionControl.Core;

public sealed partial class WorkspaceStore
{
    // This is part of every public retrieval path, including read-only MCP connections.
    // The stored project label alone is never proof of permission.
    private const string ApprovalPredicate = """
        r.source_kind IN ('claude','codex') AND r.kind IN ('message','lifecycle')
        AND (r.author='user' OR (r.author='assistant' AND CASE WHEN json_valid(r.data_json) THEN json_extract(r.data_json,'$.phase')='final' ELSE 0 END)
            OR (r.kind='lifecycle' AND r.author='system' AND CASE WHEN json_valid(r.data_json) THEN json_extract(r.data_json,'$.schema')='memory-v1' ELSE 0 END))
        AND EXISTS(SELECT 1 FROM projects approved WHERE approved.id=r.project_id AND approved.is_approved=1
            AND (mc_path_within(r.working_directory,approved.root_path)=1
                OR EXISTS(SELECT 1 FROM project_workspaces pw WHERE pw.project_id=approved.id AND mc_path_within(r.working_directory,pw.root_path)=1)
                OR EXISTS(SELECT 1 FROM project_repositories pr WHERE pr.project_id=approved.id AND mc_path_within(r.working_directory,pr.local_root)=1)))
        """;

    public bool CanIndexWorkingDirectory(string? cwd)
    {
        lock (gate)
        {
            var project = MatchProjectUnsafe(cwd, null);
            return project is not null && IsProjectApprovedUnsafe(project, null) && NormalizePrivacyPath(cwd) is not null;
        }
    }

    private bool IsProjectApprovedUnsafe(string project, SqliteTransaction? transaction)
    {
        using var command = Command("SELECT is_approved FROM projects WHERE id=$project", ("$project", project));
        command.Transaction = transaction;
        return command.ExecuteScalar() is long approved && approved == 1;
    }

    private bool CanIndexRecordUnsafe(EvidenceRecord record, SqliteTransaction transaction)
    {
        if (record.SourceKind is not ("claude" or "codex") || record.ProjectId is null
            || !IsProjectApprovedUnsafe(record.ProjectId, transaction) || NormalizePrivacyPath(record.WorkingDirectory) is null) return false;
        if (record.Kind == "message" && record.Author == "user") return true;
        try
        {
            using var document = JsonDocument.Parse(record.DataJson ?? "{}");
            var data = document.RootElement;
            return record.Kind == "message" && record.Author == "assistant"
                    && data.TryGetProperty("phase", out var phase) && phase.GetString() == "final"
                || record.Kind == "lifecycle" && record.Author == "system"
                    && data.TryGetProperty("schema", out var schema) && schema.GetString() == "memory-v1";
        }
        catch (JsonException) { return false; }
    }

    /// <summary>Changes the allowlist atomically. Revocation removes all retained payload and annotations for that project.</summary>
    public int SetProjectApproval(string projectId, bool approved)
    {
        lock (gate)
        {
            var project = GetProjects().FirstOrDefault(p => p.Id == projectId) ?? throw new ArgumentException("Unknown project.", nameof(projectId));
            if (approved && (NormalizePrivacyPath(project.RootPath) is null || BroadDiscoveryRoot(project.RootPath!)))
                throw new ArgumentException("Approve a specific absolute project folder, not a drive, Desktop, or home folder.", nameof(projectId));
            using var transaction = connection.BeginTransaction();
            var removed = 0;
            if (!approved)
            {
                removed = ProjectExecute(transaction, "DELETE FROM evidence WHERE project_id=$project", ("$project", projectId));
                ProjectExecute(transaction, "DELETE FROM session_corrections WHERE project_id=$project OR NOT EXISTS(SELECT 1 FROM evidence WHERE evidence.source_id=session_corrections.source_id AND evidence.session_id=session_corrections.session_id)", ("$project", projectId));
            }
            ProjectExecute(transaction, "UPDATE projects SET is_approved=$approved,approved_at=$at WHERE id=$project", ("$project", projectId), ("$approved", approved), ("$at", approved ? DateTimeOffset.UtcNow.ToString("O") : null));
            // Checkpoints can hold pending final replies. Clearing them both removes pending excluded
            // text and guarantees available history is replayed after a later approval.
            ProjectExecute(transaction, "DELETE FROM checkpoints");
            projectRoots = null;
            RebuildApprovedSearchIndexes(transaction);
            Invalidate(transaction);
            transaction.Commit();
            Execute("PRAGMA wal_checkpoint(TRUNCATE); PRAGMA incremental_vacuum");
            return removed;
        }
    }

    public int PurgeExcludedHistory()
    {
        lock (gate)
        {
            using var transaction = connection.BeginTransaction();
            var removed = ProjectExecute(transaction, "DELETE FROM evidence WHERE id IN (SELECT r.id FROM evidence r WHERE NOT (" + ApprovalPredicate + "))");
            ProjectExecute(transaction, "DELETE FROM session_corrections WHERE NOT EXISTS(SELECT 1 FROM evidence r WHERE r.source_id=session_corrections.source_id AND r.session_id=session_corrections.session_id); DELETE FROM checkpoints");
            RebuildApprovedSearchIndexes(transaction);
            Invalidate(transaction); transaction.Commit();
            Execute("PRAGMA wal_checkpoint(TRUNCATE); PRAGMA incremental_vacuum");
            return removed;
        }
    }

    public void ClearAllHistory()
    {
        lock (gate)
        {
            using var transaction = connection.BeginTransaction();
            ProjectExecute(transaction, "DELETE FROM evidence; DELETE FROM corrections; DELETE FROM session_corrections; DELETE FROM checkpoints");
            RebuildApprovedSearchIndexes(transaction); Invalidate(transaction); transaction.Commit();
            Execute("PRAGMA wal_checkpoint(TRUNCATE); PRAGMA incremental_vacuum");
        }
    }

    private string? SanitizeCheckpointMetadata(string? json, SqliteTransaction transaction)
    {
        if (json is null) return null;
        try
        {
            var data = System.Text.Json.Nodes.JsonNode.Parse(json) as System.Text.Json.Nodes.JsonObject;
            if (data is null) return null;
            var cwd = data.FirstOrDefault(p => p.Key.Equals("cwd", StringComparison.OrdinalIgnoreCase)).Value?.GetValue<string>();
            var project = MatchProjectUnsafe(cwd, transaction);
            foreach (var property in data.Select(p => p.Key).Where(key => key.Equals("pendingAssistant", StringComparison.OrdinalIgnoreCase) || key.Equals("lastUpdateMessage", StringComparison.OrdinalIgnoreCase)).ToArray())
            {
                var pending = data[property] as System.Text.Json.Nodes.JsonObject;
                var pendingCwd = pending?.FirstOrDefault(p => p.Key.Equals("cwd", StringComparison.OrdinalIgnoreCase)).Value?.GetValue<string>();
                var pendingProject = MatchProjectUnsafe(pendingCwd, transaction);
                if (project is null || !IsProjectApprovedUnsafe(project, transaction)
                    || pendingProject != project || !PrivacyPathWithin(pendingCwd, cwd)) data.Remove(property);
            }
            // An interrupted turn's last update, pending shell calls and tool facts are kept only for approved projects.
            if (project is null || !IsProjectApprovedUnsafe(project, transaction))
                foreach (var property in data.Select(p => p.Key).Where(key => key.Equals("lastUpdateMessage", StringComparison.OrdinalIgnoreCase) || key.Equals("pendingCalls", StringComparison.OrdinalIgnoreCase) || key.Equals("pendingFacts", StringComparison.OrdinalIgnoreCase)).ToArray())
                    data.Remove(property);
            // Persist parser state only, never arbitrary payload fields supplied to a checkpoint.
            var fields = new HashSet<string>(["parserVersion","replayBelow","lookbackDays","includeSubagents","session","cwd","branch","version","timestamp","gap","approvedHistory","subagent","turn","finalEmitted","turnCompleted","pendingAssistant","discarding","waitingForTail","creationTicks","compressedLength","compressedTicks","historyBaseThread","historyBaseOffset","historyBaseOutsideLookback","historyBaseRead","lastUpdateMessage","pendingCalls","pendingFacts"], StringComparer.OrdinalIgnoreCase);
            foreach (var key in data.Select(p => p.Key).Where(key => !fields.Contains(key)).ToArray()) data.Remove(key);
            // Parser state must survive a long pending final. The generic
            // 12 KiB evidence-metadata limit used to replace this entire object
            // with {truncated:true}, losing the version and rewinding forever.
            return SafeJson(data.ToJsonString(), MemoryLimits.MessageCharacters * 6 + 16384, MemoryLimits.MessageCharacters);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException) { return null; }
    }

    // Reset at every SQL statement: repeated rows share path validation, but no
    // filesystem/approval decision survives into the next request.
    private readonly Dictionary<string, string?> statementPaths = new(StringComparer.Ordinal);
    private readonly Dictionary<(string?, string?), bool> statementPathPairs = [];
    private void RegisterPrivacyFunctions()
    {
        connection.CreateFunction<string?, string?, bool>("mc_path_within", StatementPathWithin, isDeterministic: true);
    }

    private bool StatementPathWithin(string? candidate, string? parent)
    {
        if (statementPathPairs.TryGetValue((candidate, parent), out var cached)) return cached;
        string? Normalized(string? value)
        {
            if (value is null) return null;
            if (statementPaths.TryGetValue(value, out var normalized)) return normalized;
            normalized = NormalizePrivacyPath(value);
            if (statementPaths.Count < 4096) statementPaths[value] = normalized;
            return normalized;
        }
        var path = Normalized(candidate); var root = Normalized(parent);
        var comparison = path is { Length: > 1 } && path[1] == ':' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var result = path is not null && root is not null && (path.Equals(root, comparison) || path.StartsWith(root.TrimEnd('/') + "/", comparison));
        if (statementPathPairs.Count < 8192) statementPathPairs[(candidate, parent)] = result;
        return result;
    }

    private static bool PrivacyPathWithin(string? candidate, string? parent)
    {
        // Reject unrelated roots lexically before touching the filesystem.
        // Otherwise matching one cwd against dozens of repositories performs
        // hundreds of redundant ancestor checks for every transcript envelope.
        var path = NormalizePrivacyPath(candidate, validateFilesystem: false); var root = NormalizePrivacyPath(parent, validateFilesystem: false);
        if (path is null || root is null) return false;
        var comparison = path.Length > 1 && path[1] == ':' ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!path.Equals(root, comparison) && !path.StartsWith(root.TrimEnd('/') + "/", comparison)) return false;
        return NormalizePrivacyPath(candidate) is not null && NormalizePrivacyPath(parent) is not null;
    }

    private static string? NormalizePrivacyPath(string? path, bool validateFilesystem = true)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 32768 || path.Contains('\0')) return null;
        path = path.Replace('\\', '/');
        if (path.StartsWith("//", StringComparison.Ordinal)) return null; // UNC/WSL paths require an explicit environment mapping.
        var windows = path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] == '/';
        if (!windows && !path.StartsWith('/')) return null;
        var prefix = windows ? char.ToUpperInvariant(path[0]) + ":" : "";
        List<string> parts = [];
        foreach (var part in path[(windows ? 3 : 1)..].Split('/'))
        {
            if (part is "" or ".") continue;
            if (part == "..") { if (parts.Count == 0) return null; parts.RemoveAt(parts.Count - 1); }
            else { if (part.Contains(':')) return null; parts.Add(part); }
        }
        var normalized = prefix + "/" + string.Join('/', parts);
        // Never treat a link/reparse alias as permission to read another folder. This check is
        // bounded by the path's depth and also rejects a moved approved folder replaced by a link.
        if (validateFilesystem && OperatingSystem.IsWindows() == windows)
        {
            try
            {
                for (var current = new DirectoryInfo(normalized); current is not null; current = current.Parent)
                    if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0) return null;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
        }
        return normalized.TrimEnd('/');
    }

    private void RebuildApprovedSearchIndexes(SqliteTransaction transaction)
    {
        ProjectExecute(transaction, "INSERT INTO evidence_fts(evidence_fts) VALUES('delete-all'); INSERT INTO corrections_fts(corrections_fts) VALUES('delete-all')");
        ProjectExecute(transaction, "INSERT INTO evidence_fts(rowid,title,body,author) SELECT r.rowid,r.title,r.body,r.author FROM evidence r WHERE " + ApprovalPredicate);
        ProjectExecute(transaction, "INSERT INTO corrections_fts(rowid,task,note) SELECT c.rowid,c.task,c.note FROM corrections c JOIN evidence r ON r.id=c.record_id WHERE " + ApprovalPredicate);
    }

    private void CreateMigrationBackup(int version)
    {
        // Online backup includes committed WAL content. Never copy a live .db file directly.
        ValidateOwnedSchema(version, null);
        var directory = Path.Combine(Path.GetDirectoryName(databasePath)!, "migration-backups");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, Path.GetFileNameWithoutExtension(databasePath) + "-v" + version.ToString(CultureInfo.InvariantCulture) + "-" + DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffffff", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..8] + ".sqlite");
        using var backup = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false }.ToString());
        backup.Open();
        connection.BackupDatabase(backup);
        using var check = backup.CreateCommand(); check.CommandText = "PRAGMA integrity_check";
        if (!string.Equals(check.ExecuteScalar() as string, "ok", StringComparison.Ordinal))
            throw new IOException("The pre-migration backup could not be verified; the workspace was not migrated.");
    }
}

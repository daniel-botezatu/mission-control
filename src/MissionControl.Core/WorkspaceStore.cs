using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace MissionControl.Core;

/// <summary>Single-writer local store. Hosts run its synchronous operations off the UI thread.</summary>
public sealed partial class WorkspaceStore : IDisposable
{
    public const long MaximumDatabaseBytes = 1024L * 1024 * 1024;
    public long StorageLimitBytes => Math.Clamp(long.TryParse(GetSetting("storage.limitMb"), out var mb) ? mb : 1024, 64, 8192) * 1024 * 1024;
    private const int SchemaVersion = 7;
    private readonly object gate = new();
    private readonly SqliteConnection connection;
    private readonly string databasePath;
    internal string DatabasePath => databasePath;
    private readonly bool readOnly;
    private bool disposed;
    private static readonly string[] RecordColumns = ["id", "source_id", "stream_id", "session_id", "project_id", "source_kind", "kind", "title", "body", "author", "timestamp", "ingested_at", "working_directory", "branch", "state", "url", "data_json", "source_version", "changed_at"];
    private const string SessionProject = "(SELECT sc.project_id FROM session_corrections sc WHERE sc.source_id=r.source_id AND sc.session_id=r.session_id)";
    // A correction is a user annotation; it can never expand the project's privacy boundary.
    private const string EffectiveProject = "r.project_id";
    private static readonly string Projection = string.Join(",", RecordColumns.Select(c => c == "project_id" ? EffectiveProject + " AS project_id" : "r." + c));

    public WorkspaceStore(string databasePath) : this(databasePath, false) { }

    /// <summary>Opens an existing, current workspace without migrations, collectors, or credential access.</summary>
    public static WorkspaceStore OpenReadOnly(string databasePath) => new(databasePath, true);

    private WorkspaceStore(string databasePath, bool readOnly)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        this.databasePath = Path.GetFullPath(databasePath);
        this.readOnly = readOnly;
        if (readOnly && !File.Exists(this.databasePath))
            throw new InvalidOperationException("The workspace does not exist. Open Mission Control to create it before connecting a read-only client.");
        if (!readOnly) WorkspacePaths.CreatePrivateDirectory(Path.GetDirectoryName(this.databasePath)!);
        connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = this.databasePath, Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate, Cache = SqliteCacheMode.Private, Pooling = false }.ToString());
        try
        {
            connection.Open();
            RegisterPrivacyFunctions();
            SQLiteVersion = Convert.ToString(Scalar("SELECT sqlite_version()"), CultureInfo.InvariantCulture)!;
            if (!IsSafeWalVersion(SQLiteVersion))
                throw new InvalidOperationException($"SQLite {SQLiteVersion} does not include the WAL-reset fix. Mission Control requires SQLite 3.51.3+, or maintained backports 3.50.7 / 3.44.6.");
            if (readOnly)
            {
                Execute("PRAGMA busy_timeout=5000; PRAGMA query_only=ON;");
                var version = Convert.ToInt32(Scalar("PRAGMA user_version"), CultureInfo.InvariantCulture);
                if (version != SchemaVersion)
                    throw new InvalidOperationException($"Workspace schema {version} does not match this read-only client ({SchemaVersion}). Open the matching Mission Control release first; this client will not migrate data.");
                ValidateOwnedSchema(version, null);
                return;
            }
            Execute("PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON; PRAGMA synchronous=FULL; PRAGMA secure_delete=ON; PRAGMA wal_autocheckpoint=256; PRAGMA journal_size_limit=8388608;");
            if (!string.Equals(Convert.ToString(Scalar("PRAGMA journal_mode=WAL"), CultureInfo.InvariantCulture), "wal", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("SQLite WAL mode could not be enabled for the selected workspace.");
            Execute("PRAGMA max_page_count=2147483646");
            Migrate();
            RemoveHostWrittenMessages();
            var pageSize = Convert.ToInt64(Scalar("PRAGMA page_size"), CultureInfo.InvariantCulture);
            Execute("PRAGMA max_page_count=" + (StorageLimitBytes / pageSize).ToString(CultureInfo.InvariantCulture));
        }
        catch { connection.Dispose(); throw; }
    }

    public string SQLiteVersion { get; }

    /// <summary>A consistent read across several store queries; the callback must remain synchronous and read-only.</summary>
    public T ReadSnapshot<T>(Func<T> read)
    {
        lock (gate)
        {
            Execute("BEGIN DEFERRED");
            try { var result = read(); Execute("COMMIT"); return result; }
            catch { Execute("ROLLBACK"); throw; }
        }
    }
    public long DatabaseBytes { get { lock (gate) return DatabaseBytesUnsafe(); } }

    public object CompactStorage()
    {
        lock (gate)
        {
            if (readOnly) throw new InvalidOperationException("Read-only clients cannot compact storage.");
            var before = DatabaseBytesUnsafe();
            if (!string.Equals(Scalar("PRAGMA integrity_check") as string, "ok", StringComparison.Ordinal))
                throw new InvalidOperationException("Database integrity check failed; compaction was not started.");
            Execute("PRAGMA wal_checkpoint(TRUNCATE)");
            // VACUUM is atomic; retained records and all settings remain intact.
            using (var command = Command("VACUUM")) { command.CommandTimeout = 120; command.ExecuteNonQuery(); }
            Execute("PRAGMA wal_checkpoint(TRUNCATE)");
            if (!string.Equals(Scalar("PRAGMA integrity_check") as string, "ok", StringComparison.Ordinal))
                throw new InvalidOperationException("Post-compaction integrity check failed; restore the verified backup.");
            return new { beforeBytes = before, afterBytes = DatabaseBytesUnsafe(), integrity = "ok" };
        }
    }

    public static bool IsSafeWalVersion(string version)
    {
        if (!Version.TryParse(version, out var v)) return false;
        return v >= new Version(3, 51, 3) || (v >= new Version(3, 50, 7) && v < new Version(3, 51, 0)) || (v >= new Version(3, 44, 6) && v < new Version(3, 45, 0));
    }

    public IReadOnlyList<SourceConfig> GetSources()
    {
        lock (gate)
        {
            using var command = Command("SELECT id,kind,name,root_path,enabled,environment FROM sources ORDER BY name,id");
            using var reader = command.ExecuteReader();
            var items = new List<SourceConfig>();
            while (reader.Read()) items.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetBoolean(4), reader.GetString(5)));
            return items;
        }
    }

    public void SaveSource(SourceConfig source)
    {
        if (source.Kind is not ("claude" or "codex" or "fixture")) throw new ArgumentException("Only local Claude Code and Codex sources are supported.", nameof(source));
        ValidateIdentity(source.Id, nameof(source.Id));
        lock (gate) Execute("INSERT INTO sources(id,kind,name,root_path,enabled,environment) VALUES($id,$kind,$name,$root,$enabled,$environment) ON CONFLICT(id) DO UPDATE SET kind=excluded.kind,name=excluded.name,root_path=excluded.root_path,enabled=excluded.enabled,environment=excluded.environment",
            ("$id", source.Id), ("$kind", source.Kind), ("$name", Redactor.Clean(source.Name, 200)), ("$root", source.RootPath), ("$enabled", source.Enabled), ("$environment", source.Environment));
    }

    public IReadOnlyList<SourceStatus> GetStatuses()
    {
        lock (gate)
        {
            var progress = ReadSourceProgress();
            using var command = Command("SELECT s.id,s.name,s.kind,s.enabled,CASE WHEN s.enabled=0 THEN 'disabled' ELSE COALESCE(t.state,'not synced') END,t.last_synced,COALESCE(t.pending,0),t.gap,s.root_path,s.environment FROM sources s LEFT JOIN source_status t ON t.source_id=s.id ORDER BY s.name,s.id");
            using var r = command.ExecuteReader();
            var items = new List<SourceStatus>();
            while (r.Read())
            {
                var scan = progress.GetValueOrDefault(r.GetString(0));
                items.Add(new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetBoolean(3), r.GetString(4), NullableString(r, 5),
                    Math.Max(r.GetInt32(6), scan?.PendingFiles ?? 0), NullableString(r, 7), r.GetString(8), r.GetString(9), scan));
            }
            return items;
        }
    }

    public void SetSourceStatus(SourceStatus status)
    {
        lock (gate) Execute("INSERT INTO source_status(source_id,state,last_synced,pending,gap) VALUES($id,$state,$synced,$pending,$gap) ON CONFLICT(source_id) DO UPDATE SET state=excluded.state,last_synced=excluded.last_synced,pending=excluded.pending,gap=excluded.gap",
            ("$id", status.Id), ("$state", status.State), ("$synced", status.LastSynced), ("$pending", Math.Max(0, status.Pending)), ("$gap", CleanNullable(status.Gap, 1000)));
    }

    public void SetSourceStatus(string sourceId, string state, string? gap = null, int pending = 0)
    {
        lock (gate)
        {
            var source = GetSources().FirstOrDefault(s => s.Id == sourceId) ?? throw new ArgumentException("Unknown source.", nameof(sourceId));
            var previous = GetStatuses().FirstOrDefault(s => s.Id == sourceId);
            var synced = state is "current" or "synced" or "gap" ? DateTimeOffset.UtcNow.ToString("O") : previous?.LastSynced;
            SetSourceStatus(new(source.Id, source.Name, source.Kind, source.Enabled, state, synced, pending, gap, source.RootPath, source.Environment));
        }
    }

    public StreamCheckpoint? GetCheckpoint(string sourceId, string streamId)
    {
        lock (gate)
        {
            using var command = Command("SELECT source_id,stream_id,offset,fingerprint,anchor,length,modified_ticks,metadata_json FROM checkpoints WHERE source_id=$source AND stream_id=$stream", ("$source", sourceId), ("$stream", streamId));
            using var r = command.ExecuteReader();
            return r.Read() ? new(r.GetString(0), r.GetString(1), r.GetInt64(2), r.GetString(3), r.GetString(4), r.GetInt64(5), r.GetInt64(6), NullableString(r, 7)) : null;
        }
    }

    public IReadOnlyList<StreamCheckpoint> GetCheckpoints(string sourceId)
    {
        lock (gate)
        {
            using var command = Command("SELECT source_id,stream_id,offset,fingerprint,anchor,length,modified_ticks,metadata_json FROM checkpoints WHERE source_id=$source ORDER BY stream_id LIMIT 50001", ("$source", sourceId));
            using var r = command.ExecuteReader();
            var items = new List<StreamCheckpoint>();
            while (r.Read()) items.Add(new(r.GetString(0), r.GetString(1), r.GetInt64(2), r.GetString(3), r.GetString(4), r.GetInt64(5), r.GetInt64(6), NullableString(r, 7)));
            if (items.Count > 50000) throw new InvalidOperationException("Source has more than 50,000 saved streams. Reconciliation is incomplete; choose a narrower source root.");
            return items;
        }
    }

    /// <summary>
    /// Commits records with their stream checkpoint. A reinterpretation re-reads bytes that were already
    /// observed (for example after a parser upgrade): changed rows keep their previous observed-change time.
    /// </summary>
    public void CommitBatch(IEnumerable<EvidenceRecord> records, StreamCheckpoint? checkpoint = null, bool reinterpretation = false)
        => CommitBatchCore(records, checkpoint, reinterpretation);

    private void CommitBatchCore(IEnumerable<EvidenceRecord> records, StreamCheckpoint? checkpoint, bool reinterpretation)
    {
        // Materialize only one bounded batch; an accidentally unbounded producer fails before a transaction.
        var batch = records.Take(513).ToArray();
        if (batch.Length > 512) throw new ArgumentException("An ingestion batch cannot exceed 512 records.", nameof(records));
        lock (gate)
        {
            // Leave room for diagnostics/settings and avoid starting a batch that cannot fit.
            var reservedBytes = 65536L + batch.Sum(r => 4096L + 4L * (Math.Min(r.Body.Length, r.Kind == "tool" ? 512 : MemoryLimits.MessageCharacters) + Math.Min(r.Title.Length, 512) + Math.Min(r.DataJson?.Length ?? 0, 12000)));
            if (DatabaseBytesUnsafe() + reservedBytes >= StorageLimitBytes) throw new StorageCapacityException();
            using var transaction = connection.BeginTransaction();
            try
            {
                var changed = 0;
                foreach (var incoming in batch)
                {
                    var record = Sanitize(AssignImportedProject(incoming, transaction));
                    if (!CanIndexRecordUnsafe(record, transaction)) continue;
                    var values = Values(record);
                    var parameters = RecordColumns.Select((c, i) => ("$" + c, values[i])).ToArray();
                    var updates = string.Join(",", RecordColumns.Skip(1).Select(c => c == "changed_at" ? reinterpretation ? "changed_at=evidence.changed_at" : "changed_at=MAX(excluded.changed_at,excluded.ingested_at,evidence.changed_at)" : c == "timestamp" ? "timestamp=CASE WHEN excluded.source_version IS NOT NULL THEN evidence.timestamp ELSE excluded.timestamp END" : c + "=excluded." + c));
                    var differs = string.Join(" OR ", RecordColumns.Skip(1).Where(c => c != "ingested_at" && c != "changed_at").Select(c => c == "timestamp" ? "(excluded.source_version IS NULL AND evidence.timestamp IS NOT excluded.timestamp)" : "evidence." + c + " IS NOT excluded." + c));
                    // An older version never replaces a newer one, except that re-reading the same stream with a new
                    // parser is authoritative for that stream's records, even when it moves one to its earlier original time.
                    var reread = reinterpretation ? " OR (excluded.source_version IS NULL AND evidence.source_id IS excluded.source_id AND evidence.stream_id IS excluded.stream_id)" : "";
                    using var command = Command($"INSERT INTO evidence({string.Join(',', RecordColumns)}) VALUES({string.Join(',', RecordColumns.Select(c => "$" + c))}) ON CONFLICT(id) DO UPDATE SET {updates} WHERE (COALESCE(excluded.source_version,excluded.timestamp)>=COALESCE(evidence.source_version,evidence.timestamp){reread}) AND NOT (evidence.state IS 'deleted' AND excluded.state IS NOT 'deleted' AND COALESCE(excluded.source_version,excluded.timestamp)=COALESCE(evidence.source_version,evidence.timestamp)) AND ({differs})", parameters);
                    command.Transaction = transaction;
                    var written = command.ExecuteNonQuery();
                    changed += written;
                }
                if (checkpoint is not null)
                {
                    if (checkpoint.Offset < 0 || checkpoint.Offset > checkpoint.Length) throw new ArgumentException("Checkpoint offset must be inside the source stream.", nameof(checkpoint));
                    using var command = Command("INSERT INTO checkpoints(source_id,stream_id,offset,fingerprint,anchor,length,modified_ticks,metadata_json,updated_at) VALUES($source,$stream,$offset,$fingerprint,$anchor,$length,$modified,$metadata,$now) ON CONFLICT(source_id,stream_id) DO UPDATE SET offset=excluded.offset,fingerprint=excluded.fingerprint,anchor=excluded.anchor,length=excluded.length,modified_ticks=excluded.modified_ticks,metadata_json=excluded.metadata_json,updated_at=excluded.updated_at",
                        ("$source", checkpoint.SourceId), ("$stream", checkpoint.StreamId), ("$offset", checkpoint.Offset), ("$fingerprint", checkpoint.Fingerprint), ("$anchor", checkpoint.Anchor), ("$length", checkpoint.Length), ("$modified", checkpoint.ModifiedTicks), ("$metadata", SanitizeCheckpointMetadata(checkpoint.MetadataJson, transaction)), ("$now", DateTimeOffset.UtcNow.ToString("O")));
                    command.Transaction = transaction;
                    command.ExecuteNonQuery();
                }
                if (changed > 0) Invalidate(transaction);
                if (DatabaseBytesUnsafe() >= StorageLimitBytes) throw new StorageCapacityException();
                transaction.Commit();
            }
            catch (SqliteException error) when (error.SqliteErrorCode == 13) { transaction.Rollback(); throw new StorageCapacityException(); }
            catch { transaction.Rollback(); throw; }
        }
    }

    public IReadOnlyList<ProjectInfo> GetProjects()
    {
        lock (gate) return ReadProjects();
    }

    public void SaveProject(ProjectInfo project)
    {
        ValidateIdentity(project.Id, nameof(project.Id));
        var root = NormalizePrivacyPath(project.RootPath);
        if (root is null || BroadDiscoveryRoot(project.RootPath!))
            throw new ArgumentException("Use a specific absolute local project folder.", nameof(project));
        lock (gate)
        {
            var existing = GetProjects().FirstOrDefault(p => p.Id == project.Id);
            using var transaction = connection.BeginTransaction();
            var rootChanged = existing is not null && !string.Equals(NormalizePrivacyPath(existing.RootPath), root, ProjectPathComparison);
            if (rootChanged)
            {
                ProjectExecute(transaction, "DELETE FROM evidence WHERE project_id=$id; DELETE FROM session_corrections WHERE project_id=$id; UPDATE projects SET is_approved=0,approved_at=NULL WHERE id=$id; DELETE FROM project_workspaces WHERE project_id=$id; DELETE FROM project_repositories WHERE project_id=$id; DELETE FROM checkpoints", ("$id", project.Id));
            }
            ProjectExecute(transaction, "INSERT INTO projects(id,name,color,description,root_path,origin) VALUES($id,$name,$color,$description,$root,'manual') ON CONFLICT(id) DO UPDATE SET name=excluded.name,color=excluded.color,description=excluded.description,root_path=excluded.root_path,origin='manual'",
                ("$id", project.Id), ("$name", Redactor.Clean(project.Name, 200)), ("$color", Redactor.Clean(project.Color, 64)), ("$description", Redactor.Clean(project.Description, 2000)), ("$root", project.RootPath));
            projectRoots = null;
            ReassignProjectEvidenceUnsafe(transaction);
            Invalidate(transaction); transaction.Commit();
        }
    }

    public QueryResult Query(QueryOptions options, bool previews = false, bool fuzzy = true)
    {
        lock (gate)
        {
            var (where, parameters) = Predicate(options);
            var total = Convert.ToInt32(Scalar("SELECT COUNT(*) FROM evidence r LEFT JOIN corrections c ON c.record_id=r.id " + where, parameters), CultureInfo.InvariantCulture);
            if (total == 0 && fuzzy && !string.IsNullOrWhiteSpace(options.Search))
            {
                var (visible, visibleParameters) = Predicate(options with { Search = null });
                if (CorrectSearch(options.Search, visible, visibleParameters) is { } corrected)
                    return Query(options with { Search = corrected }, previews, fuzzy: false) with { SearchNotice = "Used spelling-tolerant search. Verify matching excerpts." };
            }
            var bound = parameters.Concat(new (string, object?)[] { ("$limit", Math.Clamp(options.Limit, 1, 500)), ("$offset", Math.Max(0, options.Offset)) }).ToArray();
            var ordering = options.ByTimestamp ? "r.timestamp DESC,r.id" : "r.changed_at DESC,r.id";
            var projection = previews ? Projection.Replace("r.body", "substr(r.body,1,2000) AS body").Replace("r.data_json", "NULL AS data_json") + ",length(r.body),length(r.body)>2000" : Projection;
            return new(ReadRecords($"SELECT {projection} FROM evidence r LEFT JOIN corrections c ON c.record_id=r.id {where} ORDER BY {ordering} LIMIT $limit OFFSET $offset", bound), total);
        }
    }

    public int Count() { lock (gate) return Convert.ToInt32(Scalar("SELECT COUNT(*) FROM evidence r WHERE " + ApprovalPredicate), CultureInfo.InvariantCulture); }

    public EvidenceRecord? GetRecord(string id)
    {
        lock (gate) return ReadRecords($"SELECT {Projection} FROM evidence r LEFT JOIN corrections c ON c.record_id=r.id WHERE r.id=$id AND {ApprovalPredicate}", ("$id", id)).FirstOrDefault();
    }

    public IReadOnlyList<UserCorrection> GetCorrections(IReadOnlyList<string>? recordIds = null)
    {
        lock (gate)
        {
            if (recordIds is { Count: 0 }) return [];
            var parameters = recordIds?.Select((id, index) => ("$r" + index, (object?)id)).ToArray() ?? [];
            var filter = " WHERE record_id IN (SELECT r.id FROM evidence r WHERE " + ApprovalPredicate + ")" + (recordIds is null ? "" : " AND record_id IN (" + string.Join(',', parameters.Select(p => p.Item1)) + ")");
            using var command = Command("SELECT record_id,project_id,task,dismissed,snoozed_until,note FROM corrections" + filter + " ORDER BY record_id", parameters);
            using var r = command.ExecuteReader();
            var items = new List<UserCorrection>();
            while (r.Read()) items.Add(new(r.GetString(0), NullableString(r, 1), NullableString(r, 2), r.GetBoolean(3), NullableString(r, 4), NullableString(r, 5)));
            return items;
        }
    }

    public void SaveCorrection(UserCorrection correction)
    {
        lock (gate)
        {
            if (GetRecord(correction.RecordId) is null) throw new ArgumentException("The evidence record no longer exists.", nameof(correction));
            using var transaction = connection.BeginTransaction();
            using var command = Command("INSERT INTO corrections(record_id,project_id,task,dismissed,snoozed_until,note) VALUES($id,$project,$task,$dismissed,$snoozed,$note) ON CONFLICT(record_id) DO UPDATE SET project_id=excluded.project_id,task=excluded.task,dismissed=excluded.dismissed,snoozed_until=excluded.snoozed_until,note=excluded.note",
                ("$id", correction.RecordId), ("$project", correction.ProjectId), ("$task", CleanNullable(correction.Task, 1000)), ("$dismissed", correction.Dismissed), ("$snoozed", correction.SnoozedUntil), ("$note", CleanNullable(correction.Note, 4000)));
            command.Transaction = transaction;
            command.ExecuteNonQuery();
            Invalidate(transaction);
            transaction.Commit();
        }
    }

    public void DeleteCorrection(string id)
    {
        lock (gate)
        {
            using var transaction = connection.BeginTransaction();
            using var command = Command("DELETE FROM corrections WHERE record_id=$id", ("$id", id));
            command.Transaction = transaction;
            if (command.ExecuteNonQuery() > 0) Invalidate(transaction);
            transaction.Commit();
        }
    }

    public string? GetSetting(string key, string? fallback = null)
    {
        lock (gate) return Scalar("SELECT value FROM settings WHERE key=$key", ("$key", key)) as string ?? fallback;
    }

    public void SetSetting(string key, string value)
    {
        if (value.Length > 32768) throw new ArgumentException("A setting cannot exceed 32 KB.", nameof(value));
        lock (gate)
        {
            Execute("INSERT INTO settings(key,value) VALUES($key,$value) ON CONFLICT(key) DO UPDATE SET value=excluded.value", ("$key", key), ("$value", value));
            if (key == "storage.limitMb") Execute("PRAGMA max_page_count=" + (StorageLimitBytes / Convert.ToInt64(Scalar("PRAGMA page_size"), CultureInfo.InvariantCulture)).ToString(CultureInfo.InvariantCulture));
        }
    }

    public string EvidenceVersion() { lock (gate) return Convert.ToString(Scalar("SELECT evidence_version FROM workspace_meta WHERE id=1"), CultureInfo.InvariantCulture)!; }

    /// <summary>
    /// Removes messages written more than <paramref name="days"/> ago, judged by their original
    /// timestamp rather than when Mission Control saved them. Annotated records and corrected
    /// sessions are kept; rows without a readable timestamp are never removed.
    /// </summary>
    public int ApplyRetention(int days)
    {
        if (days < 1 || days > 36500) throw new ArgumentOutOfRangeException(nameof(days), "Retention must be between 1 and 36500 days.");
        lock (gate)
        {
            using var transaction = connection.BeginTransaction();
            using var command = Command("DELETE FROM evidence WHERE julianday(timestamp)<julianday($cutoff) AND NOT EXISTS(SELECT 1 FROM corrections c WHERE c.record_id=evidence.id) AND NOT EXISTS(SELECT 1 FROM session_corrections sc WHERE sc.source_id=evidence.source_id AND sc.session_id=evidence.session_id)", ("$cutoff", DateTimeOffset.UtcNow.AddDays(-days).ToString("O")));
            command.Transaction = transaction;
            var removed = command.ExecuteNonQuery();
            if (removed > 0) Invalidate(transaction);
            using (var stamp = Command("INSERT INTO settings(key,value) VALUES('retention.lastApplied',$now) ON CONFLICT(key) DO UPDATE SET value=excluded.value", ("$now", DateTimeOffset.UtcNow.ToString("O"))))
            { stamp.Transaction = transaction; stamp.ExecuteNonQuery(); }
            transaction.Commit();
            Execute("PRAGMA incremental_vacuum; PRAGMA wal_checkpoint(TRUNCATE)");
            return removed;
        }
    }

    /// <summary>Applies the saved retention period when it has not run within <paramref name="interval"/>; null when not due.</summary>
    public int? ApplyRetentionIfDue(TimeSpan interval)
    {
        lock (gate)
        {
            if (DateTimeOffset.TryParse(GetSetting("retention.lastApplied"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var last)
                && DateTimeOffset.UtcNow - last < interval) return null;
            var days = int.TryParse(GetSetting("retention.days"), out var saved) ? Math.Clamp(saved, 7, 3650) : 90;
            return ApplyRetention(days);
        }
    }

    public void DeleteSource(string id)
    {
        lock (gate)
        {
            using var transaction = connection.BeginTransaction();
            DeleteSourceRows(id, transaction);
            Invalidate(transaction);
            transaction.Commit();
            Execute("PRAGMA incremental_vacuum; PRAGMA wal_checkpoint(TRUNCATE)");
        }
    }

    private void DeleteSourceRows(string id, SqliteTransaction transaction)
    {
        foreach (var sql in new[] {
            "DELETE FROM corrections WHERE record_id IN (SELECT id FROM evidence WHERE source_id=$id)",
            "DELETE FROM session_corrections WHERE source_id=$id",
            "DELETE FROM evidence WHERE source_id=$id", "DELETE FROM checkpoints WHERE source_id=$id",
            "DELETE FROM source_status WHERE source_id=$id", "DELETE FROM sources WHERE id=$id" })
        {
            using var command = Command(sql, ("$id", id));
            command.Transaction = transaction;
            command.ExecuteNonQuery();
        }
    }

    public AppSnapshot Snapshot(string mode, string monitoring)
    {
        lock (gate)
        {
            // The dashboard only contains approved local conversation summaries.
            var summaryProjection = string.Join(',', RecordColumns.Select(column => column switch
            {
                "project_id" => EffectiveProject + " AS project_id",
                "body" => "substr(r.body,1,512) AS body",
                "data_json" => "NULL AS data_json",
                _ => "r." + column
            }));
            var records = ReadRecords($"SELECT {summaryProjection} FROM evidence r LEFT JOIN corrections c ON c.record_id=r.id WHERE {ApprovalPredicate} ORDER BY r.timestamp DESC,r.id LIMIT 250");
            var checkpoint = Scalar("SELECT MAX(updated_at) FROM checkpoints") as string;
            var budget = 4 * 1024 * 1024 - 4096;
            var partial = false;
            List<T> Bounded<T>(IReadOnlyList<T> values, int maximumBytes)
            {
                List<T> result = [];
                var available = Math.Min(budget, maximumBytes);
                foreach (var value in values)
                {
                    var bytes = JsonSerializer.SerializeToUtf8Bytes(value).Length + 1;
                    if (bytes > available) { partial = true; continue; }
                    result.Add(value); available -= bytes; budget -= bytes;
                }
                return result;
            }
            var projects = Bounded(GetProjects().Where(p => p.IsApproved).ToArray(), 1024 * 1024);
            var sources = Bounded(GetStatuses(), 1024 * 1024);
            var summaries = Bounded(records, budget * 3 / 4);
            var ids = summaries.Select(r => r.Id).ToHashSet(StringComparer.Ordinal);
            var corrections = Bounded(GetCorrections(ids.ToArray()).Select(c => c with { Note = null, Task = CleanNullable(c.Task, 200) }).ToArray(), budget);
            var discoveryGap = GetSetting("projects.discovery.gap") is { Length: > 0 } gap ? gap : null;
            return new(mode, monitoring, projects, summaries, sources, corrections, Count(), SQLiteVersion, checkpoint, DatabaseBytesUnsafe(), partial, discoveryGap);
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            try { if (!readOnly) Execute("PRAGMA wal_checkpoint(TRUNCATE)"); }
            finally { disposed = true; connection.Dispose(); }
        }
    }

    private void Migrate()
    {
        var version = Convert.ToInt32(Scalar("PRAGMA user_version"), CultureInfo.InvariantCulture);
        if (version > SchemaVersion) throw new InvalidOperationException($"Workspace schema {version} is newer than this application supports ({SchemaVersion}). Open it with a newer Mission Control version.");
        if (version == 0)
        {
            if (SchemaObjects(connection).Count > 0)
                throw new InvalidOperationException("The selected file contains an unrecognized database. Mission Control will not change its tables.");
            Execute("PRAGMA auto_vacuum=INCREMENTAL");
        }
        if (version is > 0 and < SchemaVersion) CreateMigrationBackup(version);
        using var transaction = connection.BeginTransaction();
        // A version number alone does not establish ownership. Refuse foreign objects or an
        // incompatible schema before any migration DDL, including files labelled version 1.
        ValidateOwnedSchema(version, transaction);
        for (var next = version + 1; next <= SchemaVersion; next++)
        {
            using var command = Command(MigrationSql(next));
            command.Transaction = transaction;
            command.ExecuteNonQuery();
            using var versionCommand = Command("PRAGMA user_version=" + next.ToString(CultureInfo.InvariantCulture));
            versionCommand.Transaction = transaction;
            versionCommand.ExecuteNonQuery();
        }
        transaction.Commit();
        if (version is > 0 and < SchemaVersion) Execute("PRAGMA wal_checkpoint(TRUNCATE); PRAGMA incremental_vacuum");
    }

    private void ValidateOwnedSchema(int version, SqliteTransaction? transaction)
    {
        using var canonical = new SqliteConnection("Data Source=:memory:;Pooling=False");
        canonical.Open();
        for (var step = 1; step <= version; step++)
        {
            using var create = canonical.CreateCommand();
            create.CommandText = MigrationSql(step);
            create.ExecuteNonQuery();
        }
        var expected = SchemaObjects(canonical);
        var actual = SchemaObjects(connection, transaction);
        if (expected.Count != actual.Count || actual.Any(item => !expected.TryGetValue(item.Key, out var sql) || sql != item.Value))
            throw new InvalidOperationException("The selected file contains an unrecognized or incompatible database schema. Mission Control will not change its tables.");
    }

    private static Dictionary<string, string> SchemaObjects(SqliteConnection database, SqliteTransaction? transaction = null)
    {
        using var command = database.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT type,name,sql FROM sqlite_master WHERE name NOT GLOB 'sqlite_*' ORDER BY type,name";
        using var reader = command.ExecuteReader();
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        while (reader.Read())
            result.Add(reader.GetString(0) + ":" + reader.GetString(1), Regex.Replace(reader.IsDBNull(2) ? "" : reader.GetString(2), @"\s+", " ").Trim());
        return result;
    }

    private static string MigrationSql(int version)
    {
        var name = version switch { 1 => "001_Initial.sql", 2 => "002_Remote.sql", 3 => "003_Collection.sql", 4 => "004_AutomaticProjects.sql", 5 => "005_ManualContextSearch.sql", 6 => "006_ProjectPaths.sql", 7 => "007_LocalMemory.sql", _ => throw new InvalidOperationException("Unsupported workspace schema version.") };
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("MissionControl.Core.Migrations." + name) ?? throw new InvalidOperationException("The workspace schema migration is missing from the application.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private List<EvidenceRecord> ReadRecords(string sql, params (string, object?)[] parameters)
    {
        using var command = Command(sql, parameters);
        using var r = command.ExecuteReader();
        var items = new List<EvidenceRecord>();
        while (r.Read()) items.Add(new(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), NullableString(r, 4), r.GetString(5), r.GetString(6), r.GetString(7), r.GetString(8), r.GetString(9), r.GetString(10), r.GetString(11), NullableString(r, 12), NullableString(r, 13), NullableString(r, 14), NullableString(r, 15), NullableString(r, 16), NullableString(r, 17), NullableString(r, 18), r.FieldCount > 20 ? r.GetInt32(19) : null, r.FieldCount > 20 && r.GetBoolean(20) ? 0 : null));
        return items;
    }

    private static (string Where, (string, object?)[] Parameters) Predicate(QueryOptions options)
    {
        var terms = new List<string> { ApprovalPredicate };
        var parameters = new List<(string, object?)>();
        void Add(string expression, string name, string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            terms.Add(expression); parameters.Add((name, value));
        }
        if (options.ProjectId == "__unassigned") terms.Add("(" + EffectiveProject + ") IS NULL");
        else Add("(" + EffectiveProject + ")=$project", "$project", options.ProjectId);
        Add("r.source_kind=$source", "$source", options.SourceKind);
        Add("r.source_id=$sourceId", "$sourceId", options.SourceId);
        Add("r.kind=$kind", "$kind", options.Kind);
        if (string.Equals(options.State?.Trim(), "open", StringComparison.OrdinalIgnoreCase))
            terms.Add("lower(trim(COALESCE(r.state,''))) IN ('open','draft')");
        else Add("lower(trim(r.state))=$state", "$state", options.State?.Trim().ToLowerInvariant());
        Add("r.session_id=$session", "$session", options.SessionId);
        Add("r.changed_at >= $since", "$since", options.Since is null ? null : Timestamp(options.Since));
        Add("r.author LIKE $person ESCAPE '\\'", "$person", options.Person is null ? null : "%" + options.Person.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%");
        AddSearchPredicate(options.Search, terms, parameters);
        return (terms.Count == 0 ? "" : "WHERE " + string.Join(" AND ", terms), parameters.ToArray());
    }

    private static EvidenceRecord Sanitize(EvidenceRecord r)
    {
        if (string.IsNullOrWhiteSpace(r.Id) || string.IsNullOrWhiteSpace(r.SourceId) || string.IsNullOrWhiteSpace(r.StreamId) || string.IsNullOrWhiteSpace(r.SessionId))
            throw new ArgumentException("Evidence requires stable record, source, stream, and session identities.");
        if (new[] { r.Id, r.SourceId, r.StreamId, r.SessionId, r.ProjectId }.Any(id => id?.Length > 4096))
            throw new ArgumentException("Evidence identities cannot exceed 4096 characters.");
        var deleted = r.State == "deleted";
        return r with { SourceKind = Redactor.Clean(r.SourceKind, 100), Kind = Redactor.Clean(r.Kind, 100), Title = deleted ? "Deleted evidence" : Redactor.Clean(r.Title, 512), Body = deleted ? "" : Redactor.Clean(r.Body, r.Kind == "tool" ? 512 : MemoryLimits.MessageCharacters), Author = deleted ? "" : Redactor.Clean(r.Author, 200), Timestamp = Timestamp(r.Timestamp), IngestedAt = Timestamp(r.IngestedAt), WorkingDirectory = CleanNullable(r.WorkingDirectory, 2048), Branch = CleanNullable(r.Branch, 256), State = CleanNullable(r.State, 100), Url = CleanNullable(r.Url, 2048), DataJson = deleted ? null : SafeJson(r.DataJson), SourceVersion = r.SourceVersion is null ? null : Timestamp(r.SourceVersion), ChangedAt = Timestamp(r.ChangedAt ?? r.SourceVersion ?? r.Timestamp) };
    }

    private static string Timestamp(string value)
    {
        if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
            throw new ArgumentException("Evidence timestamps must be valid ISO-8601 dates.");
        return parsed.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    }

    private static string? SafeJson(string? json, int maximum = 12000, int stringMaximum = 12000)
    {
        if (json is null) return null;
        if (json.Length > maximum) return "{\"truncated\":true}";
        try
        {
            using var document = JsonDocument.Parse(json);
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer)) Write(document.RootElement, writer);
            var clean = System.Text.Encoding.UTF8.GetString(buffer.ToArray());
            return clean.Length > maximum ? "{\"truncated\":true}" : clean;
        }
        catch (Exception error) when (error is JsonException or RegexMatchTimeoutException) { return "{\"invalidOrRedacted\":true}"; }

        void Write(JsonElement value, Utf8JsonWriter writer)
        {
            switch (value.ValueKind)
            {
                case JsonValueKind.Object:
                    writer.WriteStartObject();
                    foreach (var property in value.EnumerateObject())
                    {
                        writer.WritePropertyName(Redactor.Clean(property.Name, 12000));
                        if (Redactor.IsSensitiveKey(property.Name)) writer.WriteStringValue("[REDACTED]");
                        else Write(property.Value, writer);
                    }
                    writer.WriteEndObject(); break;
                case JsonValueKind.Array:
                    writer.WriteStartArray();
                    foreach (var item in value.EnumerateArray()) Write(item, writer);
                    writer.WriteEndArray(); break;
                case JsonValueKind.String: writer.WriteStringValue(Redactor.Clean(value.GetString()!, stringMaximum)); break;
                default: value.WriteTo(writer); break;
            }
        }
    }

    private static object?[] Values(EvidenceRecord r) => [r.Id, r.SourceId, r.StreamId, r.SessionId, r.ProjectId, r.SourceKind, r.Kind, r.Title, r.Body, r.Author, r.Timestamp, r.IngestedAt, r.WorkingDirectory, r.Branch, r.State, r.Url, r.DataJson, r.SourceVersion, r.ChangedAt];
    private static void ValidateIdentity(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 4096) throw new ArgumentException("A nonempty identity of at most 4096 characters is required.", name);
    }
    private static string? NullableString(SqliteDataReader r, int index) => r.IsDBNull(index) ? null : r.GetString(index);
    private static string? CleanNullable(string? value, int max) => value is null ? null : Redactor.Clean(value, max);

    private void Invalidate(SqliteTransaction transaction)
    {
        using var command = Command("UPDATE workspace_meta SET evidence_version=evidence_version+1 WHERE id=1;");
        command.Transaction = transaction;
        command.ExecuteNonQuery();
    }

    private long DatabaseBytesUnsafe()
    {
        long total = 0;
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { var file = new FileInfo(databasePath + suffix); if (file.Exists) total += file.Length; }
            catch (IOException) { }
        }
        return total;
    }

    private SqliteCommand Command(string sql, params (string, object?)[] parameters)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        statementPaths.Clear(); statementPathPairs.Clear();
        var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (key, value) in parameters) command.Parameters.AddWithValue(key, value ?? DBNull.Value);
        return command;
    }

    private object? Scalar(string sql, params (string, object?)[] parameters)
    {
        using var command = Command(sql, parameters);
        return command.ExecuteScalar();
    }

    private int Execute(string sql, params (string, object?)[] parameters)
    {
        using var command = Command(sql, parameters);
        return command.ExecuteNonQuery();
    }
}

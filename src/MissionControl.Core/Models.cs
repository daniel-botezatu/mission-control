namespace MissionControl.Core;

public sealed record SourceConfig(string Id, string Kind, string Name, string RootPath, bool Enabled = false, string Environment = "windows");
public sealed record SourceStatus(string Id, string Name, string Kind, bool Enabled, string State, string? LastSynced, int Pending, string? Gap, string RootPath, string Environment, SourceProgress? Progress = null);
public sealed record SourceProgress(int ScannedFiles, int PendingFiles, long ScannedBytes, long TotalBytes, long PendingBytes, string? LastCheckpoint);
public sealed record EvidenceRecord(
    string Id, string SourceId, string StreamId, string SessionId, string? ProjectId,
    string SourceKind, string Kind, string Title, string Body, string Author,
    string Timestamp, string IngestedAt, string? WorkingDirectory = null,
    string? Branch = null, string? State = null, string? Url = null, string? DataJson = null, string? SourceVersion = null, string? ChangedAt = null, int? BodyLength = null, int? NextBodyOffset = null);
public sealed record StreamCheckpoint(string SourceId, string StreamId, long Offset, string Fingerprint, string Anchor, long Length, long ModifiedTicks, string? MetadataJson = null);
public sealed record UserCorrection(string RecordId, string? ProjectId = null, string? Task = null, bool Dismissed = false, string? SnoozedUntil = null, string? Note = null);
public sealed record ProjectInfo(string Id, string Name, string Color, string Description, string? RootPath = null, bool AutoDiscovered = false, IReadOnlyList<ProjectRepository>? Repositories = null, bool IsApproved = false, string? ApprovedAt = null);
public sealed record ProjectRepository(string LocalRoot, IReadOnlyList<string> GitHubRepositories);
public sealed record QueryOptions(string? Search = null, string? ProjectId = null, string? SourceKind = null, string? Kind = null, int Limit = 100, int Offset = 0, string? Since = null, string? Person = null, string? SessionId = null, string? State = null, string? SourceId = null, bool ByTimestamp = false);
public sealed record QueryResult(IReadOnlyList<EvidenceRecord> Records, int Total, string? SearchNotice = null);
public sealed record CollectionStatistics(string Monitoring, int SavedEntries, int Sessions, int UserMessages, int FinalResponses, int StatusEvents, long DatabaseBytes, long ActiveDatabaseBytes, long StorageLimitBytes, string? LastCheckpoint, bool CatchingUp, int PendingFiles = 0, long PendingBytes = 0, long ScannedBytes = 0, long TotalSourceBytes = 0);
public sealed record AppSnapshot(string Monitoring, IReadOnlyList<ProjectInfo> Projects, IReadOnlyList<EvidenceRecord> Records, IReadOnlyList<SourceStatus> Sources, IReadOnlyList<UserCorrection> Corrections, int TotalRecords, string SqliteVersion, string? LastCheckpoint, long DatabaseBytes, bool IsPartial = false, string? ProjectDiscoveryGap = null);

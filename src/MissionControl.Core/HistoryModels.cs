using System.Text.Json;

namespace MissionControl.Core;

/// <summary>Operator supplied restrictions; tool arguments can only narrow these values.</summary>
public sealed record HistoryScope(string? ProjectId = null, string? SessionId = null, string? Since = null, string? Until = null, string? SourceId = null);
public sealed record HistoryToolDefinition(string Name, string Description, JsonElement InputSchema);
public sealed record HistoryToolResult(string Json, bool IsError = false);
internal sealed record HistoryQuery(string? Query = null, string Mode = "all", string? ProjectId = null,
    string? SourceKind = null, string? SourceId = null, string? SessionId = null, string? From = null,
    string? Before = null, string? ExchangeId = null, int Limit = 10, int Offset = 0, bool Chronological = false,
    bool IncludeLifecycle = false, string? SeekTimestamp = null, string? SeekId = null, bool IncludeAnchor = false);
internal sealed record HistorySession(string SourceId, string SessionId, string SourceKind, string? ProjectId,
    string FirstTimestamp, string LastTimestamp, int Messages, int UserMessages, int AssistantResponses,
    string? Branch, string? WorkingDirectory, string? LastObservedState, string? StateRecordId, string? StateTimestamp);
internal sealed record HistorySessionPage(IReadOnlyList<HistorySession> Sessions, int Total);

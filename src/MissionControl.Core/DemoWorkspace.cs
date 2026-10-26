using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MissionControl.Core;

/// <summary>App-owned synthetic conversation files, collected through the normal approved-project pipeline.</summary>
public sealed class DemoWorkspace
{
    public const int InitialRecordCount = 30;
    public const int OfflineEventCount = 6;
    public const int RecoveredRecordCount = 34;
    private const string InitializationKey = "demo.memory.initialized.v3";
    private const string OriginKey = "demo.fixture.origin-utc";
    private const string OfflineKey = "demo.offline.appended.v1";
    private static readonly DateTimeOffset TemplateReference = new(2026, 10, 16, 10, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly WorkspaceStore store;
    private readonly SemaphoreSlim serial = new(1, 1);
    private readonly string projectDirectory;

    public DemoWorkspace(string dataDirectory, WorkspaceStore store)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        this.store = store;
        var dataRoot = Path.GetFullPath(dataDirectory);
        SourceDirectory = Path.Combine(dataRoot, "demo-sources");
        projectDirectory = Path.Combine(dataRoot, "projects");
    }

    public string SourceDirectory { get; }
    public string InitialFilePath => Path.Combine(SourceDirectory, "initial.jsonl");
    public bool HasOfflineChanges => store.GetSetting(OfflineKey) == "1";

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await serial.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(SourceDirectory);
            var shift = ReadOrCreateTimeShift();
            if (store.GetSetting(InitializationKey) == "1" && File.Exists(InitialFilePath)) return;
            // Only the generated demo source is replaced. Live data and settings are separate.
            if (store.GetSetting(InitializationKey) != "1") store.DeleteSource("demo-replay");
            ProjectInfo[] projects =
            [
                new("atlas", "Atlas API", "#89a8ff", "Cursor pagination decisions and regression coverage.", Path.Combine(projectDirectory, "atlas")),
                new("studio", "Studio", "#c69bf2", "Sidebar accessibility and design-system decisions.", Path.Combine(projectDirectory, "studio")),
                new("orbit", "Orbit mobile", "#70c6b0", "Token refresh reliability and device verification.", Path.Combine(projectDirectory, "orbit")),
                new("dispatch", "Dispatch", "#e7b477", "Queue idempotency and the unresolved concurrency race.", Path.Combine(projectDirectory, "dispatch")),
                new("platform", "Platform", "#86c1d5", "CI cache isolation and staging verification.", Path.Combine(projectDirectory, "platform"))
            ];
            foreach (var project in projects)
            {
                Directory.CreateDirectory(project.RootPath!);
                store.SaveProject(project);
                store.SetProjectApproval(project.Id, true);
            }
            var contents = await MaterializeAsync("demo-initial.jsonl", shift, cancellationToken);
            if (HasOfflineChanges) contents += await MaterializeAsync("demo-offline.jsonl", shift, cancellationToken);
            var temporaryPath = InitialFilePath + ".new";
            await File.WriteAllTextAsync(temporaryPath, contents, new UTF8Encoding(false), cancellationToken);
            File.Move(temporaryPath, InitialFilePath, true);
            store.SaveSource(new("demo-replay", "fixture", "Demo conversations · synthetic local fixtures", SourceDirectory, true));
            store.SetSetting(InitializationKey, "1");
        }
        finally { serial.Release(); }
    }

    /// <summary>Appends once while paused; stable identities make interrupted-write retries idempotent.</summary>
    public async Task AppendChangesAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await serial.WaitAsync(cancellationToken);
        try
        {
            if (HasOfflineChanges) return;
            var contents = await MaterializeAsync("demo-offline.jsonl", ReadOrCreateTimeShift(), cancellationToken);
            var bytes = Encoding.UTF8.GetBytes("\n" + contents);
            await using (var stream = new FileStream(InitialFilePath, FileMode.Append, FileAccess.Write, FileShare.Read, 4096, FileOptions.Asynchronous))
            {
                await stream.WriteAsync(bytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            store.SetSetting(OfflineKey, "1");
        }
        finally { serial.Release(); }
    }

    private TimeSpan ReadOrCreateTimeShift()
    {
        var saved = store.GetSetting(OriginKey);
        if (DateTimeOffset.TryParse(saved, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var origin)) return origin - TemplateReference;
        var firstRunOrigin = DateTimeOffset.UtcNow.AddMinutes(-30);
        store.SetSetting(OriginKey, firstRunOrigin.ToString("O", CultureInfo.InvariantCulture));
        return firstRunOrigin - TemplateReference;
    }

    private async Task<string> MaterializeAsync(string fixtureName, TimeSpan shift, CancellationToken cancellationToken)
    {
        await using var stream = OpenFixture(fixtureName);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var text = new StringBuilder();
        while (await reader.ReadLineAsync(cancellationToken) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var record = JsonSerializer.Deserialize<EvidenceRecord>(line, Json) ?? throw new InvalidDataException($"Invalid demo fixture in {fixtureName}.");
            if (record.SourceKind is not ("claude" or "codex") || record.Kind != "message" || record.Author is not ("user" or "assistant") || record.ProjectId is null)
                throw new InvalidDataException("Demo fixtures must contain only conversation messages.");
            var metadata = JsonNode.Parse(record.DataJson ?? "{}") as JsonObject ?? new JsonObject();
            metadata["synthetic"] = true;
            metadata["schema"] = "memory-v1";
            metadata["phase"] = record.Author == "user" ? "user" : "final";
            metadata["sourceRecordId"] = record.Id;
            metadata["final"] = record.Author == "assistant";
            text.AppendLine(JsonSerializer.Serialize(record with
            {
                Timestamp = (DateTimeOffset.Parse(record.Timestamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) + shift).ToString("O", CultureInfo.InvariantCulture),
                IngestedAt = "",
                WorkingDirectory = Path.Combine(projectDirectory, record.ProjectId),
                DataJson = metadata.ToJsonString(Json)
            }, Json));
        }
        return text.ToString();
    }

    private static Stream OpenFixture(string fixtureName)
    {
        var resource = typeof(DemoWorkspace).Assembly.GetManifestResourceStream("MissionControl.Core.Fixtures." + fixtureName);
        if (resource is not null) return resource;
        foreach (var candidate in new[] { Path.Combine(AppContext.BaseDirectory, "fixtures", fixtureName), Path.Combine(Environment.CurrentDirectory, "fixtures", fixtureName) })
            if (File.Exists(candidate)) return File.OpenRead(candidate);
        throw new FileNotFoundException($"Demo fixture {fixtureName} is missing from the application package.");
    }
}

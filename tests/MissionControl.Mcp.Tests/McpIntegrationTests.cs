using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using MissionControl.Core;
using MissionControl.Mcp;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace MissionControl.Mcp.Tests;

public sealed class McpIntegrationTests
{
    [Theory]
    [InlineData("2025-11-25", "full")]
    [InlineData("2024-11-05", "full")]
    [InlineData("2025-11-25", "compact")]
    [InlineData("2024-11-05", "compact")]
    public async Task Date_window_continuations_fit_actual_mcp_response_and_do_not_write_history(string version, string format)
    {
        using var fixture = new Fixture(); fixture.Seed().Dispose();
        var original = SHA256.HashData(File.ReadAllBytes(fixture.Database));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var client = await fixture.Connect(version, timeout.Token);
        string? cursor = null; var bodies = new Dictionary<string,string>(); JsonElement page = default;
        for (var i = 0; i < 30; i++)
        {
            var arguments = new Dictionary<string,object?> { ["view"] = "messages", ["from"] = "2026-10-20T00:00:00Z", ["before"] = "2026-10-21T00:00:00Z", ["bodyCharacters"] = 200, ["maxOutputCharacters"] = 6000 };
            arguments["format"] = format;
            if (cursor is not null) arguments["cursor"] = cursor;
            var result = await Call(client, "get_project_activity", arguments, timeout.Token);
            Assert.False(result.IsError); Assert.True(JsonSerializer.Serialize(result).Length <= 6000);
            Assert.Null(result.StructuredContent); // one representation, on both protocol versions
            page = JsonSerializer.Deserialize<JsonElement>(result.Content.OfType<TextContentBlock>().Single().Text);
            if (format == "compact")
            {
                foreach (var group in page.GetProperty("conversations").EnumerateArray())
                    foreach (var row in group.GetProperty("messages").EnumerateArray())
                    {
                        var id = row[0].GetString()!; var previous = bodies.GetValueOrDefault(id, "");
                        Assert.Equal(previous.Length, row[4].GetInt32()); bodies[id] = previous + row[3].GetString();
                    }
            }
            else foreach (var record in page.GetProperty("records").EnumerateArray())
            {
                var id = record.GetProperty("id").GetString()!;
                var previous = bodies.GetValueOrDefault(id, "");
                Assert.Equal(previous.Length, record.GetProperty("bodyOffset").GetInt32());
                bodies[id] = previous + record.GetProperty("body").GetString();
            }
            cursor = page.GetProperty("nextCursor").GetString(); if (cursor is null) break;
        }
        Assert.Null(cursor); Assert.True(page.GetProperty("deliveryComplete").GetBoolean());
        Assert.Equal("walrus evidence request", bodies["record"]);
        Assert.Equal("walrus evidence " + new string('a', 1200), bodies["final"]);
        Assert.Equal(original, SHA256.HashData(File.ReadAllBytes(fixture.Database)));
    }

    [Theory]
    [InlineData("2025-11-25")]
    [InlineData("2024-11-05")]
    public async Task Catalog_and_progressive_reads_are_read_only_and_keep_original_references(string version)
    {
        using var fixture = new Fixture();
        fixture.Seed().Dispose();
        var original = SHA256.HashData(File.ReadAllBytes(fixture.Database));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var client = await fixture.Connect(version, timeout.Token);
        var tools = await client.ListToolsAsync(cancellationToken: timeout.Token);
        Assert.Equal(HistoryTools.Definitions.Select(t => t.Name).Order(), tools.Select(t => t.Name).Order());
        Assert.All(tools, tool => { Assert.True(tool.ProtocolTool.Annotations?.ReadOnlyHint); Assert.False(tool.ProtocolTool.Annotations?.DestructiveHint); });
        var search = await Read(client, "search_history", new { query = "walrus", limit = 1 }, timeout.Token);
        Assert.Equal(2, search.GetProperty("total").GetInt32());
        var record = Assert.Single(search.GetProperty("records").EnumerateArray());
        Assert.Equal(128, record.GetProperty("reference").GetProperty("sourceByteOffset").GetInt64());
        var digest = await Read(client, "get_session_digest", new { sourceId = "source", sessionId = "session", limit = 1 }, timeout.Token);
        Assert.Equal("record", Assert.Single(digest.GetProperty("records").EnumerateArray()).GetProperty("id").GetString());
        Assert.Equal(1, digest.GetProperty("nextOffset").GetInt32());
        var second = await Read(client, "get_session_digest", new { sourceId = "source", sessionId = "session", offset = 1 }, timeout.Token);
        Assert.Equal("final", Assert.Single(second.GetProperty("records").EnumerateArray()).GetProperty("id").GetString());
        var exchange = await Read(client, "get_exchange", new { exchangeId = "exchange", bodyCharacters = 100 }, timeout.Token);
        Assert.Equal(2, exchange.GetProperty("total").GetInt32());
        Assert.True(exchange.GetProperty("records")[1].GetProperty("nextBodyOffset").GetInt32() > 0);
        var activity = await Read(client, "get_project_activity", new { }, timeout.Token);
        Assert.Equal(1, activity.GetProperty("total").GetInt32());
        var details = await Read(client, "get_session_details", new { sourceId = "source", sessionId = "session" }, timeout.Token);
        Assert.Equal(2, details.GetProperty("sessions")[0].GetProperty("messages").GetInt32());
        Assert.DoesNotContain("private-secret", details.GetRawText());
        Assert.Equal("https://github.com/example/project", details.GetProperty("projects")[0].GetProperty("repositories")[0].GetProperty("urls")[0].GetString());
        Assert.Equal(original, SHA256.HashData(File.ReadAllBytes(fixture.Database)));
    }

    [Fact]
    public async Task Operator_scope_cannot_be_expanded_and_revocation_takes_effect_on_existing_client()
    {
        using var fixture = new Fixture();
        using var writer = fixture.Seed();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var client = await fixture.Connect("2025-11-25", timeout.Token, "--project", "project", "--source", "source", "--session", "session", "--since", "2026-10-20T00:00:00Z", "--until", "2026-10-21T00:00:00Z");
        var visible = await Read(client, "search_history", new { query = "walrus" }, timeout.Token);
        Assert.Equal(2, visible.GetProperty("total").GetInt32());
        var narrowed = await Read(client, "search_history", new { query = "walrus", projectId = "other" }, timeout.Token);
        Assert.Equal(0, narrowed.GetProperty("total").GetInt32());
        writer.SetProjectApproval("project", false);
        foreach (var (tool, arguments) in new (string, object)[] {
            ("search_history", new { query = "walrus" }),
            ("get_session_digest", new { sourceId = "source", sessionId = "session" }),
            ("get_exchange", new { exchangeId = "exchange" }),
            ("get_project_activity", new { }),
        })
        {
            var result = await Read(client, tool, arguments, timeout.Token);
            Assert.Equal(0, result.GetProperty("total").GetInt32());
            Assert.DoesNotContain("walrus evidence", result.GetRawText());
        }
        var denied = await Call(client, "get_session_details", new { sourceId = "source", sessionId = "session" }, timeout.Token);
        Assert.True(denied.IsError);
    }

    [Fact]
    public async Task Bounded_arguments_injection_and_cancelled_requests_leave_later_calls_usable()
    {
        using var fixture = new Fixture();
        fixture.Seed().Dispose();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var client = await fixture.Connect("2025-11-25", timeout.Token);
        foreach (var arguments in new object[] { new { query = "walrus", limit = 100 }, new { query = "walrus", command = "delete" }, new { query = "walrus", from = "yesterday" }, new { query = new string('x', 17000) } })
            Assert.True((await Call(client, "search_history", arguments, timeout.Token)).IsError);
        var punctuation = await Read(client, "search_history", new { query = "\" OR * --" }, timeout.Token);
        Assert.Equal(0, punctuation.GetProperty("total").GetInt32());
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Read(client, "search_history", new { query = "walrus" }, timeout.Token)));
        Assert.All(results, result => Assert.Equal(2, result.GetProperty("total").GetInt32()));
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Call(client, "search_history", new { query = "walrus" }, cancelled.Token));
        Assert.False((await Call(client, "search_history", new { query = "walrus" }, timeout.Token)).IsError);
    }

    [Fact]
    public async Task Oversized_frame_exits_without_database_changes_or_echoing_input()
    {
        using var fixture = new Fixture(); fixture.Seed().Dispose();
        var original = SHA256.HashData(File.ReadAllBytes(fixture.Database));
        using var process = fixture.Start("--data-dir", fixture.Root);
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        try { await process.StandardInput.WriteLineAsync(new string('x', 262145)).WaitAsync(TimeSpan.FromSeconds(15)); await process.StandardInput.FlushAsync(); }
        catch (IOException) { }
        process.StandardInput.Close();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
        Assert.NotEqual(0, process.ExitCode); Assert.Empty(await output);
        Assert.Contains("256 KiB", await error); Assert.True((await error).Length < 500);
        Assert.Equal(original, SHA256.HashData(File.ReadAllBytes(fixture.Database)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(999)]
    public async Task Missing_or_incompatible_database_is_never_created_or_upgraded(int version)
    {
        using var fixture = new Fixture();
        byte[]? original = null;
        if (version > 0)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(fixture.Database)!);
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = fixture.Database, Pooling = false }.ToString());
            connection.Open(); using var command = connection.CreateCommand();
            command.CommandText = $"CREATE TABLE preserved(value TEXT); INSERT INTO preserved VALUES('untouched'); PRAGMA user_version={version};"; command.ExecuteNonQuery();
            connection.Close(); original = SHA256.HashData(File.ReadAllBytes(fixture.Database));
        }
        using var process = fixture.Start("--data-dir", fixture.Root); process.StandardInput.Close();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
        Assert.NotEqual(0, process.ExitCode); Assert.Empty(await process.StandardOutput.ReadToEndAsync());
        Assert.NotEmpty(await process.StandardError.ReadToEndAsync());
        if (original is not null) Assert.Equal(original, SHA256.HashData(File.ReadAllBytes(fixture.Database)));
        else Assert.False(Directory.Exists(fixture.Root));
    }

    [Fact]
    public async Task Stdin_eof_exits_cleanly()
    {
        using var fixture = new Fixture(); fixture.Seed().Dispose();
        using var process = fixture.Start("--data-dir", fixture.Root); process.StandardInput.Close();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(0, process.ExitCode); Assert.Empty(await process.StandardOutput.ReadToEndAsync());
    }

    [Fact]
    public async Task Frame_limit_is_enforced_across_reads_and_reset_between_frames()
    {
        await using var stream = new BoundedLineStream(new MemoryStream(Encoding.UTF8.GetBytes("1234\n12345\n123456")), 5);
        var buffer = new byte[3]; var consumed = 0;
        await Assert.ThrowsAsync<IOException>(async () => { int read; while ((read = await stream.ReadAsync(buffer)) > 0) consumed += read; });
        Assert.True(consumed >= 12);
    }

    [Fact]
    public void NewClientsUseTheirOwnTimezoneAndExistingExplicitChoicesRemainSupported()
    {
        Assert.Equal(TimeZoneInfo.Local.Id, McpOptions.Parse([]).TimeZone.Id);
        Assert.Equal(TimeZoneInfo.Local.Id, McpOptions.Parse(["--time-zone", "local"]).TimeZone.Id);
        Assert.Equal(TimeZoneInfo.Utc.Id, McpOptions.Parse(["--time-zone", "UTC"]).TimeZone.Id);
        Assert.Equal("Europe/Amsterdam", McpOptions.Parse(["--time-zone", "Europe/Amsterdam"]).TimeZone.Id);
    }

    [Fact]
    public void Invalid_options_do_not_fall_back_to_unscoped_default_workspace()
    {
        foreach (var options in new[] { new[] { "--data-dir", "relative" }, ["--project"], ["--demo"], ["--project", "a", "--project", "b"], ["--http"], ["--database", "relative"], ["--database", Path.GetFullPath("any.db"), "--live"], ["--since", "yesterday"], ["--since", "2026-10-21T00:00:00Z", "--until", "2026-10-20T00:00:00Z"] })
            Assert.Throws<ArgumentException>(() => McpOptions.Parse(options));
    }

    private static Task<CallToolResult> Call(McpClient client, string tool, object arguments, CancellationToken token) => client.CallToolAsync(tool, JsonSerializer.Deserialize<Dictionary<string, object?>>(JsonSerializer.Serialize(arguments)), cancellationToken: token).AsTask();
    private static async Task<JsonElement> Read(McpClient client, string tool, object arguments, CancellationToken token)
    {
        var result = await Call(client, tool, arguments, token); Assert.False(result.IsError);
        return JsonSerializer.Deserialize<JsonElement>(string.Join("\n", result.Content.OfType<TextContentBlock>().Select(c => c.Text)));
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "Mission Control-Memory-Mcp-" + Guid.NewGuid().ToString("N"));
        public string Database => Path.Combine(Root, "live", "workspace.db");
        public static string ServerDll => typeof(McpOptions).Assembly.Location;
        public WorkspaceStore Seed()
        {
            var projectRoot = Path.Combine(Root, "project"); Directory.CreateDirectory(projectRoot);
            var store = new WorkspaceStore(Database);
            store.SaveSource(new("source", "codex", "Local Codex", Root, true));
            store.SaveProject(new("project", "Project", "#123456", "Tests only", projectRoot)); store.ApplyProjectDiscovery(new(projectRoot, "Project", [new(projectRoot, ["example/project"])], [])); store.SetProjectApproval("project", true);
            store.SetSetting("private.test", "private-secret");
            store.SetSourceStatus("source", "gap", "Excluded file C:/Personal/private-secret/session.jsonl is inaccessible.");
            EvidenceRecord Record(string id, string body, string author, string cwd) => new(id, "source", "stream.jsonl", "session", "project", "codex", "message", "Evidence", body, author, id == "record" ? "2026-10-20T10:00:00Z" : "2026-10-20T10:01:00Z", "2026-10-20T10:01:00Z", WorkingDirectory: cwd, DataJson: JsonSerializer.Serialize(new { schema = "memory-v1", phase = author == "user" ? "user" : "final", final = author != "user", exchangeId = "exchange", sourceRecordId = id, sourceByteOffset = 128 }));
            store.CommitBatch([Record("record", "walrus evidence request", "user", projectRoot), Record("final", "walrus evidence " + new string('a', 1200), "assistant", projectRoot), Record("private", "private-secret", "user", Path.Combine(Root, "unapproved"))]);
            return store;
        }
        public Task<McpClient> Connect(string version, CancellationToken token, params string[] scope) => McpClient.CreateAsync(new StdioClientTransport(new() { Command = "dotnet", Arguments = [ServerDll, "--data-dir", Root, .. scope], Name = "Memory MCP test", ShutdownTimeout = TimeSpan.FromSeconds(5) }), new() { ProtocolVersion = version }, cancellationToken: token);
        public Process Start(params string[] arguments)
        {
            var info = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true };
            info.ArgumentList.Add(ServerDll); foreach (var argument in arguments) info.ArgumentList.Add(argument); return Process.Start(info)!;
        }
        public void Dispose() { SqliteConnection.ClearAllPools(); if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
}




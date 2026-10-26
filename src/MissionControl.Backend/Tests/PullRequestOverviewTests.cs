using System.Text.Json;
using Xunit;

namespace MissionControl.Backend.Tests;

public sealed class PullRequestOverviewTests
{
    private static string Response(string url = "https://github.com/example/project/pull/42") => JsonSerializer.Serialize(new
    {
        data = new { viewer = new { login = "developer", pullRequests = new { totalCount = 1, nodes = new[] {
            new { number = 42, title = "A pull request", url, isDraft = true, reviewDecision = (string?)null, updatedAt = "2026-10-21T10:00:00Z", repository = new { nameWithOwner = "example/project" } }
        } } } }
    });

    [Fact]
    public async Task Overview_is_read_only_bounded_cached_and_explicitly_refreshable()
    {
        var time = new Clock(); var reader = new Reader { Json = Response() };
        using var overview = new PullRequestOverview(reader, time);
        var first = await overview.GetAsync();
        Assert.Equal("ready", first.State); Assert.Equal("developer", first.Login); Assert.Equal(1, first.Total);
        Assert.True(Assert.Single(first.Items).IsDraft);
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Same(first, await overview.GetAsync()); Assert.Equal(1, reader.Calls);
        await overview.GetAsync(refresh: true); Assert.Equal(2, reader.Calls);
        time.Advance(TimeSpan.FromMinutes(6)); await overview.GetAsync(); Assert.Equal(3, reader.Calls);
        Assert.Equal("https://github.com/example/project/pull/42", overview.ResolveLink(first.Items[0].Url).AbsoluteUri);
        Assert.Throws<ArgumentException>(() => overview.ResolveLink("https://example.com/"));
        Assert.Throws<ArgumentException>(() => overview.ResolveLink("https://github.com/example/project/pull/43"));
        var info = GitHubOverviewReader.StartInfo();
        Assert.Equal(GitHubOverviewReader.Executable(), info.FileName); Assert.False(info.UseShellExecute); Assert.True(info.CreateNoWindow);
        Assert.Equal(new[] { "api", "graphql", "--hostname", "github.com", "--raw-field", "query=" + GitHubOverviewReader.Query }, info.ArgumentList);
        Assert.StartsWith("query ", GitHubOverviewReader.Query); Assert.DoesNotContain("mutation", GitHubOverviewReader.Query);
        Assert.Contains("states:OPEN", GitHubOverviewReader.Query); Assert.Contains("first:20", GitHubOverviewReader.Query);
        Assert.Equal("1", info.Environment["GH_PROMPT_DISABLED"]);
    }

    [Fact]
    public void FinderLaunchFindsBothHomebrewLayoutsAndAcceptsAnExplicitPath()
    {
        Assert.Equal("/opt/homebrew/bin/gh", GitHubOverviewReader.Executable(macOS: true, exists: path => path == "/opt/homebrew/bin/gh"));
        Assert.Equal("/usr/local/bin/gh", GitHubOverviewReader.Executable(macOS: true, exists: path => path == "/usr/local/bin/gh"));
        Assert.Equal("gh", GitHubOverviewReader.Executable(macOS: true, exists: _ => false));
        var custom = Path.Combine(Path.GetTempPath(), "Custom CLI", OperatingSystem.IsWindows() ? "gh.exe" : "gh");
        Assert.Equal(custom, GitHubOverviewReader.Executable(custom, true, _ => false));
        Assert.Throws<ArgumentException>(() => GitHubOverviewReader.Executable("./gh"));
    }

    [Fact]
    public async Task Failed_or_partial_responses_do_not_look_like_an_empty_success_or_leak_errors()
    {
        var time = new Clock(); var reader = new Reader { Json = Response() };
        using var overview = new PullRequestOverview(reader, time);
        await overview.GetAsync(); time.Advance(TimeSpan.FromSeconds(1));
        reader.Json = "{\"errors\":[{\"message\":\"PRIVATE ERROR CONTENT\"}]}";
        var failed = await overview.GetAsync(true);
        Assert.Equal("unavailable", failed.State); Assert.Empty(failed.Items); Assert.Null(failed.CheckedAt);
        Assert.DoesNotContain("PRIVATE ERROR", failed.Error); Assert.Null(failed.AllUrl);
        Assert.Throws<ArgumentException>(() => overview.ResolveLink("https://github.com/example/project/pull/42"));
        time.Advance(TimeSpan.FromSeconds(1)); reader.Json = Response("https://evil.example/pull/42");
        Assert.Equal("unavailable", (await overview.GetAsync(true)).State);
        time.Advance(TimeSpan.FromSeconds(1)); reader.Json = Response();
        Assert.Equal("ready", (await overview.GetAsync(true)).State);
    }

    [Fact]
    public async Task Shutdown_cancels_a_pending_cli_read()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var overview = new PullRequestOverview(new BlockingReader(started));
        var task = overview.GetAsync(); await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        overview.RequestStop(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    private sealed class Reader : IGitHubOverviewReader
    {
        public string Json = ""; public int Calls;
        public Task<string> ReadAsync(CancellationToken token) { Calls++; return Task.FromResult(Json); }
    }
    private sealed class BlockingReader(TaskCompletionSource started) : IGitHubOverviewReader
    {
        public async Task<string> ReadAsync(CancellationToken token) { started.SetResult(); await Task.Delay(Timeout.Infinite, token); return ""; }
    }
    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.Parse("2026-10-21T10:00:00Z");
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
}

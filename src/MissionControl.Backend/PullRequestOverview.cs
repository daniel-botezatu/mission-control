using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MissionControl.Backend;

/// <summary>Status says what holds the pull request up; Loop says whose move it is: "you" or "others".</summary>
internal sealed record PullRequestItem(int Number, string Title, string Repository, string Url, bool IsDraft, string? ReviewDecision, string UpdatedAt, string Status = "open", string Loop = "others", string? Author = null);
internal sealed record PullRequestSummary(string State, string? Login, int Total, IReadOnlyList<PullRequestItem> Items, string? CheckedAt, string? Error, string? AllUrl,
    int ReviewRequestTotal = 0, IReadOnlyList<PullRequestItem>? ReviewRequests = null, string? ReviewRequestsUrl = null);
internal interface IGitHubOverviewReader { Task<string> ReadAsync(CancellationToken token); }

/// <summary>A desktop-only, on-demand read through the user's official CLI. Never writes workspace data.</summary>
internal sealed class PullRequestOverview(IGitHubOverviewReader? reader = null, TimeProvider? clock = null) : IDisposable
{
    private readonly IGitHubOverviewReader _reader = reader ?? new GitHubOverviewReader();
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly SemaphoreSlim _serial = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private PullRequestSummary? _cached;
    private DateTimeOffset _attemptedAt = DateTimeOffset.MinValue;
    private int _generation;

    public async Task<PullRequestSummary> GetAsync(bool refresh = false)
    {
        var requestedAt = _clock.GetUtcNow();
        var generation = Volatile.Read(ref _generation);
        await _serial.WaitAsync(_stop.Token);
        try
        {
            var lifetime = _cached?.State == "ready" ? TimeSpan.FromMinutes(5) : TimeSpan.FromSeconds(30);
            if (_cached is not null && (generation != _generation || !refresh && requestedAt - _attemptedAt < lifetime)) return _cached;
            _attemptedAt = requestedAt;
            try { _cached = Parse(await _reader.ReadAsync(_stop.Token), _clock.GetUtcNow()); }
            catch (Exception error) when (error is GitHubOverviewException or JsonException or IOException or InvalidDataException or InvalidOperationException or KeyNotFoundException or FormatException or System.ComponentModel.Win32Exception)
            {
                var message = error is GitHubOverviewException known ? known.Message : "GitHub returned an incomplete overview. Try Refresh again.";
                _cached = new("unavailable", null, 0, [], null, message, null);
            }
            Interlocked.Increment(ref _generation);
            return _cached;
        }
        finally { _serial.Release(); }
    }

    internal static PullRequestSummary Parse(string json, DateTimeOffset checkedAt)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.TryGetProperty("errors", out _)) throw new InvalidDataException();
        var viewer = root.GetProperty("data").GetProperty("viewer");
        var login = viewer.GetProperty("login").GetString() ?? "";
        if (!Regex.IsMatch(login, @"\A[A-Za-z0-9][A-Za-z0-9-]{0,38}\z")) throw new InvalidDataException();
        var connection = viewer.GetProperty("pullRequests");
        var total = connection.GetProperty("totalCount").GetInt32();
        var nodes = connection.GetProperty("nodes");
        if (total < 0 || nodes.GetArrayLength() > 20 || nodes.GetArrayLength() > total) throw new InvalidDataException();
        var items = new List<PullRequestItem>();
        foreach (var node in nodes.EnumerateArray())
        {
            var decision = node.GetProperty("reviewDecision").GetString();
            var checks = node.GetProperty("commits").GetProperty("nodes").EnumerateArray().Select(commit => commit.GetProperty("commit").GetProperty("statusCheckRollup"))
                .Select(rollup => rollup.ValueKind == JsonValueKind.Object ? rollup.GetProperty("state").GetString() : null).LastOrDefault();
            var (status, loop) = Classify(node.GetProperty("isDraft").GetBoolean(), decision, node.GetProperty("mergeable").GetString(), checks);
            items.Add(Item(node, decision, status, loop, null));
        }
        var requested = root.GetProperty("data").GetProperty("reviewRequests");
        var requestTotal = requested.GetProperty("issueCount").GetInt32();
        var requestNodes = requested.GetProperty("nodes");
        if (requestTotal < 0 || requestNodes.GetArrayLength() > 20) throw new InvalidDataException();
        var requests = new List<PullRequestItem>();
        // Search results that are not pull requests arrive as empty objects.
        foreach (var node in requestNodes.EnumerateArray().Where(node => node.ValueKind == JsonValueKind.Object && node.TryGetProperty("number", out _)))
        {
            var author = node.GetProperty("author") is { ValueKind: JsonValueKind.Object } who ? who.GetProperty("login").GetString() : null;
            requests.Add(Item(node, null, "review_requested", "you", author is not null && Regex.IsMatch(author, @"\A[A-Za-z0-9][A-Za-z0-9-]{0,38}(?:\[bot\])?\z") ? author : "ghost"));
        }
        var all = "https://github.com/search?type=pullrequests&q=" + Uri.EscapeDataString($"is:pr is:open author:{login}");
        var reviews = "https://github.com/search?type=pullrequests&q=" + Uri.EscapeDataString($"is:pr is:open archived:false review-requested:{login}");
        return new("ready", login, total, items, checkedAt.ToString("O"), null, all, requestTotal, requests, reviews);
    }

    /// <summary>The first thing holding a pull request up, and whose move it is.</summary>
    internal static (string Status, string Loop) Classify(bool draft, string? decision, string? mergeable, string? checks) =>
        draft ? ("draft", "you")
        : decision == "CHANGES_REQUESTED" ? ("changes_requested", "you")
        : mergeable == "CONFLICTING" ? ("conflict", "you")
        : checks is "FAILURE" or "ERROR" ? ("checks_failing", "you")
        : decision == "APPROVED" ? ("approved", "you")
        : decision == "REVIEW_REQUIRED" ? ("review_required", "others")
        // No required review: nothing blocks it, so it waits on someone choosing to review or merge.
        : ("open", "others");

    private static PullRequestItem Item(JsonElement node, string? decision, string status, string loop, string? author)
    {
        var number = node.GetProperty("number").GetInt32();
        var repository = node.GetProperty("repository").GetProperty("nameWithOwner").GetString() ?? "";
        var url = node.GetProperty("url").GetString() ?? "";
        if (number < 1 || !Regex.IsMatch(repository, @"\A[A-Za-z0-9-]+/[A-Za-z0-9_.-]+\z")
            || url != $"https://github.com/{repository}/pull/{number}") throw new InvalidDataException();
        var title = node.GetProperty("title").GetString() ?? "";
        var updated = node.GetProperty("updatedAt").GetString() ?? "";
        if (title.Length > 2048 || !DateTimeOffset.TryParse(updated, out _)) throw new InvalidDataException();
        return new(number, title, repository, url, node.GetProperty("isDraft").GetBoolean(), decision, updated, status, loop, author);
    }

    public Uri ResolveLink(string? value)
    {
        var cached = _cached;
        if (cached?.State != "ready" || value is null
            || value != cached.AllUrl && value != cached.ReviewRequestsUrl && !cached.Items.Concat(cached.ReviewRequests ?? []).Any(item => item.Url == value))
            throw new ArgumentException("Choose a pull request from your loaded overview.");
        return new Uri(value);
    }

    public void RequestStop() => _stop.Cancel();
    public void Dispose() { _stop.Dispose(); _serial.Dispose(); }
}

internal sealed class GitHubOverviewException(string message) : Exception(message);

internal sealed class GitHubOverviewReader(Func<string?>? configuredPath = null) : IGitHubOverviewReader
{
    internal const string Query = "query MissionControlOpenLoops { viewer { login pullRequests(first:20, states:OPEN, orderBy:{field:UPDATED_AT,direction:DESC}) { totalCount nodes { number title url isDraft reviewDecision mergeable updatedAt repository { nameWithOwner } commits(last:1) { nodes { commit { statusCheckRollup { state } } } } } } } "
        + "reviewRequests: search(type:ISSUE, first:20, query:\"is:pr is:open archived:false review-requested:@me\") { issueCount nodes { ... on PullRequest { number title url isDraft updatedAt repository { nameWithOwner } author { login } } } } }";

    internal static string Executable(string? configured = null, bool? macOS = null, Func<string, bool>? exists = null)
    {
        var selected = string.IsNullOrWhiteSpace(configured) ? Environment.GetEnvironmentVariable("MISSION_CONTROL_GH_EXECUTABLE") : configured;
        if (!string.IsNullOrWhiteSpace(selected)) return MissionControl.Core.WorkspacePaths.Absolute(selected);
        // Finder/LaunchAgent processes do not inherit the interactive shell's Homebrew PATH.
        if (macOS ?? OperatingSystem.IsMacOS())
            foreach (var candidate in new[] { "/opt/homebrew/bin/gh", "/usr/local/bin/gh" })
                if ((exists ?? File.Exists)(candidate)) return candidate;
        return "gh";
    }

    internal static ProcessStartInfo StartInfo(string? configured = null)
    {
        var info = new ProcessStartInfo(Executable(configured)) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        foreach (var argument in new[] { "api", "graphql", "--hostname", "github.com", "--raw-field", "query=" + Query }) info.ArgumentList.Add(argument);
        info.Environment["GH_PROMPT_DISABLED"] = "1";
        info.Environment.Remove("GH_DEBUG");
        return info;
    }

    public async Task<string> ReadAsync(CancellationToken token)
    {
        using var process = new Process { StartInfo = StartInfo(configuredPath?.Invoke()) };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        var started = false;
        try
        {
            try { started = process.Start(); }
            catch (System.ComponentModel.Win32Exception) { throw new GitHubOverviewException("Install GitHub CLI and run gh auth login to show your open pull requests."); }
            process.StandardInput.Close();
            var output = ReadBoundedAsync(process.StandardOutput, 128 * 1024, timeout.Token);
            var errors = ReadBoundedAsync(process.StandardError, 16 * 1024, timeout.Token);
            await Task.WhenAll(output, errors, process.WaitForExitAsync(timeout.Token));
            if (process.ExitCode != 0)
            {
                var authentication = process.ExitCode == 4 || errors.Result.Contains("gh auth login", StringComparison.OrdinalIgnoreCase) || errors.Result.Contains("HTTP 401", StringComparison.OrdinalIgnoreCase);
                throw new GitHubOverviewException(authentication
                    ? "Sign in to GitHub CLI with gh auth login, then Refresh this overview."
                    : "GitHub could not be reached or access was refused. Check your connection and GitHub CLI account, then Refresh.");
            }
            return output.Result;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new GitHubOverviewException("GitHub took too long to respond. Refresh to try again."); }
        finally
        {
            if (started && !process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, int limit, CancellationToken token)
    {
        var result = new StringBuilder();
        var buffer = new char[4096];
        int length;
        while ((length = await reader.ReadAsync(buffer.AsMemory(), token)) > 0)
        {
            if (result.Length + length > limit) throw new InvalidDataException("GitHub overview exceeded its transfer limit.");
            result.Append(buffer, 0, length);
        }
        return result.ToString();
    }
}

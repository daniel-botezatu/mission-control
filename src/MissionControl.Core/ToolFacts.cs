using System.Globalization;
using System.Text.RegularExpressions;

namespace MissionControl.Core;

/// <summary>
/// Short, verifiable facts a final reply may not repeat: a pull request a command created, commits and pushes it
/// made, and test-run summaries. A fact is taken from a shell call's output only when that call performed the
/// operation (<c>gh pr create</c>, <c>git commit</c>, <c>git push</c>, a test runner), so a log or file it merely
/// printed yields nothing. Each fact names the operation and when its output was observed. Raw tool output itself
/// is never stored; only these one-line facts are, attached to the turn's final reply.
/// </summary>
public static class ToolFacts
{
    public const int MaximumPerTurn = 12;
    private const int Head = 64 * 1024, Tail = 192 * 1024;
    private static readonly TimeSpan Limit = TimeSpan.FromMilliseconds(100);
    private const RegexOptions Options = RegexOptions.CultureInvariant | RegexOptions.NonBacktracking;

    // What a shell command does, judged from its text: a program or subcommand counts only as a whole command word,
    // so `cat pytest.log` runs no tests and `git commit-tree` makes no commit. Global options such as -C <path>,
    // -c name="A B" or --no-pager may precede a git subcommand; a word may contain quoted parts.
    private const string Before = @"(?:^|[\s;&|(""'/\\])", After = @"(?:$|[\s;&|)""'])";
    private const string Word = @"(?:[^\s""']|""[^""]*""|'[^']*')+";
    private const string GitPrefix = Before + @"git(?:\s+(?:-[Cc]\s+" + Word + @"|--no-pager|-P|--(?:git-dir|work-tree)=" + Word + @"))*\s+";
    private static readonly Regex CreatesPullRequest = new(Before + @"gh\s+pr\s+create" + After, Options, Limit);
    private static readonly Regex Commits = new(GitPrefix + "commit" + After, Options, Limit);
    private static readonly Regex Pushes = new(GitPrefix + "push" + After, Options, Limit);
    private static readonly string[] Keywords = ["git", "gh", "test", "jest", "pest", "phpunit"];
    private static readonly Regex RunsTests =new(Before + @"(?:dotnet\s+test|pytest|py\.test|unittest|cargo\s+(?:test|nextest)|(?:npm|pnpm|yarn|bun)\s+(?:run\s+)?test|jest|vitest|phpunit|pest|artisan\s+test|composer\s+(?:run\s+)?test)" + After, Options | RegexOptions.IgnoreCase, Limit);

    private static readonly Regex PullRequest = new(@"https://github\.com/([A-Za-z0-9_.-]+)/([A-Za-z0-9_.-]+)/pull/(\d+)", Options, Limit);
    private static readonly Regex Commit = new(@"(?m)^\[([^\]\s]+)(?: \(root-commit\))? ([0-9a-f]{7,40})\] ([^\r\n]{1,120})", Options, Limit);
    private static readonly Regex PushTarget = new(@"(?m)^To (?:https://github\.com/|git@github\.com:)([A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+?)(?:\.git)?\r?$", Options, Limit);
    private static readonly Regex PushedRef = new(@"(?m)^ [ +*] ?(?:[0-9a-f]+\.\.\.?[0-9a-f]+|\[new branch\]|\[new tag\])\s+(\S+) -> (\S+)", Options, Limit);
    private static readonly Regex DotNet = new(@"(Passed|Failed)!\s+-\s+Failed:\s+(\d+), Passed:\s+(\d+), Skipped:\s+(\d+), Total:\s+(\d+)[^\r\n]*?- ([A-Za-z0-9_.-]+\.dll)", Options, Limit);
    private static readonly Regex Pytest = new(@"(?m)^=+ ([^=\r\n]*\b\d+ (?:passed|failed)\b[^=\r\n]*) in [0-9.]+s[^=\r\n]* =+\r?$", Options, Limit);
    private static readonly Regex Cargo = new(@"test result: (ok|FAILED)\. (\d+) passed; (\d+) failed", Options, Limit);
    private static readonly Regex JestLike = new(@"(?m)^\s*Tests:?\s+((?:\d+ (?:failed|passed|skipped|todo|pending|total)[,\s]*)+)", Options, Limit);
    private static readonly Regex PhpUnitOk = new(@"OK \((\d+) tests?, (\d+) assertions?\)", Options, Limit);
    private static readonly Regex PhpUnitFail = new(@"Tests: (\d+), Assertions: (\d+), (?:Errors: \d+, )?Failures: (\d+)", Options, Limit);

    /// <summary>The fact-producing operations a shell command performs ("pr commit push test"); empty when none.</summary>
    public static string Operations(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return "";
        var text = command.Length <= 16 * 1024 ? command : command[..(16 * 1024)];
        // Every recognised command contains one of these; most tool calls contain none and skip the patterns.
        if (!Keywords.Any(keyword => text.Contains(keyword, StringComparison.OrdinalIgnoreCase))) return "";
        try
        {
            var operations = new List<string>(4);
            if (CreatesPullRequest.IsMatch(text)) operations.Add("pr");
            if (Commits.IsMatch(text)) operations.Add("commit");
            if (Pushes.IsMatch(text)) operations.Add("push");
            if (RunsTests.IsMatch(text)) operations.Add("test");
            return string.Join(' ', operations);
        }
        catch (RegexMatchTimeoutException) { return ""; }
    }

    /// <summary>Facts in one call's output for the operations that call performed, labelled with when they were observed.</summary>
    public static IReadOnlyList<string> From(string? output, string? operations, DateTimeOffset observedAt)
    {
        if (string.IsNullOrWhiteSpace(output) || string.IsNullOrWhiteSpace(operations)) return [];
        var performed = operations.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        var text = output.Length <= Head + Tail ? output : output[..Head] + "\n" + output[^Tail..];
        var when = observedAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC";
        var facts = new List<string>();
        void Add(string fact, string operation) => facts.Add($"{fact} ({operation}, {when})");
        try
        {
            // `gh pr create` prints the new pull request's URL as its last line.
            if (performed.Contains("pr") && PullRequest.Match(text.TrimEnd().Split('\n')[^1].Trim()) is { Success: true } pr)
                Add($"PR {pr.Groups[1].Value}/{pr.Groups[2].Value}#{pr.Groups[3].Value} {pr.Value}", "gh pr create");
            if (performed.Contains("commit"))
                foreach (Match commit in Commit.Matches(text).Take(3))
                    Add($"commit {commit.Groups[2].Value[..Math.Min(12, commit.Groups[2].Value.Length)]} on {commit.Groups[1].Value}: {commit.Groups[3].Value.Trim()}", "git commit");
            if (performed.Contains("push") && PushTarget.Match(text) is { Success: true } target)
                foreach (Match pushed in PushedRef.Matches(text).Take(2))
                    Add($"pushed {pushed.Groups[2].Value} to {target.Groups[1].Value}", "git push");
            if (performed.Contains("test"))
            {
                foreach (Match run in DotNet.Matches(text).TakeLast(6))
                    Add($"tests {run.Groups[6].Value}: {run.Groups[3].Value} passed, {run.Groups[2].Value} failed, {run.Groups[4].Value} skipped", "test run");
                if (Pytest.Matches(text).LastOrDefault() is { } pytest) Add("tests (pytest): " + pytest.Groups[1].Value.Trim(), "test run");
                foreach (Match cargo in Cargo.Matches(text).TakeLast(2)) Add($"tests (cargo): {cargo.Groups[2].Value} passed, {cargo.Groups[3].Value} failed", "test run");
                if (JestLike.Matches(text).LastOrDefault() is { } jest) Add("tests: " + jest.Groups[1].Value.Trim().TrimEnd(','), "test run");
                if (PhpUnitOk.Matches(text).LastOrDefault() is { } ok) Add($"tests (PHPUnit): {ok.Groups[1].Value} passed, 0 failed", "test run");
                if (PhpUnitFail.Matches(text).LastOrDefault() is { } failed) Add($"tests (PHPUnit): {failed.Groups[1].Value} run, {failed.Groups[3].Value} failed", "test run");
            }
        }
        catch (RegexMatchTimeoutException) { }
        return facts.Select(fact => Redactor.Clean(fact, 300)).Distinct().ToArray();
    }

    /// <summary>Adds facts to a turn's pending list, keeping the newest when the per-turn bound is reached.</summary>
    public static List<string> Merge(List<string>? pending, IEnumerable<string> facts)
    {
        var merged = pending ?? [];
        foreach (var fact in facts)
        {
            merged.Remove(fact);
            merged.Add(fact);
        }
        if (merged.Count > MaximumPerTurn) merged.RemoveRange(0, merged.Count - MaximumPerTurn);
        return merged;
    }

    public static string Footer(List<string>? facts) => facts is { Count: > 0 } ? "\n\n[Results of commands run in this turn: " + string.Join("; ", facts) + "]" : "";
}

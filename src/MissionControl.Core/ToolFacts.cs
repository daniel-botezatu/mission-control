using System.Text.RegularExpressions;

namespace MissionControl.Core;

/// <summary>
/// Short, verifiable facts taken from tool output that a final reply may not repeat: a pull request a
/// command created or showed, commits and pushes, and test-run summaries. Raw tool output itself is
/// never stored; only these one-line facts are, attached to the turn's final reply.
/// </summary>
public static class ToolFacts
{
    public const int MaximumPerTurn = 12;
    private const int Head = 64 * 1024, Tail = 192 * 1024;
    private static readonly TimeSpan Limit = TimeSpan.FromMilliseconds(100);
    private const RegexOptions Options = RegexOptions.CultureInvariant | RegexOptions.NonBacktracking;

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

    public static IReadOnlyList<string> From(string? output)
    {
        if (string.IsNullOrWhiteSpace(output)) return [];
        var text = output.Length <= Head + Tail ? output : output[..Head] + "\n" + output[^Tail..];
        var facts = new List<string>();
        try
        {
            // A PR URL on the last line is what `gh pr create` and similar commands print; URLs inside
            // longer listings are not facts about this turn's work.
            var lastLine = text.TrimEnd().Split('\n')[^1].Trim();
            if (PullRequest.Match(lastLine) is { Success: true } pr)
                facts.Add($"PR {pr.Groups[1].Value}/{pr.Groups[2].Value}#{pr.Groups[3].Value} {pr.Value}");
            foreach (Match commit in Commit.Matches(text).Take(3))
                facts.Add($"commit {commit.Groups[2].Value[..Math.Min(12, commit.Groups[2].Value.Length)]} on {commit.Groups[1].Value}: {commit.Groups[3].Value.Trim()}");
            if (PushTarget.Match(text) is { Success: true } target)
                foreach (Match pushed in PushedRef.Matches(text).Take(2))
                    facts.Add($"pushed {pushed.Groups[2].Value} to {target.Groups[1].Value}");
            foreach (Match run in DotNet.Matches(text).TakeLast(6))
                facts.Add($"tests {run.Groups[6].Value}: {run.Groups[3].Value} passed, {run.Groups[2].Value} failed, {run.Groups[4].Value} skipped");
            if (Pytest.Matches(text).LastOrDefault() is { } pytest) facts.Add("tests (pytest): " + pytest.Groups[1].Value.Trim());
            foreach (Match cargo in Cargo.Matches(text).TakeLast(2)) facts.Add($"tests (cargo): {cargo.Groups[2].Value} passed, {cargo.Groups[3].Value} failed");
            if (JestLike.Matches(text).LastOrDefault() is { } jest) facts.Add("tests: " + jest.Groups[1].Value.Trim().TrimEnd(','));
            if (PhpUnitOk.Matches(text).LastOrDefault() is { } ok) facts.Add($"tests (PHPUnit): {ok.Groups[1].Value} passed, 0 failed");
            if (PhpUnitFail.Matches(text).LastOrDefault() is { } failed) facts.Add($"tests (PHPUnit): {failed.Groups[1].Value} run, {failed.Groups[3].Value} failed");
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

    public static string Footer(List<string>? facts) => facts is { Count: > 0 } ? "\n\n[Tool results this turn: " + string.Join("; ", facts) + "]" : "";
}

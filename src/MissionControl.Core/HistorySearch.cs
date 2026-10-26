using System.Text.RegularExpressions;

namespace MissionControl.Core;

internal static class HistorySearch
{
    private static readonly Regex Words = new(@"[\p{L}\p{N}_]+", RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(50));
    private static readonly string[][] Synonyms = [
        ["authentication", "authenticate", "auth", "login", "signin"],
        ["database", "db", "sqlite"], ["failure", "failed", "error", "exception"],
        ["retry", "retries", "retrying"], ["cancel", "cancellation", "abort", "aborted"],
        ["redact", "redaction", "redacting"], ["timeout", "timeouts"],
        ["unfinished", "pending", "remaining"], ["decision", "decisions", "decided"],
        ["pullrequest", "pr"], ["index", "indexing", "indexed"], ["migration", "migrations"]
    ];
    public static string Term(string value) => "\"" + value + "\"*";
    public static string Alternatives(string word) => "(" + string.Join(" OR ",
        (Synonyms.FirstOrDefault(group => group.Contains(word, StringComparer.OrdinalIgnoreCase)) ?? [word]).Select(Term)) + ")";

    public static string? Correct(string query, IEnumerable<string> vocabulary)
    {
        var tokens = Words.Matches(query[..Math.Min(query.Length, 2048)]).Select(m => m.Value[..Math.Min(m.Length, 64)]).Distinct(StringComparer.OrdinalIgnoreCase).Take(16).ToArray();
        var words = vocabulary.SelectMany(text => Words.Matches(text).Select(m => m.Value.ToLowerInvariant()))
            .Where(word => word.Length is >= 3 and <= 64).Distinct(StringComparer.Ordinal).Take(10000).ToArray();
        var changed = false;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        for (var index = 0; index < tokens.Length; index++)
        {
            var token = tokens[index].ToLowerInvariant();
            if (token.Length < 4 || words.Any(word => word.StartsWith(token, StringComparison.Ordinal))) continue;
            var best = token.Length > 5 ? 3 : 2;
            string? replacement = null;
            foreach (var word in words.Where(w => w[0] == token[0] && Math.Abs(w.Length - token.Length) < best))
            {
                if (clock.ElapsedMilliseconds > 200) break;
                var distance = Distance(token, word);
                if (distance < best) { best = distance; replacement = word; }
            }
            if (replacement is null) continue;
            tokens[index] = replacement; changed = true;
        }
        return changed ? string.Join(' ', tokens) : null;
    }

    private static int Distance(string left, string right)
    {
        var previous = Enumerable.Range(0, right.Length + 1).ToArray();
        var prior = previous;
        for (var i = 1; i <= left.Length; i++)
        {
            var current = new int[right.Length + 1]; current[0] = i;
            for (var j = 1; j <= right.Length; j++)
            {
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + (left[i - 1] == right[j - 1] ? 0 : 1));
                if (i > 1 && j > 1 && left[i - 1] == right[j - 2] && left[i - 2] == right[j - 1]) current[j] = Math.Min(current[j], prior[j - 2] + 1);
            }
            prior = previous; previous = current;
        }
        return previous[right.Length];
    }
}

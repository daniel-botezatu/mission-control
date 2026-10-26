using System.Text.RegularExpressions;

namespace MissionControl.Core;

public sealed partial class WorkspaceStore
{
    private static void AddSearchPredicate(string? search, List<string> terms, List<(string, object?)> parameters)
    {
        if (string.IsNullOrWhiteSpace(search)) return;
        var tokens = Regex.Matches(search[..Math.Min(2048, search.Length)], @"[\p{L}\p{N}_]+")
            .Select(match => match.Value[..Math.Min(64, match.Value.Length)]).Distinct(StringComparer.OrdinalIgnoreCase).Take(16).ToArray();
        if (tokens.Length == 0) { terms.Add("0=1"); return; }

        // A search may combine an original message word with a manually supplied task/note.
        // Each token must match, but its match can come from any of those separately stored sources.
        for (var index = 0; index < tokens.Length; index++)
        {
            var parameter = "$search" + index;
            terms.Add($"""
                r.id IN (
                    SELECT id FROM evidence WHERE rowid IN (SELECT rowid FROM evidence_fts WHERE evidence_fts MATCH {parameter})
                    UNION SELECT record_id FROM corrections WHERE rowid IN (SELECT rowid FROM corrections_fts WHERE corrections_fts MATCH {parameter})

                )
                """);
            parameters.Add((parameter, HistorySearch.Alternatives(tokens[index])));
        }
    }

    private string? CorrectSearch(string search, string where, (string, object?)[] parameters)
    {
        using var command = Command("SELECT r.title,substr(r.body,1,2048) FROM evidence r LEFT JOIN corrections c ON c.record_id=r.id " + where + " ORDER BY r.timestamp DESC LIMIT 400", parameters);
        command.CommandTimeout = 5;
        using var reader = command.ExecuteReader();
        var text = new List<string>();
        while (reader.Read()) { text.Add(reader.GetString(0)); text.Add(reader.GetString(1)); }
        return HistorySearch.Correct(search, text);
    }
}

using System.Globalization;
using System.Text.RegularExpressions;

namespace MissionControl.Core;

public sealed partial class WorkspaceStore
{
    // A reply that presents something to send: a draft heading ("**Draft reply to Maria**", "## Slack draft"), a line
    // that introduces one ("Draft review comment for Sam:"), "the message I drafted", or a quoted message addressed to
    // someone ("> @Maria …", "> Hi team"). Draft pull requests are not drafts to send.
    private static readonly Regex DraftMarker = new(
        @"^[ \t>*_#-]*(?:here(?:'s|’s| is)[ \t]+(?:a|an|the|my|your)[ \t]+)?(?:(?:suggested|proposed|revised|updated|short|shorter|final|new)[ \t]+)?(?:(?:slack|teams|email|e-mail|reply|response|message|comment|review|ticket|announcement|status|update|pr[ \t]+description)[ \t]+)?drafts?\b(?![ \t]+(?:pr|pull[ \t]+request)s?\b)"
        + @"|^[^\n]{0,160}\bdraft(?:ed)?\b(?![ \t]+(?:pr|pull[ \t]+request)s?\b)[^\n]{0,100}:[ \t*_]*$"
        + @"|\b(?:message|reply|comment|email|note|text|response)s?[ \t]+I(?:'ve|[ \t]+have)?[ \t]+drafted\b"
        + @"|\bI(?:'ve|[ \t]+have)?[ \t]+drafted[ \t]+(?:a|an|the|one|this|your)[ \t]+(?:short[ \t]+)?(?:message|reply|comment|email|note|response)\b"
        + @"|^>[ \t]*(?:@[A-Za-z][\w.-]*|hi|hey|hello)\b",
        RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(200));

    /// <summary>
    /// Final replies since <paramref name="since"/> that present a draft and end their conversation: the person never
    /// wrote in that session again. A conversation cannot show whether the draft was sent elsewhere, so this is a
    /// reminder, not proof. Dismissed or snoozed replies are left out.
    /// </summary>
    public IReadOnlyList<EvidenceRecord> UnsentDrafts(DateTimeOffset since, int limit)
    {
        lock (gate)
        {
            var exact = since.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
            // The text bound lets the timestamp index narrow the scan; julianday then compares exactly.
            var window = since.ToUniversalTime().AddDays(-1).ToString("O", CultureInfo.InvariantCulture);
            var matches = new List<string>();
            using (var command = Command($"""
                SELECT r.id,r.body FROM evidence r LEFT JOIN corrections c ON c.record_id=r.id
                    LEFT JOIN (SELECT source_id,session_id,max(julianday(timestamp)) AS last_user FROM evidence
                        WHERE kind='message' AND author='user' AND timestamp>=$window GROUP BY source_id,session_id) u
                        ON u.source_id=r.source_id AND u.session_id=r.session_id
                WHERE r.kind='message' AND r.author='assistant' AND r.timestamp>=$window AND julianday(r.timestamp)>=julianday($since)
                    AND (u.last_user IS NULL OR julianday(r.timestamp)>u.last_user)
                    AND COALESCE(c.dismissed,0)=0 AND (c.snoozed_until IS NULL OR julianday(c.snoozed_until)<=julianday('now'))
                    AND {ApprovalPredicate}
                ORDER BY r.timestamp DESC LIMIT 200
                """, ("$window", window), ("$since", exact)))
            using (var reader = command.ExecuteReader())
                while (reader.Read() && matches.Count < limit)
                    if (DraftMarker.IsMatch(reader.GetString(1))) matches.Add(reader.GetString(0));
            if (matches.Count == 0) return [];
            var parameters = matches.Select((id, index) => ("$d" + index, (object?)id)).ToArray();
            var projection = Projection.Replace("r.body", "substr(r.body,1,2000) AS body").Replace("r.data_json", "NULL AS data_json") + ",length(r.body),length(r.body)>2000";
            return ReadRecords($"SELECT {projection} FROM evidence r LEFT JOIN corrections c ON c.record_id=r.id WHERE r.id IN ({string.Join(',', parameters.Select(p => p.Item1))}) AND {ApprovalPredicate} ORDER BY r.timestamp DESC", parameters);
        }
    }
}

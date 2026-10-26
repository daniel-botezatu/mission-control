using System.Text;
using System.Text.RegularExpressions;

namespace MissionControl.Core;

/// <summary>A navigation index over every part of a message, never a semantic summary.</summary>
internal static class HistoryOutline
{
    private static readonly Regex Checklist = new(@"(?im)^\s*(?:[-*+]\s|\d+[.)]\s|#{1,6}\s)|\b(?:must|before|after|blocked|waiting|need|should|test|draft|backfill|prevent|never|do not|don't|moet|eerst|wacht)\b",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(100));

    public static string Create(string body)
    {
        if (body.Length <= 500) return "[complete message]\n" + body;
        var result = new StringBuilder("[navigation excerpts; original character ranges; read full messages for a complete recap]\n");
        var offset = 0;
        // Scan all lines, not only the conversation's opening and closing text.
        foreach (var line in body.Split('\n'))
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                var important = Checklist.IsMatch(line);
                for (var start = 0; start < line.Length;)
                {
                    var length = Math.Min(1000, line.Length - start);
                    if (start + length < line.Length && char.IsHighSurrogate(line[start + length - 1])) length--;
                    var take = important ? length : Math.Min(length, 200);
                    if (take < length && char.IsHighSurrogate(line[start + take - 1])) take--;
                    result.Append('[').Append(offset + start).Append(':').Append(length).Append("] ")
                        .Append(line.AsSpan(start, take));
                    if (take < length) result.Append(" … [excerpted]");
                    result.AppendLine();
                    start += length;
                }
            }
            offset += line.Length + 1;
        }
        return result.ToString();
    }
}

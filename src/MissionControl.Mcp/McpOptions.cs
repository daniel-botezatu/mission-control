using MissionControl.Core;

namespace MissionControl.Mcp;

internal sealed record McpOptions(string DatabasePath, HistoryScope Scope, TimeZoneInfo TimeZone)
{
    public static McpOptions Parse(string[] args)
    {
        string? dataDirectory = null, database = null, project = null, session = null, source = null, since = null, until = null;
        var mode = "live";
        var timeZone = TimeZoneInfo.Local;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < args.Length; index++)
        {
            var flag = args[index];
            if (!seen.Add(flag)) throw new ArgumentException("Duplicate MCP option.");
            string Value()
            {
                if (++index >= args.Length || string.IsNullOrWhiteSpace(args[index]) || args[index].StartsWith("--", StringComparison.Ordinal))
                    throw new ArgumentException("MCP option requires a value.");
                return args[index];
            }
            switch (flag)
            {
                case "--time-zone": var zone = Value(); timeZone = zone == "local" ? TimeZoneInfo.Local : TimeZoneInfo.FindSystemTimeZoneById(zone); break;
                case "--data-dir": dataDirectory = Value(); break;
                case "--database": database = Value(); break;
                case "--project": project = Value(); break;
                case "--session": session = Value(); break;
                case "--source": source = Value(); break;
                case "--since": since = Date(Value()); break;
                case "--until": until = Date(Value()); break;
                case "--live": mode = "live"; break;
                case "--demo": mode = "demo"; break;
                default: throw new ArgumentException("Unknown MCP option. Supported: --database <absolute file> or --data-dir <absolute directory> with --live/--demo; optional --time-zone, --project, --session, --source, --since, --until scope values.");
            }
        }
        if (seen.Contains("--live") && seen.Contains("--demo")) throw new ArgumentException("Choose either live or demo mode.");
        if (database is not null && (dataDirectory is not null || seen.Contains("--live") || seen.Contains("--demo"))) throw new ArgumentException("An explicit MCP database cannot be combined with a data directory or mode.");
        if (database is not null && !Path.IsPathFullyQualified(database)) throw new ArgumentException("MCP database must be an absolute path.");
        if (since is not null && until is not null && string.CompareOrdinal(since, until) >= 0) throw new ArgumentException("MCP time scope must end after it starts.");
        dataDirectory ??= WorkspacePaths.DefaultDataDirectory();
        if (!Path.IsPathFullyQualified(dataDirectory)) throw new ArgumentException("MCP data directory must be an absolute path.");
        if (project?.Length > 4096 || session?.Length > 4096 || source?.Length > 4096) throw new ArgumentException("MCP scope identity is too long.");
        return new(database is null ? Path.Combine(Path.GetFullPath(dataDirectory), mode, "workspace.db") : Path.GetFullPath(database), new(project, session, since, until, source), timeZone);
    }

    private static string Date(string text)
    {
        if (text.Length > 80 || !DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var parsed))
            throw new ArgumentException("MCP time scope requires valid ISO-8601 timestamps.");
        return parsed.ToUniversalTime().ToString("O");
    }
}

using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MissionControl.Core;

public sealed partial class HistoryTools
{
    private TimeZoneInfo Zone(JsonElement args)
    {
        var id = Text(args, "timeZone", maximum: 100);
        if (id is null) return defaultTimeZone ?? TimeZoneInfo.Utc;
        try { return id == "local" ? TimeZoneInfo.Local : TimeZoneInfo.FindSystemTimeZoneById(id); }
        catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException)
        { throw new ArgumentException("Unknown timeZone. Use an IANA timezone such as Europe/Amsterdam."); }
    }

    private string? Date(JsonElement args, string name)
    {
        var text = Text(args, name, maximum: 80);
        if (text is null) return null;
        DateTimeOffset parsed;
        if (DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
        {
            var zone = Zone(args);
            var midnight = day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
            if (zone.IsInvalidTime(midnight) || zone.IsAmbiguousTime(midnight))
                throw new ArgumentException("This local midnight is ambiguous or unavailable. Supply an explicit timestamp offset.");
            parsed = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(midnight, zone));
        }
        else if (!Regex.IsMatch(text, @"T.*(?:Z|[+-]\d{2}:\d{2})$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
            || !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed))
            throw new ArgumentException("Time filters require YYYY-MM-DD in timeZone or an ISO-8601 timestamp with Z/an explicit offset.");
        return parsed.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    }

    private static string? DisplayTime(string? timestamp, TimeZoneInfo zone) => timestamp is null ? null
        : TimeZoneInfo.ConvertTime(DateTimeOffset.Parse(timestamp, CultureInfo.InvariantCulture), zone)
            .ToString("O", CultureInfo.InvariantCulture);
}

namespace MissionControl.Core;

public sealed partial class WorkspaceStore
{
    // Aggregate metadata only: never return excluded paths or message content.
    private Dictionary<string, SourceProgress> ReadSourceProgress()
    {
        using var command = Command("""
            SELECT source_id,COUNT(*),SUM(offset<length),SUM(offset),SUM(length),
                SUM(MAX(0,length-offset)),MAX(updated_at)
            FROM checkpoints GROUP BY source_id
            """);
        using var reader = command.ExecuteReader();
        var result = new Dictionary<string, SourceProgress>(StringComparer.Ordinal);
        while (reader.Read()) result.Add(reader.GetString(0), new(reader.GetInt32(1), reader.GetInt32(2),
            reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5), NullableString(reader, 6)));
        return result;
    }
}

using System.Text.Json;
using System.Text.Json.Nodes;

namespace MissionControl.Providers;

/// <summary>
/// The pinned server's earlier answers to a host's startup requests (initialize and the list methods).
/// The server marks them public; they describe tools and never contain user data or credentials.
/// Entries are discarded when the server binary or the allowed tool set changes.
/// </summary>
internal sealed class StartupCache
{
    private const int MaximumBytes = 1024 * 1024;
    private readonly object gate = new();
    private readonly string path;
    private readonly string key;
    private readonly Dictionary<string, string> entries;

    internal StartupCache(string path, string key)
    {
        (this.path, this.key) = (path, key);
        entries = Load(path, key);
    }

    internal string? Get(string entry)
    {
        lock (gate) return entries.GetValueOrDefault(entry);
    }

    /// <summary>Records one answer; a cache that cannot be written only means the next session starts the server at once.</summary>
    internal void Store(string entry, string result)
    {
        lock (gate)
        {
            if (entries.TryGetValue(entry, out var current) && current == result) return;
            // Every session's launcher writes this file; keep answers other sessions added since this one loaded it.
            foreach (var (name, answer) in Load(path, key)) entries.TryAdd(name, answer);
            entries[entry] = result;
            var document = new JsonObject { ["key"] = key, ["entries"] = new JsonObject(entries.Select(item => KeyValuePair.Create(item.Key, JsonNode.Parse(item.Value)))) };
            var temporary = path + "." + Environment.ProcessId + ".tmp";
            try
            {
                File.WriteAllText(temporary, document.ToJsonString());
                File.Move(temporary, path, overwrite: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                try { File.Delete(temporary); } catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    private static Dictionary<string, string> Load(string path, string key)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length > MaximumBytes) return result;
            if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject document || document["key"]?.GetValue<string>() != key
                || document["entries"] is not JsonObject stored) return result;
            foreach (var (name, value) in stored)
                if (value is JsonObject answer) result[name] = answer.ToJsonString();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { result.Clear(); }
        return result;
    }
}

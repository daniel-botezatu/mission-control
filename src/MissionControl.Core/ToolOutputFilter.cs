using System.Text.Json;
using System.Text.Json.Nodes;

namespace MissionControl.Core;

/// <summary>
/// Claude Code PostToolUse hook for results that bypass Mission Control, such as Slack searches and
/// threads. Applies the same best-effort secret filter used for stored history to every text value
/// of the tool result and returns a replacement only when something was removed.
/// </summary>
public static class ToolOutputFilter
{
    public const int MaximumInputBytes = 16 * 1024 * 1024;

    /// <summary>The hook's JSON reply, or null when the result contains nothing to remove.</summary>
    public static string? Filter(string hookInput)
    {
        if (JsonNode.Parse(hookInput) is not JsonObject input || input["tool_response"] is not { } response) return null;
        var removed = 0;
        var filtered = Clean(response.DeepClone(), ref removed);
        if (removed == 0) return null;
        return new JsonObject
        {
            ["hookSpecificOutput"] = new JsonObject
            {
                ["hookEventName"] = "PostToolUse",
                ["updatedToolOutput"] = filtered,
                ["additionalContext"] = $"Mission Control removed {removed} secret-looking value(s) from this result. Mention that credentials were present where relevant, never their values."
            }
        }.ToJsonString();
    }

    private static JsonNode? Clean(JsonNode? node, ref int removed)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var key in obj.Select(p => p.Key).ToArray()) obj[key] = Clean(obj[key]?.DeepClone(), ref removed);
                return obj;
            case JsonArray array:
                for (var i = 0; i < array.Count; i++) array[i] = Clean(array[i]?.DeepClone(), ref removed);
                return array;
            case JsonValue value when value.GetValueKind() == JsonValueKind.String:
                var text = value.GetValue<string>();
                var clean = Redactor.Clean(text, MemoryLimits.MessageCharacters);
                if (clean == text) return value;
                removed++;
                return JsonValue.Create(clean);
            default:
                return node;
        }
    }
}

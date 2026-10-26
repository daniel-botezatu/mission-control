using System.Text.Json;
using System.Text.RegularExpressions;

namespace MissionControl.Core;

/// <summary>Separates persisted user-role envelopes from evidence of a person's own words.</summary>
internal static class TranscriptContent
{
    // Host-written wrappers that open a user-role message: injected instructions and context,
    // slash commands and their local output, shell-mode echoes, and Codex app events, review
    // results and goal continuations. None of them is text the person typed.
    private static readonly Regex Injected = new(
        @"(?i)\A\s*(?:#\s*(?:AGENTS\.md instructions|Context from my IDE setup)|<\s*(?:INSTRUCTIONS|environment_context|permissions[ _]instructions|task|system-reminder|developer|user_instructions|subagent_notification|turn_aborted|review_instructions|collaboration_mode|recommended_plugins|app_context|ide_opened_file|ide_selection|send_user_message_question_reply|command-name|command-message|command-args|local-command-stdout|local-command-stderr|local-command-caveat|bash-input|bash-stdout|bash-stderr|external_codex_apps_open_page|user_action|codex_internal_context)\b)",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));

    // Claude Code writes these user-role messages itself: interruption markers, hook and goal
    // notices, image placeholders and the summary that continues a compacted conversation.
    // Only exact harness openings match, so typed prompts that merely mention them remain.
    private static readonly Regex HarnessNotice = new(
        @"^\s*(?:\[Request interrupted by user(?: for tool use)?\]\s*$|Stop hook feedback:|A session-scoped Stop hook is now active\b|Goal check-in:|This session is being continued from a previous conversation\b|(?:\[Image(?:[:#\s][^\]]{0,2000})?\]\s*)+$)",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));

    public static bool IsHumanText(string body)
    {
        // An IDE envelope can contain a real request. Only its explicitly delimited request
        // is eligible, never the IDE context, pasted files, or embedded instructions.
        return !string.IsNullOrWhiteSpace(body) && !Injected.IsMatch(body) && !HarnessNotice.IsMatch(body);
    }

    // A Codex review request returns the reviewer model's findings inside a user_action envelope.
    private static readonly Regex Review = new(
        @"\A\s*<user_action>[\s\S]*?<action>\s*review\s*</action>[\s\S]*?<results>\s*([\s\S]*?)\s*(?:</results>|\z)",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(100));

    /// <summary>The findings text of a Codex code review envelope, or null when the message is not one.</summary>
    public static string? ReviewResult(string body)
    {
        try { return Review.Match(body) is { Success: true } match && match.Groups[1].Value.Trim() is { Length: > 0 } text ? text : null; }
        catch (RegexMatchTimeoutException) { return null; }
    }

    public static bool IsInterruption(string body) => body.Trim() is "[Request interrupted by user]" or "[Request interrupted by user for tool use]";

    public static string HumanRequest(string body)
    {
        const string marker = "## My request:";
        if (body.TrimStart().StartsWith("# Context from my IDE setup:", StringComparison.Ordinal)
            && body.IndexOf(marker, StringComparison.Ordinal) is var index && index >= 0)
            return body[(index + marker.Length)..].Trim();
        // Claude Code in VS Code prefixes the typed prompt with editor-context blocks.
        if (IdeContext.IsMatch(body) && IdeContext.Replace(body, "").Trim() is { Length: > 0 } prompt)
            return prompt;
        if (body.TrimStart().StartsWith("<send_user_message_question_reply>", StringComparison.Ordinal) && QuestionAnswers(body) is { Length: > 0 } answers)
            return answers;
        return body;
    }

    private static readonly Regex IdeContext = new(@"^\s*(?:<(ide_opened_file|ide_selection)>[\s\S]*?</\1>\s*)+",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));

    /// <summary>The person's answers in a Codex question reply, without the generated question text.</summary>
    private static string? QuestionAnswers(string body)
    {
        var start = body.IndexOf('[');
        var end = body.LastIndexOf(']');
        if (start < 0 || end <= start) return null;
        try
        {
            using var document = JsonDocument.Parse(body.AsMemory(start, end - start + 1));
            if (document.RootElement.ValueKind != JsonValueKind.Array) return null;
            var answers = document.RootElement.EnumerateArray().Take(16)
                .Select(item => item.ValueKind == JsonValueKind.Object && item.TryGetProperty("answer", out var answer) && answer.ValueKind == JsonValueKind.String ? answer.GetString()?.Trim() : null)
                .Where(answer => !string.IsNullOrEmpty(answer)).ToArray();
            return answers.Length == 0 ? null : string.Join('\n', answers);
        }
        catch (JsonException) { return null; }
    }

    public static bool HasBlock(JsonElement message, string type) => message.TryGetProperty("content", out var content)
        && content.ValueKind == JsonValueKind.Array && content.EnumerateArray().Any(block => block.ValueKind == JsonValueKind.Object
            && block.TryGetProperty("type", out var kind) && kind.ValueKind == JsonValueKind.String && kind.GetString() == type);

    /// <summary>Readable message text only. Thinking, tools, attachments, and image data never enter the index.</summary>
    public static string MessageText(JsonElement message)
    {
        if (!message.TryGetProperty("content", out var content)) return "";
        if (content.ValueKind == JsonValueKind.String) return content.GetString() ?? "";
        if (content.ValueKind != JsonValueKind.Array) return "";
        List<string> parts = [];
        var characters = 0;
        foreach (var block in content.EnumerateArray().Take(256))
        {
            if (block.ValueKind != JsonValueKind.Object || !block.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String
                || type.GetString() is not ("text" or "input_text" or "output_text") || !block.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String) continue;
            var value = text.GetString() ?? "";
            if (value.Length > 0) { parts.Add(value); characters += value.Length; }
            if (characters >= MemoryLimits.MessageCharacters) break;
        }
        return string.Join('\n', parts);
    }

}

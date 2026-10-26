using System.Text.Json;
using MissionControl.Core;

namespace MissionControl.Tests;

/// <summary>The Slack result hook removes credentials before the AI sees them and leaves clean results alone.</summary>
public sealed class ToolOutputFilterTests
{
    private static string HookInput(object toolResponse) => JsonSerializer.Serialize(new
    {
        session_id = "s", hook_event_name = "PostToolUse", tool_name = "mcp__claude_ai_Slack__slack_read_thread",
        tool_input = new { channel_id = "D123" }, tool_response = toolResponse, mcp_server = "claude_ai_Slack"
    });

    [Fact]
    public void CredentialsInASlackResultAreReplacedAndTheAiIsTold()
    {
        var response = new[] { new { type = "text", text = "Sam: staging is up. password: hunter2-very-secret\nbot token xox" + "b-123456789012-abcdefghijklmnopqrst\nsee you" } };
        var reply = ToolOutputFilter.Filter(HookInput(response));
        Assert.NotNull(reply);
        using var document = JsonDocument.Parse(reply!);
        var output = document.RootElement.GetProperty("hookSpecificOutput");
        Assert.Equal("PostToolUse", output.GetProperty("hookEventName").GetString());
        var text = output.GetProperty("updatedToolOutput")[0].GetProperty("text").GetString()!;
        Assert.DoesNotContain("hunter2-very-secret", text);
        Assert.DoesNotContain("xox" + "b-123456789012", text);
        Assert.Contains("staging is up", text);
        Assert.Contains("see you", text);
        Assert.Contains("secret-looking", output.GetProperty("additionalContext").GetString());
    }

    [Fact]
    public void ResultWithoutSecretsIsLeftUnchanged() =>
        Assert.Null(ToolOutputFilter.Filter(HookInput(new[] { new { type = "text", text = "Maria: sounds like a plan, ship it Monday." } })));

    [Fact]
    public void StructuredResultsKeepTheirShape()
    {
        var reply = ToolOutputFilter.Filter(HookInput(new { messages = new[] { new { user = "U1", text = "DB_PASSWORD=abc123secret" }, new { user = "U2", text = "ok" } }, has_more = false }));
        using var document = JsonDocument.Parse(reply!);
        var updated = document.RootElement.GetProperty("hookSpecificOutput").GetProperty("updatedToolOutput");
        Assert.False(updated.GetProperty("has_more").GetBoolean());
        Assert.Equal(2, updated.GetProperty("messages").GetArrayLength());
        Assert.DoesNotContain("abc123secret", updated.GetProperty("messages")[0].GetProperty("text").GetString());
        Assert.Equal("ok", updated.GetProperty("messages")[1].GetProperty("text").GetString());
    }
}

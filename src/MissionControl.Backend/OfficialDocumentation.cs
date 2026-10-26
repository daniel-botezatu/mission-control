namespace MissionControl.Backend;

internal static class OfficialDocumentation
{
    public static bool TryValidate(string? text, out Uri uri)
    {
        uri = null!;
        if (text is null || text.Length > 2048 || text.Any(char.IsControl) || !Uri.TryCreate(text, UriKind.Absolute, out var parsed)
            || parsed.Scheme != "https" || parsed.Port != 443 || parsed.UserInfo.Length != 0 || parsed.Query.Length != 0) return false;
        var permitted = parsed.Host is "learn.chatgpt.com" or "developers.openai.com" or "code.claude.com" or "docs.slack.dev"
            || parsed.Host == "github.com" && (parsed.AbsolutePath == "/github/github-mcp-server" || parsed.AbsolutePath.StartsWith("/github/github-mcp-server/", StringComparison.Ordinal));
        if (!permitted) return false;
        uri = parsed; return true;
    }
}

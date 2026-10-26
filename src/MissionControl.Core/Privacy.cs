using System.Text.RegularExpressions;

namespace MissionControl.Core;

/// <summary>Best-effort filtering, never a guarantee that every secret is removed.</summary>
public static class Redactor
{
    private const string SecretKey = @"(?:APP[_-]?KEY|[a-z0-9_-]*(?:api[_-]?key|access[_-]?key|access[_-]?token|refresh[_-]?token|client[_-]?secret|password|passwd|secret[_-]?key|secret|token|private[_-]?key|encryption[_-]?key))";
    private static readonly Regex PrivateKeys = new(@"-----BEGIN [^-\r\n]*PRIVATE KEY-----[\s\S]*?(?:-----END [^-\r\n]*PRIVATE KEY-----|$)", RegexOptions.Compiled | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(100));
    private static readonly Regex Tokens = new(@"\b(?:pscale_(?:pw|tkn|oauth)_[A-Za-z0-9_-]+|sk-(?:proj-|ant-)?[A-Za-z0-9_-]{12,}|gh[pousr]_[A-Za-z0-9_]{15,}|github_pat_[A-Za-z0-9_]{15,}|xox[baprs]-[A-Za-z0-9-]{10,}|AKIA[A-Z0-9]{16})\b", RegexOptions.Compiled | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(100));
    private static readonly Regex EnvironmentSecrets = new(@"(?im)(\b(?:APP_KEY|[A-Z0-9_]*(?:PASSWORD|PASSWD|SECRET|TOKEN|PRIVATE_KEY|API_KEY|ACCESS_KEY|ENCRYPTION_KEY))\s*=\s*)[^\r\n]+", RegexOptions.Compiled | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(100));
    // Upper-case environment names with a secret word anywhere, e.g. DB_PASSWORD_PROVIDER_DATA or SERVICE_TOKENS.
    // Case-sensitive so ordinary code such as max_tokens = 1500 is left readable.
    private static readonly Regex EnvironmentNames = new(@"(?m)(\b[A-Z][A-Z0-9]*(?:_[A-Z0-9]+)*_(?:PASSWORD|PASSWD|SECRET|TOKEN|KEY)S?(?:_[A-Z0-9]+)*\s*=\s*)[^\r\n]+", RegexOptions.Compiled | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(100));
    private static readonly Regex Bearer =new(@"(?i)\b(Bearer\s+)[A-Za-z0-9._~+/-]{8,}=*", RegexOptions.Compiled | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(100));
    private static readonly Regex Assignments = new("(?i)([\\\"']?\\b" + SecretKey + "[\\\"']?\\s*(?:=>|[:=])\\s*)(?:\\\"[^\\\"\\r\\n]*\\\"|'[^'\\r\\n]*'|[^\\s,;}]+)", RegexOptions.Compiled | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(100));
    private static readonly Regex EscapedAssignments = new("(?i)(\\\\\\\"\\b" + SecretKey + "\\\\\\\"\\s*:\\s*)\\\\\\\".*?\\\\\\\"", RegexOptions.Compiled | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(100));
    private static readonly Regex SensitiveKey = new("^" + SecretKey + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(100));
    private static readonly Regex XmlEnvironmentValues = new("(?is)(<(?:env|server)\\b(?=[^>]*\\bname\\s*=\\s*[\\\"']" + SecretKey + "[\\\"'])[^>]*?\\bvalue\\s*=\\s*)([\\\"'])(.*?)\\2", RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));
    private static readonly Regex UrlPassword = new(@"(?i)([a-z][a-z0-9+.-]*://[^\s/:]+:)[^\s/@]+(@)", RegexOptions.Compiled | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(100));

    public static string Clean(string text, int max = 12000)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;
        max = Math.Clamp(max, 32, MemoryLimits.MessageCharacters);
        // Input is already bounded by ingestion/IPC boundaries. Cap this boundary too.
        var bounded = text.Length > MemoryLimits.MessageCharacters ? text[..MemoryLimits.MessageCharacters] + "\n[truncated]" : text;
        try
        {
            bounded = PrivateKeys.Replace(bounded, "[REDACTED PRIVATE KEY]");
            bounded = Tokens.Replace(bounded, "[REDACTED TOKEN]");
            bounded = EnvironmentSecrets.Replace(bounded, "$1[REDACTED]");
            bounded = EnvironmentNames.Replace(bounded, "$1[REDACTED]");
            bounded = Bearer.Replace(bounded, "$1[REDACTED]");
            bounded = XmlEnvironmentValues.Replace(bounded, "$1$2[REDACTED]$2");
            bounded = EscapedAssignments.Replace(bounded, "$1[REDACTED]");
            bounded = Assignments.Replace(bounded, "$1[REDACTED]");
            bounded = UrlPassword.Replace(bounded, "$1[REDACTED]$2");
        }
        catch (RegexMatchTimeoutException)
        {
            return "[Content withheld: secret filtering exceeded its time limit]";
        }
        return bounded.Length <= max ? bounded : bounded[..max] + "\n[truncated]";
    }

    public static string Redact(string text) => Clean(text);

    internal static bool IsSensitiveKey(string key) => SensitiveKey.IsMatch(key);
}

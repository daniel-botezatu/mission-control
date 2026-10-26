using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace MissionControl.Providers;

internal sealed record GitHubOptions(string GhExecutable, string ServerExecutable, string ServerSha256)
{
    internal static GitHubOptions Parse(string[] args)
    {
        if (args.Length != 7 || args[0] != "github") throw new ArgumentException();
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 1; index < args.Length; index += 2)
        {
            if (args[index] is not ("--gh-executable" or "--github-server" or "--github-sha256") || !values.TryAdd(args[index], args[index + 1]))
                throw new ArgumentException();
        }
        return new(values["--gh-executable"], values["--github-server"], values["--github-sha256"]);
    }
}

internal interface IGitHubProcesses
{
    Task<string> ReadTokenAsync(ProcessStartInfo command, CancellationToken token);
    Task<int> RelayServerAsync(ProcessStartInfo command, CancellationToken token);
}

/// <summary>Reuses GitHub CLI's existing login without copying credentials into host configuration or persistent files.</summary>
internal static class GitHubLauncher
{
    internal static readonly string[] ReadTools = ["get_me", "search_repositories", "get_file_contents", "list_commits", "get_commit", "list_issues", "search_issues", "issue_read", "list_pull_requests", "search_pull_requests", "pull_request_read"];
    private static readonly Regex TokenPattern = new(@"\A(?:gh[pousr]_[A-Za-z0-9_]{15,500}|github_pat_[A-Za-z0-9_]{15,500})\z", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, TimeSpan.FromMilliseconds(100));
    private static readonly string[] EnvironmentNames = ["SystemRoot", "WINDIR", "SystemDrive", "COMSPEC", "PATHEXT", "TEMP", "TMP", "USERPROFILE", "APPDATA", "LOCALAPPDATA", "ProgramData", "HOME", "PATH"];

    internal static async Task<int> RunAsync(GitHubOptions options, IGitHubProcesses processes, CancellationToken token)
    {
        var extension = OperatingSystem.IsWindows() ? ".exe" : "";
        ValidateExecutable(options.GhExecutable, "gh" + extension);
        ValidateExecutable(options.ServerExecutable, "github-mcp-server" + extension);
        if (!Regex.IsMatch(options.ServerSha256, "\\A[0-9a-fA-F]{64}\\z", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100))) throw new ArgumentException();
        // Windows denies replacement while open; Unix callers must protect the executable directory.
        using var pinnedServer = new FileStream(options.ServerExecutable, FileMode.Open, FileAccess.Read, FileShare.Read);
        var actual = await SHA256.HashDataAsync(pinnedServer, token).ConfigureAwait(false);
        if (!CryptographicOperations.FixedTimeEquals(actual, Convert.FromHexString(options.ServerSha256))) throw new InvalidDataException();

        var credentialCommand = CreateStartInfo(options.GhExecutable);
        foreach (var argument in new[] { "auth", "token", "--hostname", "github.com" }) credentialCommand.ArgumentList.Add(argument);
        credentialCommand.Environment["GH_PROMPT_DISABLED"] = "1";
        // Honor a user's explicit gh config location, but never pass inherited access tokens or alternate hosts.
        if (Environment.GetEnvironmentVariable("GH_CONFIG_DIR") is { Length: > 0 } ghConfig) credentialCommand.Environment["GH_CONFIG_DIR"] = ghConfig;
        string credential;
        using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(token))
        {
            deadline.CancelAfter(TimeSpan.FromSeconds(15));
            credential = (await processes.ReadTokenAsync(credentialCommand, deadline.Token).ConfigureAwait(false)).Trim();
        }
        if (!TokenPattern.IsMatch(credential)) throw new InvalidDataException();

        var serverCommand = CreateStartInfo(options.ServerExecutable);
        serverCommand.ArgumentList.Add("stdio");
        serverCommand.ArgumentList.Add("--read-only");
        serverCommand.ArgumentList.Add("--toolsets=");
        serverCommand.ArgumentList.Add("--tools=" + string.Join(',', ReadTools));
        // Environment settings take precedence in the official server, so construct a minimal environment from scratch.
        serverCommand.Environment["GITHUB_PERSONAL_ACCESS_TOKEN"] = credential;
        serverCommand.Environment["GITHUB_READ_ONLY"] = "true";
        serverCommand.Environment["GITHUB_TOOLS"] = string.Join(',', ReadTools);
        serverCommand.Environment["GITHUB_TOOLSETS"] = "";
        serverCommand.Environment["GITHUB_HOST"] = "https://github.com";
        try { return await processes.RelayServerAsync(serverCommand, token).ConfigureAwait(false); }
        finally { serverCommand.Environment.Remove("GITHUB_PERSONAL_ACCESS_TOKEN"); }
    }

    private static ProcessStartInfo CreateStartInfo(string executable)
    {
        var result = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(executable)!
        };
        result.Environment.Clear();
        foreach (var name in EnvironmentNames)
            if (Environment.GetEnvironmentVariable(name) is { } value) result.Environment[name] = value;
        return result;
    }

    private static void ValidateExecutable(string path, string expectedName)
    {
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal)
            || !Path.GetFileName(path).Equals(expectedName, StringComparison.OrdinalIgnoreCase) || !File.Exists(path)) throw new ArgumentException();
        var current = Path.GetFullPath(path);
        while (current is not null)
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new ArgumentException();
            current = Path.GetDirectoryName(current);
        }
    }
}

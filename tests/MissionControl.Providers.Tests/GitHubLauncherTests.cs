using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using MissionControl.Providers;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace MissionControl.Providers.Tests;

public sealed class GitHubLauncherTests : IDisposable
{
    private const string SyntheticToken = "gho_SYNTHETIC_TOKEN_FOR_TESTS_ONLY_123456";
    private readonly string directory = Path.Combine(Path.GetTempPath(), "mission-control-provider-tests-" + Guid.NewGuid().ToString("N"));
    private GitHubOptions Options
    {
        get
        {
            Directory.CreateDirectory(directory);
            var extension = OperatingSystem.IsWindows() ? ".exe" : "";
            var gh = Path.Combine(directory, "gh" + extension);
            var server = Path.Combine(directory, "github-mcp-server" + extension);
            File.WriteAllText(gh, "synthetic CLI; not executed");
            File.WriteAllText(server, "synthetic MCP server; not executed");
            return new(gh, server, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(server))));
        }
    }

    [Fact]
    public async Task ReusesExistingGhLoginAndLaunchesOnlyExactReadToolsWithoutPersistingCredential()
    {
        var options = Options;
        var fake = new FakeProcesses(SyntheticToken + "\r\n");
        Assert.Equal(0, await GitHubLauncher.RunAsync(options, fake, CancellationToken.None));
        Assert.Equal(["auth", "token", "--hostname", "github.com"], fake.GhArguments!);
        Assert.Equal("1", fake.GhEnvironment!["GH_PROMPT_DISABLED"]);
        Assert.Equal(["stdio", "--read-only", "--toolsets=", "--tools=" + string.Join(',', GitHubLauncher.ReadTools)], fake.ServerArguments!);
        Assert.Equal(SyntheticToken, fake.ServerEnvironment!["GITHUB_PERSONAL_ACCESS_TOKEN"]);
        Assert.Equal("true", fake.ServerEnvironment["GITHUB_READ_ONLY"]);
        Assert.Equal(string.Join(',', GitHubLauncher.ReadTools), fake.ServerEnvironment["GITHUB_TOOLS"]);
        Assert.Equal("", fake.ServerEnvironment["GITHUB_TOOLSETS"]);
        Assert.Equal("https://github.com", fake.ServerEnvironment["GITHUB_HOST"]);
        Assert.Equal("2", fake.ServerEnvironment["GOMAXPROCS"]);
        Assert.Equal("50", fake.ServerEnvironment["GOGC"]);
        Assert.DoesNotContain("GITHUB_PERSONAL_ACCESS_TOKEN", fake.OriginalServerCommand!.Environment.Keys);
        Assert.False(fake.OriginalServerCommand.UseShellExecute);
        Assert.True(fake.OriginalServerCommand.CreateNoWindow);
        Assert.Equal(ProcessWindowStyle.Hidden, fake.OriginalServerCommand.WindowStyle);
        Assert.All(Directory.GetFiles(directory), file => Assert.DoesNotContain(SyntheticToken, File.ReadAllText(file)));
    }

    [Fact]
    public async Task IgnoresInheritedTokensAndWriteToolOverrides()
    {
        var poisoned = new Dictionary<string, string>
        {
            ["GH_TOKEN"] = "synthetic_other_secret", ["GITHUB_TOKEN"] = "synthetic_other_secret",
            ["GITHUB_PERSONAL_ACCESS_TOKEN"] = "synthetic_other_secret", ["GITHUB_TOOLS"] = "create_issue",
            ["GITHUB_TOOLSETS"] = "all", ["GITHUB_READ_ONLY"] = "false", ["GITHUB_HOST"] = "https://example.invalid",
            ["GITHUB_MCP_SERVER_CONFIG"] = "untrusted.json", ["GITHUB_EXCLUDE_TOOLS"] = "get_me"
        };
        var original = poisoned.Keys.ToDictionary(key => key, Environment.GetEnvironmentVariable);
        try
        {
            foreach (var item in poisoned) Environment.SetEnvironmentVariable(item.Key, item.Value);
            var fake = new FakeProcesses(SyntheticToken);
            await GitHubLauncher.RunAsync(Options, fake, CancellationToken.None);
            foreach (var name in poisoned.Keys) Assert.DoesNotContain(name, fake.GhEnvironment!.Keys);
            Assert.DoesNotContain("GH_TOKEN", fake.ServerEnvironment!.Keys);
            Assert.DoesNotContain("GITHUB_TOKEN", fake.ServerEnvironment.Keys);
            Assert.DoesNotContain("GITHUB_EXCLUDE_TOOLS", fake.ServerEnvironment.Keys);
            Assert.DoesNotContain("GITHUB_MCP_SERVER_CONFIG", fake.ServerEnvironment.Keys);
            Assert.Equal("true", fake.ServerEnvironment["GITHUB_READ_ONLY"]);
            Assert.DoesNotContain("create_issue", fake.ServerEnvironment["GITHUB_TOOLS"]);
        }
        finally { foreach (var item in original) Environment.SetEnvironmentVariable(item.Key, item.Value); }
    }

    [Fact]
    public async Task RejectsChangedPinnedServerBeforeReadingCredential()
    {
        var options = Options;
        File.AppendAllText(options.ServerExecutable, "tampered");
        var fake = new FakeProcesses(SyntheticToken);
        await Assert.ThrowsAsync<InvalidDataException>(() => GitHubLauncher.RunAsync(options, fake, CancellationToken.None));
        Assert.Equal(0, fake.TokenReads);
        Assert.Equal(0, fake.ServerStarts);
    }

    [Theory]
    [InlineData("")]
    [InlineData("garbage")]
    [InlineData("gho_SHORT")]
    [InlineData("gho_SYNTHETIC_TOKEN_FOR_TESTS_ONLY_123456\nadditional-output")]
    public async Task InvalidCredentialNeverStartsServer(string token)
    {
        var fake = new FakeProcesses(token);
        await Assert.ThrowsAsync<InvalidDataException>(() => GitHubLauncher.RunAsync(Options, fake, CancellationToken.None));
        Assert.Equal(0, fake.ServerStarts);
    }

    [Fact]
    public async Task MissingLoginNeverOpensBrowserOrFallsBackToOAuth()
    {
        var fake = new FakeProcesses(SyntheticToken) { TokenError = new InvalidOperationException("synthetic auth diagnostic") };
        await Assert.ThrowsAsync<InvalidOperationException>(() => GitHubLauncher.RunAsync(Options, fake, CancellationToken.None));
        Assert.Equal(1, fake.TokenReads);
        Assert.Equal(0, fake.ServerStarts);
        Assert.Equal(["auth", "token", "--hostname", "github.com"], fake.GhArguments!);
    }

    [Fact]
    public async Task RejectsUnexpectedExecutableOrRelativePath()
    {
        var options = Options;
        var other = Path.Combine(directory, "other.exe");
        File.WriteAllText(other, "synthetic");
        var fake = new FakeProcesses(SyntheticToken);
        await Assert.ThrowsAsync<ArgumentException>(() => GitHubLauncher.RunAsync(options with { GhExecutable = other }, fake, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => GitHubLauncher.RunAsync(options with { ServerExecutable = "github-mcp-server.exe" }, fake, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => GitHubLauncher.RunAsync(options with { ServerSha256 = "--bad" }, fake, CancellationToken.None));
        Assert.Equal(0, fake.TokenReads);
    }

    [Fact]
    public void ArgumentsOnlyAllowExplicitGithubModeAndThreeRequiredOptions()
    {
        var valid = new[] { "github", "--gh-executable", "a", "--github-server", "b", "--github-sha256", "c" };
        Assert.Equal(new GitHubOptions("a", "b", "c"), GitHubOptions.Parse(valid));
        Assert.Throws<ArgumentException>(() => GitHubOptions.Parse(["slack", .. valid.Skip(1)]));
        Assert.Throws<ArgumentException>(() => GitHubOptions.Parse([.. valid, "--login"]));
        Assert.Throws<ArgumentException>(() => GitHubOptions.Parse(["github", "--token", "secret", .. valid.Skip(3)]));
        Assert.Throws<ArgumentException>(() => GitHubOptions.Parse(["github", "--gh-executable", "a", "--gh-executable", "b", "--github-sha256", "c"]));
    }

    [WindowsFact]
    public async Task PinnedServerCannotBeReplacedWhileChildIsRunning()
    {
        var options = Options;
        var fake = new FakeProcesses(SyntheticToken)
        {
            OnServerStart = () => Assert.Throws<IOException>(() => File.WriteAllText(options.ServerExecutable, "changed"))
        };
        await GitHubLauncher.RunAsync(options, fake, CancellationToken.None);
    }

    [Fact]
    public async Task ChildFailureAlsoClearsCredentialFromLaunchDescription()
    {
        var fake = new FakeProcesses(SyntheticToken) { OnServerStart = () => throw new InvalidOperationException() };
        await Assert.ThrowsAsync<InvalidOperationException>(() => GitHubLauncher.RunAsync(Options, fake, CancellationToken.None));
        Assert.DoesNotContain("GITHUB_PERSONAL_ACCESS_TOKEN", fake.OriginalServerCommand!.Environment.Keys);
    }

    [Fact]
    public async Task RealChildCredentialCaptureDiscardsStderrAndHasBoundedOutput()
    {
        var runner = new GitHubProcesses(Stream.Null, Stream.Null);
        var result = await runner.ReadTokenAsync(Child("[Console]::Error.WriteLine('synthetic-secret-diagnostic'); [Console]::WriteLine('" + SyntheticToken + "')", "printf '%s\\n' synthetic-secret-diagnostic >&2; printf '%s\\n' " + SyntheticToken), CancellationToken.None);
        Assert.Equal(SyntheticToken, result.Trim());
        await Assert.ThrowsAsync<InvalidDataException>(() => runner.ReadTokenAsync(Child("[Console]::Write('x' * 5000)", "printf '%05000d' 0"), CancellationToken.None));
    }

    [Fact]
    public async Task RealChildRelayForwardsProtocolOnlyAndDiscardsDiagnostics()
    {
        using var output = new MemoryStream();
        using var input = new MemoryStream(Encoding.UTF8.GetBytes("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"tools/call\"}\n"));
        var runner = new GitHubProcesses(input, output);
        Directory.CreateDirectory(directory);
        var cache = new StartupCache(Path.Combine(directory, "startup-cache.json"), "test");
        var status = await runner.RelayServerAsync(_ => Task.FromResult(Child("[Console]::Error.WriteLine('synthetic-secret-diagnostic'); [Console]::WriteLine('{\"jsonrpc\":\"2.0\",\"result\":{}}')", "printf '%s\\n' synthetic-secret-diagnostic >&2; printf '%s\\n' '{\"jsonrpc\":\"2.0\",\"result\":{}}'")), cache, CancellationToken.None);
        Assert.Equal(0, status);
        var text = Encoding.UTF8.GetString(output.ToArray());
        Assert.Contains("jsonrpc", text);
        Assert.DoesNotContain("synthetic-secret", text);
    }

    [Fact]
    public async Task RealLauncherFailurePrintsOnlyFixedDiagnosticAndNeverArguments()
    {
        var processInfo = new ProcessStartInfo("dotnet")
        { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        processInfo.ArgumentList.Add(typeof(GitHubLauncher).Assembly.Location);
        processInfo.ArgumentList.Add("synthetic-invalid-argument-containing-secret");
        using var process = Process.Start(processInfo)!;
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.Equal(2, process.ExitCode);
        Assert.Equal("", await output);
        Assert.Equal(Program.Failure, (await errors).Trim());
        Assert.DoesNotContain("synthetic-invalid", await errors);
    }

    [Fact]
    public async Task CredentialLookupCancellationTerminatesChildPromptly()
    {
        var runner = new GitHubProcesses(Stream.Null, Stream.Null);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        var clock = Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => runner.ReadTokenAsync(Child("Start-Sleep -Seconds 30", "sleep 30"), timeout.Token));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5));
    }

    private static ProcessStartInfo Child(string code, string unixCode)
    {
        var result = new ProcessStartInfo(OperatingSystem.IsWindows() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe") : "/bin/sh")
        { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        foreach (var arg in OperatingSystem.IsWindows() ? new[] { "-NoProfile", "-NonInteractive", "-Command", code } : new[] { "-c", unixCode }) result.ArgumentList.Add(arg);
        return result;
    }

    public void Dispose()
    {
        var resolved = Path.GetFullPath(directory);
        var root = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (resolved.StartsWith(root, StringComparison.OrdinalIgnoreCase) && Path.GetFileName(resolved).StartsWith("mission-control-provider-tests-", StringComparison.Ordinal) && Directory.Exists(resolved))
            Directory.Delete(resolved, true);
    }

    private sealed class FakeProcesses(string token) : IGitHubProcesses
    {
        public int TokenReads { get; private set; }
        public int ServerStarts { get; private set; }
        public string[]? GhArguments { get; private set; }
        public string[]? ServerArguments { get; private set; }
        public Dictionary<string, string?>? GhEnvironment { get; private set; }
        public Dictionary<string, string?>? ServerEnvironment { get; private set; }
        public ProcessStartInfo? OriginalServerCommand { get; private set; }
        public Exception? TokenError { get; init; }
        public Action? OnServerStart { get; init; }
        public Task<string> ReadTokenAsync(ProcessStartInfo command, CancellationToken cancellationToken)
        {
            TokenReads++;
            GhArguments = command.ArgumentList.ToArray();
            GhEnvironment = command.Environment.ToDictionary();
            if (TokenError is not null) throw TokenError;
            return Task.FromResult(token);
        }
        // Behaves like a host whose first request needs the server.
        public async Task<int> RelayServerAsync(Func<CancellationToken, Task<ProcessStartInfo>> start, StartupCache cache, CancellationToken cancellationToken)
        {
            var command = await start(cancellationToken);
            ServerStarts++;
            OriginalServerCommand = command;
            ServerArguments = command.ArgumentList.ToArray();
            ServerEnvironment = command.Environment.ToDictionary();
            OnServerStart?.Invoke();
            return 0;
        }
    }
}

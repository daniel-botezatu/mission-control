using System.Text.Json;
using MissionControl.Core;

namespace MissionControl.Tests;

public sealed class GitWorkspaceDiscoveryTests
{
    [Fact]
    public void Repository_cwd_and_nested_cwd_use_the_nearest_git_checkout()
    {
        using var tree = new Fixture();
        var repo = tree.Repository("billing", "https://github.com/Acme/Billing.git");
        var nested = tree.Folder("billing/src/Http");
        var discovery = new GitWorkspaceDiscovery();
        var result = Assert.IsType<GitWorkspaceDiscoveryResult>(discovery.Discover(nested));
        Assert.Equal(repo, result.RootPath);
        Assert.Equal("billing", result.Name);
        Assert.Equal("acme/billing", Assert.Single(Assert.Single(result.Repositories).GitHubRepositories));
        Assert.Equal(repo, Assert.Single(result.Repositories).CheckoutPath);
    }

    [Fact]
    public void Umbrella_with_its_own_git_repository_groups_all_child_repositories()
    {
        using var tree = new Fixture();
        var umbrella = tree.Repository("SP", "git@github.com:Acme/workspace.git");
        var api = tree.Repository("SP/Billing", "git@github.com:Acme/Billing.git");
        var processor = tree.Repository("SP/Report-Processor", "ssh://git@github.com/Acme/Report-Processor.git");
        var discovery = new GitWorkspaceDiscovery();
        var result = Assert.IsType<GitWorkspaceDiscoveryResult>(discovery.Discover(umbrella));
        Assert.Equal(umbrella, result.RootPath);
        Assert.Equal(3, result.Repositories.Count);
        Assert.Contains(result.Repositories, x => x.CheckoutPath == api && x.GitHubRepositories.Contains("acme/billing"));
        Assert.Contains(result.Repositories, x => x.CheckoutPath == processor && x.GitHubRepositories.Contains("acme/report-processor"));
        Assert.Contains(result.Repositories, x => x.CheckoutPath == umbrella && x.GitHubRepositories.Contains("acme/workspace"));
        // A child session still identifies its own checkout until the grouping service observes the umbrella.
        Assert.Equal(api, discovery.Discover(api)?.RootPath);
    }

    [Fact]
    public void Umbrella_without_git_groups_nested_repositories_without_scanning_siblings()
    {
        using var tree = new Fixture();
        var umbrella = tree.Folder("SP");
        tree.Repository("SP/backend/Billing", "https://github.com/org/api.git");
        tree.Repository("SP/frontend/UI", "https://github.com/org/ui.git");
        tree.Repository("unrelated", "https://github.com/other/unrelated.git");
        var result = Assert.IsType<GitWorkspaceDiscoveryResult>(new GitWorkspaceDiscovery().Discover(umbrella));
        Assert.Equal(umbrella, result.RootPath);
        Assert.Equal(2, result.Repositories.Count);
        Assert.DoesNotContain(result.Repositories, x => x.GitHubRepositories.Contains("other/unrelated"));
    }

    [Fact]
    public void Linked_worktree_resolves_to_its_main_checkout_without_git_execution()
    {
        using var tree = new Fixture();
        var main = tree.Repository("main", "https://github.com/org/api.git");
        var gitdir = tree.Folder("main/.git/worktrees/feature");
        File.WriteAllText(Path.Combine(gitdir, "commondir"), "../..\n");
        var worktree = tree.Folder("feature");
        File.WriteAllText(Path.Combine(worktree, ".git"), "gitdir: ../main/.git/worktrees/feature\n");
        var discovery = new GitWorkspaceDiscovery();
        var result = Assert.IsType<GitWorkspaceDiscoveryResult>(discovery.Discover(worktree));
        var repo = Assert.Single(result.Repositories);
        Assert.Equal(main, result.RootPath);
        Assert.Equal(main, repo.CheckoutPath);
        Assert.Equal("org/api", Assert.Single(repo.GitHubRepositories));
        Assert.Equal(worktree, Assert.Single(result.LinkedWorktrees!));
        Assert.True(discovery.IsLinkedWorktree(worktree));
        Assert.False(discovery.IsLinkedWorktree(main));
    }

    [Fact]
    public void Agent_worktrees_inside_a_repository_are_not_listed_as_repositories()
    {
        using var tree = new Fixture();
        var main = tree.Repository("Acme", "https://github.com/acme/chatbot_ai.git");
        string Worktree(string relative, string name)
        {
            var metadata = tree.Folder($"Acme/.git/worktrees/{name}");
            File.WriteAllText(Path.Combine(metadata, "commondir"), "../..\n");
            var path = tree.Folder(relative);
            File.WriteAllText(Path.Combine(path, ".git"), $"gitdir: {metadata}\n");
            return path;
        }
        var agent = Worktree("Acme/.claude/worktrees/wf_12", "wf_12");
        // A worktree in an ordinary folder is skipped by the scan instead of becoming a second repository.
        var visible = Worktree("Acme/review-copy", "review-copy");
        var child = tree.Repository("Acme/my-acme", "https://github.com/acme/my-acme.git");
        var result = Assert.IsType<GitWorkspaceDiscoveryResult>(new GitWorkspaceDiscovery().Discover(tree.Folder("Acme/.claude/worktrees/wf_12/src")));
        Assert.Equal(main, result.RootPath);
        Assert.Equal([main, child], result.Repositories.Select(x => x.CheckoutPath).Order(StringComparer.OrdinalIgnoreCase));
        Assert.Contains(agent, result.LinkedWorktrees!);
        Assert.Contains(visible, result.LinkedWorktrees!);
    }

    [Fact]
    public void Submodule_and_bare_repository_worktrees_are_not_redirected()
    {
        using var tree = new Fixture();
        tree.Repository("app", "https://github.com/org/app.git");
        tree.Folder("app/.git/modules/lib");
        var submodule = tree.Folder("app/lib");
        File.WriteAllText(Path.Combine(submodule, ".git"), "gitdir: ../.git/modules/lib\n");
        var bare = tree.Folder("bare.git");
        File.WriteAllText(Path.Combine(bare, "config"), "[remote \"origin\"]\nurl = https://github.com/org/bare.git\n");
        var bareMetadata = tree.Folder("bare.git/worktrees/main");
        File.WriteAllText(Path.Combine(bareMetadata, "commondir"), "../..\n");
        var bareWorktree = tree.Folder("bare-main");
        File.WriteAllText(Path.Combine(bareWorktree, ".git"), $"gitdir: {bareMetadata}\n");
        var discovery = new GitWorkspaceDiscovery();
        Assert.False(discovery.IsLinkedWorktree(submodule));
        Assert.False(discovery.IsLinkedWorktree(bareWorktree));
        Assert.Equal(bareWorktree, discovery.Discover(bareWorktree)?.RootPath);
    }

    [Fact]
    public void Umbrella_scan_stops_at_repository_boundaries_and_stays_under_its_limit()
    {
        using var tree = new Fixture();
        var umbrella = tree.Repository("SP", "https://github.com/org/workspace.git");
        for (var repo = 0; repo < 14; repo++)
        {
            tree.Repository($"SP/service-{repo}", $"https://github.com/org/service-{repo}.git");
            // Large source trees inside each checkout previously consumed the 512-directory budget.
            for (var folder = 0; folder < 40; folder++) tree.Folder($"SP/service-{repo}/app/area-{folder}/nested");
        }
        var result = Assert.IsType<GitWorkspaceDiscoveryResult>(new GitWorkspaceDiscovery().Discover(umbrella));
        Assert.Equal(15, result.Repositories.Count);
        Assert.Empty(result.Gaps);
    }

    [Fact]
    public void Excluded_scratch_folders_never_form_projects()
    {
        using var tree = new Fixture();
        var scratch = tree.Folder("tmp-agent");
        var repo = tree.Repository("tmp-agent/e2e", "https://github.com/org/e2e.git");
        var policy = new GitWorkspaceDiscovery([scratch]);
        Assert.Null(policy.Discover(repo));
        Assert.True(policy.IsExcludedWorkspace(Path.Combine(repo, "src")));
        Assert.False(policy.IsExcludedWorkspace(tree.Root));
        // The default constructor excludes nothing, so explicit fixtures and callers keep full control.
        Assert.Equal(repo, new GitWorkspaceDiscovery().Discover(repo)?.RootPath);
    }

    [Fact]
    public void Remote_urls_are_normalized_and_credentials_are_never_returned()
    {
        using var tree = new Fixture();
        var root = tree.Repository("api", "https://username:super-secret-token@github.com/Org/API.git");
        File.AppendAllText(Path.Combine(root, ".git", "config"), "[remote \"upstream\"]\n url = git@github.com:ORG/API.git\n[remote \"origin-push\"]\n pushurl = https://github.com/wrong/push-only.git\n");
        var result = Assert.IsType<GitWorkspaceDiscoveryResult>(new GitWorkspaceDiscovery().Discover(root));
        Assert.Equal("org/api", Assert.Single(Assert.Single(result.Repositories).GitHubRepositories));
        var serialized = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("super-secret-token", serialized);
        Assert.DoesNotContain("username", serialized);
        Assert.DoesNotContain("https://", serialized);
    }

    [Theory]
    [InlineData("https://github.com/Org/Repo.git", "org/repo")]
    [InlineData("https://token@github.com/Org/Repo.git/", "org/repo")]
    [InlineData("git@github.com:Org/Repo.git", "org/repo")]
    [InlineData("ssh://git@github.com/Org/Repo.git", "org/repo")]
    [InlineData("ssh://git@github.com:22/Org/Repo.git", "org/repo")]
    [InlineData("https://github.com/org/repo", "org/repo")]
    [InlineData("https://github.example.com/org/repo", null)]
    [InlineData("https://github.com.evil.example/org/repo", null)]
    [InlineData("https://github.com/org/repo/pulls", null)]
    [InlineData("https://github.com/org/repo?token=secret", null)]
    [InlineData("https://github.com/org/repo#fragment", null)]
    [InlineData("https://github.com:444/org/repo", null)]
    [InlineData("ssh://git@github.com:444/org/repo", null)]
    [InlineData("git://github.com/org/repo", null)]
    [InlineData("git@other.example:org/repo.git", null)]
    [InlineData("https://github.com/org/repo%2Fextra", null)]
    [InlineData("file:///tmp/repo", null)]
    [InlineData("org/repo", null)]
    [InlineData("https://github.com/org-/repo", null)]
    [InlineData("https://github.com/org/..", null)]
    public void Only_canonical_github_ssh_and_https_aliases_are_accepted(string input, string? expected) =>
        Assert.Equal(expected, GitWorkspaceDiscovery.NormalizeGitHubRepository(input));

    [Fact]
    public void Includes_global_settings_hooks_and_non_remote_urls_are_not_interpreted()
    {
        using var tree = new Fixture();
        var root = tree.Repository("api", "https://github.com/org/local.git");
        var included = Path.Combine(tree.Root, "included-config");
        File.WriteAllText(included, "[remote \"hidden\"]\n url = https://github.com/org/hidden.git\n");
        File.AppendAllText(Path.Combine(root, ".git", "config"), $"[include]\n path = {included.Replace('\\', '/')}\n[core]\n hooksPath = malicious-hook\n[credential]\n helper = !touch executed\n[url \"https://github.com/org/rewrite\"]\n insteadOf = https://somewhere.example/\n");
        var result = Assert.IsType<GitWorkspaceDiscoveryResult>(new GitWorkspaceDiscovery().Discover(root));
        Assert.Equal("org/local", Assert.Single(Assert.Single(result.Repositories).GitHubRepositories));
        Assert.Contains(result.Gaps, x => x.Contains("include", StringComparison.Ordinal));
        Assert.False(File.Exists(Path.Combine(root, "executed")));
    }

    [Fact]
    public void Config_quoting_comments_and_line_continuations_are_supported()
    {
        using var tree = new Fixture();
        var root = tree.Repository("api", "https://github.com/org/initial.git");
        File.WriteAllText(Path.Combine(root, ".git", "config"), "[remote \"origin\"] # comment\n URL = \"https://github.com/Org/\"\\\n\"API.git\" ; comment\n[remote \"upstream\"]\n url = https://github.com/Org/Upstream.git # trailing\n[core]\n url = https://github.com/ignored/irrelevant.git\n");
        var result = Assert.IsType<GitWorkspaceDiscoveryResult>(new GitWorkspaceDiscovery().Discover(root));
        Assert.Equal(new[] { "org/api", "org/upstream" }, Assert.Single(result.Repositories).GitHubRepositories);
    }

    [Fact]
    public void Oversized_config_is_not_partially_parsed_and_is_reported()
    {
        using var tree = new Fixture();
        var root = tree.Repository("api", "https://github.com/org/api.git");
        File.AppendAllText(Path.Combine(root, ".git", "config"), new string('x', 65537));
        var result = Assert.IsType<GitWorkspaceDiscoveryResult>(new GitWorkspaceDiscovery().Discover(root));
        Assert.Empty(Assert.Single(result.Repositories).GitHubRepositories);
        Assert.Contains(result.Gaps, x => x.Contains("64 KiB", StringComparison.Ordinal));
    }

    [Fact]
    public void Invalid_or_oversized_worktree_pointers_are_reported_without_following_them()
    {
        using var tree = new Fixture();
        var root = tree.Folder("worktree");
        File.WriteAllText(Path.Combine(root, ".git"), "gitdir: " + new string('x', 4097));
        var result = Assert.IsType<GitWorkspaceDiscoveryResult>(new GitWorkspaceDiscovery().Discover(root));
        Assert.Empty(Assert.Single(result.Repositories).GitHubRepositories);
        Assert.Contains(result.Gaps, x => x.Contains("pointer", StringComparison.Ordinal));
        File.WriteAllText(Path.Combine(root, ".git"), "gitdir: \\\\server\\share\\repository");
        result = Assert.IsType<GitWorkspaceDiscoveryResult>(new GitWorkspaceDiscovery().Discover(root));
        Assert.Empty(Assert.Single(result.Repositories).GitHubRepositories);
        Assert.Contains(result.Gaps, x => x.Contains("unsafe", StringComparison.Ordinal));
    }

    [Fact]
    public void Dependency_build_and_hidden_folders_are_skipped()
    {
        using var tree = new Fixture();
        var root = tree.Folder("workspace");
        tree.Repository("workspace/real", "https://github.com/org/real.git");
        tree.Repository("workspace/node_modules/dependency", "https://github.com/org/ignored.git");
        tree.Repository("workspace/vendor/dependency", "https://github.com/org/ignored.git");
        tree.Repository("workspace/.cache/dependency", "https://github.com/org/ignored.git");
        tree.Repository("workspace/build/generated", "https://github.com/org/ignored.git");
        var result = Assert.IsType<GitWorkspaceDiscoveryResult>(new GitWorkspaceDiscovery().Discover(root));
        Assert.Equal("org/real", Assert.Single(Assert.Single(result.Repositories).GitHubRepositories));
    }

    [Fact]
    public void Depth_and_repository_count_are_bounded()
    {
        using var tree = new Fixture();
        var root = tree.Folder("workspace");
        tree.Repository("workspace/a/b/c/depth4", "https://github.com/org/depth4.git");
        tree.Repository("workspace/a/b/c/d/depth5", "https://github.com/org/depth5.git");
        var discovery = new GitWorkspaceDiscovery();
        var result = Assert.IsType<GitWorkspaceDiscoveryResult>(discovery.Discover(root));
        Assert.Equal("org/depth4", Assert.Single(Assert.Single(result.Repositories).GitHubRepositories));
        for (var index = 0; index < 66; index++) tree.Repository($"workspace/repo-{index:D2}", $"https://github.com/org/repo-{index}.git");
        result = Assert.IsType<GitWorkspaceDiscoveryResult>(discovery.Discover(root));
        Assert.Equal(GitWorkspaceDiscovery.MaximumRepositories, result.Repositories.Count);
        Assert.Contains(result.Gaps, x => x.Contains("64-repository", StringComparison.Ordinal));
    }

    [Fact]
    public void Directory_budget_prevents_unbounded_umbrella_scanning()
    {
        using var tree = new Fixture();
        var root = tree.Folder("workspace");
        for (var index = 0; index < 520; index++) tree.Folder($"workspace/folder-{index:D3}");
        var result = Assert.IsType<GitWorkspaceDiscoveryResult>(new GitWorkspaceDiscovery().Discover(root));
        Assert.Empty(result.Repositories);
        Assert.Contains(result.Gaps, x => x.Contains("512-directory", StringComparison.Ordinal));
    }

    [Fact]
    public void Missing_relative_unc_and_device_roots_are_refused()
    {
        using var tree = new Fixture();
        var discovery = new GitWorkspaceDiscovery();
        Assert.Null(discovery.Discover(Path.Combine(tree.Root, "does-not-exist")));
        Assert.Null(discovery.Discover("relative/path"));
        Assert.Null(discovery.Discover("\\\\server\\share"));
        Assert.Null(discovery.Discover("\\\\?\\C:\\workspace"));
    }

    [Fact]
    public void Drive_root_is_not_scanned_downward()
    {
        using var tree = new Fixture();
        var drive = Path.GetPathRoot(tree.Root)!;
        var result = Assert.IsType<GitWorkspaceDiscoveryResult>(new GitWorkspaceDiscovery().Discover(drive));
        Assert.Empty(result.Repositories);
        Assert.Contains(result.Gaps, x => x.Contains("drive roots", StringComparison.Ordinal));
    }

    [Fact]
    public void Cancellation_is_honored_before_filesystem_work()
    {
        using var token = new CancellationTokenSource();
        token.Cancel();
        Assert.Throws<OperationCanceledException>(() => new GitWorkspaceDiscovery().Discover("not-a-path", token.Token));
    }

    private sealed class Fixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "MissionControl-git-discovery-" + Guid.NewGuid().ToString("N"));
        public Fixture() => Directory.CreateDirectory(Root);
        public string Folder(string relative)
        {
            var path = Path.GetFullPath(Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar)));
            Assert.StartsWith(Root + Path.DirectorySeparatorChar, path, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
            Directory.CreateDirectory(path);
            return path;
        }
        public string Repository(string relative, string remote)
        {
            var directory = Folder(relative);
            var metadata = Path.Combine(directory, ".git");
            Directory.CreateDirectory(metadata);
            File.WriteAllText(Path.Combine(metadata, "config"), $"[core]\n repositoryformatversion = 0\n[remote \"origin\"]\n url = {remote}\n");
            return directory;
        }
        public void Dispose()
        {
            var resolved = Path.GetFullPath(Root);
            if (Path.GetDirectoryName(resolved) != Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) || !Path.GetFileName(resolved).StartsWith("MissionControl-git-discovery-", StringComparison.Ordinal)) throw new InvalidOperationException("Unsafe test cleanup path.");
            Directory.Delete(resolved, true);
        }
    }
}

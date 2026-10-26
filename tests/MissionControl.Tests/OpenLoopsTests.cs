using System.Text.Json;
using MissionControl.Core;

namespace MissionControl.Tests;

/// <summary>Open loops are read from Git metadata files and approved history only, on request.</summary>
public sealed class OpenLoopsTests : IDisposable
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-26T12:00:00Z");
    private readonly string root = Path.Combine(Path.GetTempPath(), "MissionControl-open-loops-" + Guid.NewGuid().ToString("N"));

    // Refs, config, reflogs and worktrees only: these hashes have no objects, so whether a listed branch's work was
    // pushed cannot be verified and is reported as such rather than guessed.
    [Fact]
    public void BranchMetadataIsReadWithoutStartingGit()
    {
        var checkout = Path.Combine(root, "repo");
        var git = Path.Combine(checkout, ".git");
        string Hash(char c) => new(c, 40);
        void Ref(string name, string hash, DateTimeOffset? movedAt, string entry = "commit: change")
        {
            Write(Path.Combine(git, "refs", name), hash + "\n");
            if (movedAt is { } at) Write(Path.Combine(git, "logs", "refs", name), $"{Hash('0')} {hash} Dev <dev@example.com> {at.ToUnixTimeSeconds()} +0100\t{entry}\n");
        }
        Ref("remotes/origin/main", Hash('a'), Now.AddDays(-1));
        Ref("heads/main", Hash('a'), Now.AddDays(-1));
        Ref("heads/never-pushed", Hash('b'), Now.AddHours(-2));
        Ref("heads/differs", Hash('c'), Now.AddHours(-1));
        Ref("remotes/origin/differs", Hash('d'), Now.AddHours(-3));
        Ref("heads/merged", Hash('1'), Now.AddHours(-6));
        Ref("heads/fresh", Hash('a'), Now.AddHours(-1));
        Ref("heads/old", Hash('2'), Now.AddDays(-45));
        Ref("heads/pr-9", Hash('5'), Now.AddHours(-1), "fetch origin pull/9/head:pr-9: storing head");
        Ref("heads/backup", Hash('6'), Now.AddHours(-1), "branch: Created from main");
        Write(Path.Combine(git, "packed-refs"), $"# pack-refs with: peeled fully-peeled sorted\n{Hash('3')} refs/heads/team/packed\n^{Hash('4')}\n");
        Write(Path.Combine(git, "logs", "refs", "heads", "team", "packed"), $"{Hash('0')} {Hash('3')} Dev <dev@example.com> {Now.AddHours(-4).ToUnixTimeSeconds()} +0000\tcommit (initial): start\n");
        Write(Path.Combine(git, "refs", "remotes", "origin", "HEAD"), "ref: refs/remotes/origin/main\n");
        Write(Path.Combine(git, "config"), """
            [core]
                bare = false
            [branch "main"]
                remote = origin
                merge = refs/heads/main
            [branch "differs"]
                remote = origin
                merge = refs/heads/differs
            [branch "merged"]
                remote = origin
                merge = refs/heads/merged
            """);
        // A linked worktree of the same repository must not list its branches twice.
        var worktree = Path.Combine(root, "worktree");
        Write(Path.Combine(git, "worktrees", "wt", "commondir"), "../..\n");
        Write(Path.Combine(worktree, ".git"), "gitdir: " + Path.Combine(git, "worktrees", "wt") + "\n");

        var found = GitBranchLoops.Read([new(checkout, ["example/repo"]), new(worktree, [])], Now, TimeSpan.FromDays(30));

        Assert.Equal([("differs", "unverified", (int?)null), ("never-pushed", "unverified", null), ("team/packed", "unverified", null)],
            found.Branches.Select(b => (b.Branch, b.State, b.Commits)).ToArray());
        Assert.All(found.Branches, b => Assert.Equal("example/repo", b.Repository));
        Assert.Equal(Now.AddHours(-1), DateTimeOffset.Parse(found.Branches[0].LastCommitAt));
        Assert.Equal(0, found.Omitted);
    }

    [Fact]
    public void MissingRepositoriesAndRepositoriesWithoutARemoteAreSkipped()
    {
        var local = Path.Combine(root, "local-only");
        Write(Path.Combine(local, ".git", "refs", "heads", "master"), new string('a', 40) + "\n");
        Write(Path.Combine(local, ".git", "logs", "refs", "heads", "master"), $"{new string('0', 40)} {new string('a', 40)} Dev <dev@example.com> {Now.AddHours(-1).ToUnixTimeSeconds()} +0000\tcommit: work\n");
        Assert.Empty(GitBranchLoops.Read([new(Path.Combine(root, "missing"), []), new(root, []), new(local, [])], Now, TimeSpan.FromDays(30)).Branches);
    }

    [GitFact]
    public void CommitHistoryDecidesWhatIsUnpushed()
    {
        var now = DateTimeOffset.UtcNow;
        UnpushedBranch[] Found(string repository) => GitBranchLoops.Read([new(repository, ["synthetic/repository"])], now, TimeSpan.FromDays(30)).Branches.ToArray();

        // Behind only: the local branch was reset below a commit that is already on the remote.
        var behind = Repository("behind", now);
        Git(behind, now.AddHours(-3), "commit", "--allow-empty", "-m", "already pushed");
        Git(behind, now.AddHours(-2), "update-ref", "refs/remotes/origin/main", "HEAD");
        Git(behind, now.AddHours(-1), "reset", "--hard", "HEAD~1");
        Assert.Equal("0\t1", Git(behind, now, "rev-list", "--left-right", "--count", "HEAD...@{u}"));
        Assert.Empty(Found(behind));

        // Diverged: one local commit, and a teammate's commit fetched later.
        var diverged = Repository("diverged", now);
        var start = Git(diverged, now, "rev-parse", "HEAD");
        Git(diverged, now.AddHours(-3), "commit", "--allow-empty", "-m", "local unpushed work");
        var teammate = Git(diverged, now.AddHours(-2), "commit-tree", Git(diverged, now, "rev-parse", "HEAD^{tree}"), "-p", start, "-m", "teammate work");
        Git(diverged, now.AddHours(-1), "update-ref", "-m", "fetch origin: fast-forward", "refs/remotes/origin/main", teammate);
        Assert.Equal("1\t1", Git(diverged, now, "rev-list", "--left-right", "--count", "HEAD...@{u}"));
        Assert.Equal([("main", "ahead", (int?)1)], Found(diverged).Select(b => (b.Branch, b.State, b.Commits)));

        // Review finding: a remote descendant dated before its parents (clock skew or imported history) must still
        // exclude the ancestors walked before it.
        var skewed = Repository("skewed", now);
        Git(skewed, now.AddHours(-3), "commit", "--allow-empty", "-m", "also already pushed");
        var skewedRemote = Git(skewed, now.AddHours(-5), "commit-tree", Git(skewed, now, "rev-parse", "HEAD^{tree}"), "-p", Git(skewed, now, "rev-parse", "HEAD"), "-m", "remote descendant with a backdated clock");
        Git(skewed, now.AddHours(-1), "update-ref", "refs/remotes/origin/main", skewedRemote);
        Assert.Equal("0", Git(skewed, now, "rev-list", "--count", "HEAD", "--not", "--remotes"));
        Assert.Empty(Found(skewed));

        // Review finding: a branch without an upstream whose history cannot be read is unverified, not "never pushed".
        var unreadable = Repository("unreadable", now);
        Git(unreadable, now.AddHours(-2), "switch", "-c", "feature/unreadable");
        Git(unreadable, now.AddHours(-1), "commit", "--allow-empty", "-m", "commit whose object goes missing");
        var missing = Git(unreadable, now, "rev-parse", "HEAD");
        var objectFile = Path.Combine(unreadable, ".git", "objects", missing[..2], missing[2..]);
        File.SetAttributes(objectFile, FileAttributes.Normal);
        File.Move(objectFile, objectFile + ".hidden");
        Assert.Equal([("feature/unreadable", "unverified", (int?)null)], Found(unreadable).Select(b => (b.Branch, b.State, b.Commits)));

        // Tracking main but pushed to its own pull request branch without -u: nothing is unpushed.
        var pullRequest = Repository("pull-request", now);
        Git(pullRequest, now.AddHours(-2), "switch", "-c", "feature/tracks-main", "--track", "origin/main");
        Git(pullRequest, now.AddHours(-2), "commit", "--allow-empty", "-m", "pull request work");
        Git(pullRequest, now.AddHours(-1), "update-ref", "refs/remotes/origin/feature/tracks-main", "HEAD");
        Assert.Equal("1\t0", Git(pullRequest, now, "rev-list", "--left-right", "--count", "HEAD...@{u}"));
        Assert.Empty(Found(pullRequest));

        // Never pushed: a branch whose commit exists on the remote under another name is pushed; one with its own commit is not.
        var named = Repository("named", now);
        Git(named, now.AddHours(-2), "switch", "-c", "feature/pushed-elsewhere");
        Git(named, now.AddHours(-2), "commit", "--allow-empty", "-m", "pushed as another branch");
        Git(named, now.AddHours(-2), "update-ref", "refs/remotes/origin/other-name", "HEAD");
        Git(named, now.AddHours(-1), "switch", "-c", "feature/local-only");
        Git(named, now.AddHours(-1), "commit", "--allow-empty", "-m", "only here");
        Assert.Equal([("feature/local-only", "never_pushed", (int?)1)], Found(named).Select(b => (b.Branch, b.State, b.Commits)));
    }

    [GitFact]
    public void PackedHistoryStoredAsDeltasIsWalkedLikeGit()
    {
        var now = DateTimeOffset.UtcNow;
        var repository = Repository("packed", now);
        // Similar commit messages make Git store most commits as deltas of one another.
        var description = string.Concat(Enumerable.Repeat("Shared description that makes these commits similar. ", 40));
        for (var i = 1; i <= 30; i++) Git(repository, now.AddMinutes(-60 + i), "commit", "--allow-empty", "-m", $"change {i}", "-m", description);
        Git(repository, now, "repack", "-adfq", "--window=250", "--depth=50");
        var deltas = Git(repository, now, "verify-pack", "-v", Directory.GetFiles(Path.Combine(repository, ".git", "objects", "pack"), "*.idx").Single())
            .Split('\n').Count(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries) is [_, "commit", _, _, _, _, _, ..]);
        Assert.True(deltas >= 10, $"expected commits stored as deltas, found {deltas}");
        Assert.Equal("30\t0", Git(repository, now, "rev-list", "--left-right", "--count", "HEAD...@{u}"));
        var found = Assert.Single(GitBranchLoops.Read([new(repository, [])], now, TimeSpan.FromDays(30)).Branches);
        Assert.Equal(("ahead", (int?)30), (found.State, found.Commits));
    }

    // A repository whose main branch tracks origin/main at its first commit.
    private string Repository(string name, DateTimeOffset now)
    {
        var path = Path.Combine(root, name);
        Directory.CreateDirectory(path);
        Git(path, now.AddHours(-4), "init", "--initial-branch=main");
        Git(path, now.AddHours(-4), "config", "core.logAllRefUpdates", "true");
        Git(path, now.AddHours(-4), "commit", "--allow-empty", "-m", "base");
        Git(path, now.AddHours(-4), "remote", "add", "origin", "https://example.invalid/synthetic/repository.git");
        Git(path, now.AddHours(-4), "config", "branch.main.remote", "origin");
        Git(path, now.AddHours(-4), "config", "branch.main.merge", "refs/heads/main");
        Git(path, now.AddHours(-4), "update-ref", "--create-reflog", "refs/remotes/origin/main", "HEAD");
        return path;
    }

    private string Git(string repository, DateTimeOffset at, params string[] arguments)
    {
        var info = new System.Diagnostics.ProcessStartInfo("git") { WorkingDirectory = repository, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        var config = Path.Combine(root, "empty-gitconfig");
        if (!File.Exists(config)) File.WriteAllText(config, "");
        info.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        info.Environment["GIT_CONFIG_GLOBAL"] = config;
        info.Environment["GIT_AUTHOR_NAME"] = info.Environment["GIT_COMMITTER_NAME"] = "Synthetic";
        info.Environment["GIT_AUTHOR_EMAIL"] = info.Environment["GIT_COMMITTER_EMAIL"] = "synthetic@example.invalid";
        info.Environment["GIT_AUTHOR_DATE"] = info.Environment["GIT_COMMITTER_DATE"] = at.ToString("O");
        using var process = System.Diagnostics.Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, "git " + string.Join(' ', arguments) + ": " + error);
        return output.Result.Trim();
    }

    [Fact]
    public void DraftsLeftAtTheEndOfAConversationAreListedAndAnsweredOnesAreNot()
    {
        Directory.CreateDirectory(root);
        var approved = Path.Combine(root, "approved");
        var other = Path.Combine(root, "other");
        Directory.CreateDirectory(approved); Directory.CreateDirectory(other);
        using var store = new WorkspaceStore(Path.Combine(root, "workspace.db"));
        store.SaveProject(new("approved", "Approved", "blue", "Test-only approval", approved));
        store.SetProjectApproval("approved", true);
        store.SaveProject(new("other", "Other", "blue", "Not approved", other));
        store.SaveSource(new("source", "claude", "Claude", root, true, OperatingSystem.IsWindows() ? "windows" : "linux"));
        EvidenceRecord Message(string id, string session, string author, string body, DateTimeOffset at, string? folder = null) => new(id, "source", session + ".jsonl", session,
            folder == other ? "other" : "approved", "claude", "message", body.Split('\n')[0], body, author, at.ToString("O"), at.ToString("O"), WorkingDirectory: folder ?? approved,
            DataJson: author == "assistant" ? JsonSerializer.Serialize(new { schema = "memory-v1", phase = "final", final = true }) : null);
        store.CommitBatch([
            Message("u1", "left", "user", "Write Maria a reply", Now.AddHours(-3)),
            Message("left-draft", "left", "assistant", "Here it is.\n\n**Draft reply to Maria:**\nThanks, merging today.", Now.AddHours(-2)),
            Message("u2", "answered", "user", "Draft the announcement", Now.AddHours(-3)),
            Message("answered-draft", "answered", "assistant", "## Slack draft\nRelease 2.1 is out.", Now.AddHours(-2)),
            Message("u3", "answered", "user", "Posted, thanks", Now.AddHours(-1)),
            Message("draft-pr", "pr", "assistant", "Draft PR #12 opened with the fix.", Now.AddHours(-2)),
            Message("old-draft", "old", "assistant", "Draft:\nSomething from last month.", Now.AddDays(-20)),
            Message("dismissed-draft", "dismissed", "assistant", "Draft:\nNo longer needed.", Now.AddHours(-2)),
            Message("hidden-draft", "hidden", "assistant", "Draft:\nFrom a project that is not approved.", Now.AddHours(-2), other),
            Message("quoted-draft", "quoted", "assistant", "Send this to Maria:\n\n> @Maria could you add the deploy key?", Now.AddHours(-1.5)),
            Message("summary", "summary", "assistant", "All tests pass and the branch is pushed.", Now.AddHours(-1))]);
        store.SaveCorrection(new("dismissed-draft", Dismissed: true));

        var drafts = store.UnsentDrafts(Now.AddDays(-7), 5);

        Assert.Equal(["quoted-draft", "left-draft"], drafts.Select(record => record.Id).ToArray());
    }

    private static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (!Directory.Exists(root)) return;
        // Git writes its objects read-only, which Windows refuses to delete.
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(root, true);
    }
}

/// <summary>Runs only where Git is installed; the tests use it to build real repositories, never the code under test.</summary>
public sealed class GitFactAttribute : FactAttribute
{
    public GitFactAttribute()
    {
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("git", "--version") { UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true });
            process?.WaitForExit(10_000);
            if (process is not { ExitCode: 0 }) Skip = "Git is not installed.";
        }
        catch (System.ComponentModel.Win32Exception) { Skip = "Git is not installed."; }
    }
}

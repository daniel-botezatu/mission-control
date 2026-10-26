using System.Text.Json;
using MissionControl.Core;

namespace MissionControl.Tests;

/// <summary>Open loops are read from Git metadata files and approved history only, on request.</summary>
public sealed class OpenLoopsTests : IDisposable
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-26T12:00:00Z");
    private readonly string root = Path.Combine(Path.GetTempPath(), "MissionControl-open-loops-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void UnpushedAndNeverPushedBranchesAreFoundWithoutStartingGit()
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
        Ref("heads/ahead", Hash('c'), Now.AddHours(-1));
        Ref("remotes/origin/ahead", Hash('d'), Now.AddHours(-3));
        Ref("heads/behind", Hash('e'), Now.AddHours(-5));
        Ref("remotes/origin/behind", Hash('f'), Now.AddHours(-4));
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
            [branch "ahead"]
                remote = origin
                merge = refs/heads/ahead
            [branch "merged"]
                remote = origin
                merge = refs/heads/merged
            """);
        // A linked worktree of the same repository must not list its branches twice.
        var worktree = Path.Combine(root, "worktree");
        Write(Path.Combine(git, "worktrees", "wt", "commondir"), "../..\n");
        Write(Path.Combine(worktree, ".git"), "gitdir: " + Path.Combine(git, "worktrees", "wt") + "\n");

        var branches = GitBranchLoops.Read([new(checkout, ["example/repo"]), new(worktree, [])], Now, TimeSpan.FromDays(30));

        Assert.Equal([("ahead", "ahead"), ("never-pushed", "never_pushed"), ("team/packed", "never_pushed")], branches.Select(b => (b.Branch, b.State)).ToArray());
        Assert.All(branches, b => Assert.Equal("example/repo", b.Repository));
        Assert.Equal(Now.AddHours(-1), DateTimeOffset.Parse(branches[0].LastCommitAt));
    }

    [Fact]
    public void MissingRepositoriesAndRepositoriesWithoutARemoteAreSkipped()
    {
        var local = Path.Combine(root, "local-only");
        Write(Path.Combine(local, ".git", "refs", "heads", "master"), new string('a', 40) + "\n");
        Write(Path.Combine(local, ".git", "logs", "refs", "heads", "master"), $"{new string('0', 40)} {new string('a', 40)} Dev <dev@example.com> {Now.AddHours(-1).ToUnixTimeSeconds()} +0000\tcommit: work\n");
        Assert.Empty(GitBranchLoops.Read([new(Path.Combine(root, "missing"), []), new(root, []), new(local, [])], Now, TimeSpan.FromDays(30)));
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
        if (Directory.Exists(root)) Directory.Delete(root, true);
    }
}

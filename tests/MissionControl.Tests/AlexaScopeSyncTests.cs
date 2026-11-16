using MissionControl.Core;

namespace MissionControl.Tests;

public sealed class AlexaScopeSyncTests
{
    [SymlinkFact]
    public void Managed_store_rejects_linked_live_directory_and_database_file()
    {
        var root = Path.Combine(Path.GetTempPath(), "mc-linked-" + Guid.NewGuid().ToString("N"));
        var target = Path.Combine(root, "outside"); Directory.CreateDirectory(target);
        var database = Path.Combine(target, "workspace.db"); File.WriteAllText(database, "untouched");
        var linked = Path.Combine(root, "linked"); AlexaStoreOwnership.Require(linked);
        Directory.CreateSymbolicLink(Path.Combine(linked, "live"), target);
        Assert.Throws<ArgumentException>(() => AlexaStoreOwnership.Require(linked));
        var leaf = Path.Combine(root, "leaf"); AlexaStoreOwnership.Require(leaf);
        Directory.CreateDirectory(Path.Combine(leaf, "live"));
        File.CreateSymbolicLink(Path.Combine(leaf, "live", "workspace.db"), database);
        Assert.Throws<ArgumentException>(() => AlexaStoreOwnership.Require(leaf));
        Assert.Equal("untouched", File.ReadAllText(database));
    }
    [Fact]
    public void Managed_store_refuses_existing_unowned_database_before_writing_it()
    {
        var root = Path.Combine(Path.GetTempPath(), "mc-owned-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "live"));
        var path = Path.Combine(root, "live", "workspace.db");
        File.WriteAllText(path, "untouched-existing-database");
        Assert.Throws<ArgumentException>(() => AlexaStoreOwnership.Require(root));
        Assert.Equal("untouched-existing-database", File.ReadAllText(path));
        Assert.False(File.Exists(Path.Combine(root, AlexaStoreOwnership.Marker)));
        var fresh = Path.Combine(root, "fresh");
        AlexaStoreOwnership.Require(fresh);
        AlexaStoreOwnership.Require(fresh);
        Assert.Equal(AlexaStoreOwnership.Version, File.ReadAllText(Path.Combine(fresh, AlexaStoreOwnership.Marker)));
    }

    [Fact]
    public void Revocation_clears_marker_from_database_and_wal_while_store_is_open()
    {
        var root = Path.Combine(Path.GetTempPath(), "mc-purge-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var path = Path.Combine(root, "workspace.db");
        using var store = new WorkspaceStore(path);
        var project = Path.Combine(root, "project");
        store.SaveSource(new("source", "codex", "Synthetic", Path.Combine(root, "source"), true));
        store.SynchronizeAlexaScopes([new("Fixture", project, true)]);
        var id = Assert.Single(store.GetProjects()).Id;
        var stamp = DateTimeOffset.UtcNow.ToString("O");
        var marker = "REVOKED_PRIVATE_CANARY_" + Guid.NewGuid().ToString("N");
        store.CommitBatch([new("record", "source", "rollout.jsonl", "session", id, "codex", "message", marker, marker, "user", stamp, stamp, WorkingDirectory: project)]);
        store.SynchronizeAlexaScopes([]);
        foreach (var file in new[] { path, path + "-wal" }.Where(File.Exists))
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            Assert.DoesNotContain(marker, reader.ReadToEnd());
        }
    }
    [Fact]
    public void Synchronization_preserves_ids_reindexes_on_approval_and_purges_on_revocation()
    {
        var root = Path.Combine(Path.GetTempPath(), "mc-scope-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var store = new WorkspaceStore(Path.Combine(root, "workspace.db"));
        var project = Path.Combine(root, "project");
        store.SaveSource(new("source", "codex", "Synthetic", Path.Combine(root, "source"), true));
        Assert.True(store.SynchronizeAlexaScopes([new("Fixture", project, true)]));
        var id = Assert.Single(store.GetProjects()).Id;
        var stamp = DateTimeOffset.UtcNow.ToString("O");
        store.CommitBatch([new("record", "source", "rollout.jsonl", "session", id, "codex", "message", "Synthetic", "Synthetic", "user", stamp, stamp, WorkingDirectory: project)]);
        Assert.Single(store.QuerySessions(new()).Records);
        Assert.False(store.SynchronizeAlexaScopes([new("Fixture", project, true)]));
        Assert.True(store.SynchronizeAlexaScopes([new("Renamed", project, false)]));
        Assert.Equal(id, Assert.Single(store.GetProjects()).Id);
        Assert.Empty(store.QuerySessions(new()).Records);
        Assert.Null(store.GetRecord("record"));
        Assert.False(store.CanIndexWorkingDirectory(project));
        store.SynchronizeAlexaScopes([new("Renamed", project, true)]);
        Assert.True(store.CanIndexWorkingDirectory(project));
        foreach (var broad in new[] { Path.GetPathRoot(root)!, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory) }.Where(p => !string.IsNullOrEmpty(p)))
            foreach (var spelling in new[] { broad + Path.DirectorySeparatorChar, Path.Combine(broad, "."), Path.Combine(broad, "child", "..") })
                Assert.Throws<ArgumentException>(() => store.SynchronizeAlexaScopes([new("Bad", spelling, true)]));
    }

    [Fact]
    public void Denied_and_removed_nested_roots_never_inherit_parent_approval()
    {
        var root = Path.Combine(Path.GetTempPath(), "mc-scope-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var store = new WorkspaceStore(Path.Combine(root, "workspace.db"));
        var parent = Path.Combine(root, "parent"); var child = Path.Combine(parent, "private");
        store.SynchronizeAlexaScopes([new("Parent", parent, true), new("Private", child, false)]);
        Assert.True(store.CanIndexWorkingDirectory(parent));
        Assert.False(store.CanIndexWorkingDirectory(child));
        store.SynchronizeAlexaScopes([new("Parent", parent, true)]);
        Assert.False(store.CanIndexWorkingDirectory(child));
        store.SynchronizeAlexaScopes([]);
        Assert.False(store.CanIndexWorkingDirectory(parent));
    }

    [Fact]
    public void Invalid_snapshot_leaves_previous_allowlist_intact_and_equal_roots_are_rejected()
    {
        var root = Path.Combine(Path.GetTempPath(), "mc-scope-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var store = new WorkspaceStore(Path.Combine(root, "workspace.db"));
        var project = Path.Combine(root, "project");
        store.SynchronizeAlexaScopes([new("Fixture", project, true)]);
        Assert.Throws<ArgumentException>(() => store.SynchronizeAlexaScopes([new("Bad", Path.GetPathRoot(root)!, true)]));
        Assert.True(store.CanIndexWorkingDirectory(project));
        Assert.Throws<ArgumentException>(() => store.SynchronizeAlexaScopes([new("A", project, true), new("B", project, false)]));
        Assert.True(store.CanIndexWorkingDirectory(project));
    }

    [Fact]
    public void Legacy_manual_id_is_reused_without_an_equal_root_alias()
    {
        var root = Path.Combine(Path.GetTempPath(), "mc-scope-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var store = new WorkspaceStore(Path.Combine(root, "workspace.db"));
        var project = Path.Combine(root, "project");
        store.SaveProject(new("legacy", "Fixture", "#000000", "", project));
        store.SetProjectApproval("legacy", true);
        store.SynchronizeAlexaScopes([new("Fixture", project, true)]);
        Assert.Equal("legacy", Assert.Single(store.GetProjects()).Id);
        Assert.True(store.CanIndexWorkingDirectory(project));
    }
}

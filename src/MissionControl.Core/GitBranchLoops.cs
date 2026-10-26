using System.Globalization;
using System.Text.RegularExpressions;

namespace MissionControl.Core;

/// <summary>A local branch with work that is not on its remote. State is "ahead" or "never_pushed".</summary>
public sealed record UnpushedBranch(string Repository, string Checkout, string Branch, string State, string LastCommitAt);

/// <summary>
/// Finds local branches whose work has not been pushed, from Git's own metadata files: refs, packed-refs, the branch
/// sections of the repository config and the reflogs. Like repository discovery, it never starts Git and never
/// fetches, so it reports against the remote state of the last fetch or push.
/// </summary>
/// <remarks>
/// Without walking commit history, "ahead" means the branch differs from its remote-tracking branch and moved more
/// recently than it. A branch that is only behind (moved by a later fetch) is not listed, and neither is a branch
/// whose remote branch was deleted, which usually follows a merge. "never_pushed" needs the developer's own work in
/// the reflog, so fetched pull requests and branches only created from another are left out, as are repositories
/// with no remote at all.
/// </remarks>
public static class GitBranchLoops
{
    private const int MaximumRefs = 2000;
    private const int MaximumPackedRefsBytes = 8 * 1024 * 1024;
    private const int MaximumConfigBytes = 64 * 1024;
    private const int MaximumBranches = 50;
    private const int MaximumReflogBytes = 64 * 1024;
    private static readonly Regex LocalWorkEntry = new(@"^(?:commit|merge|cherry-pick|revert|rebase|am)\b", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex Hash = new("^(?:[0-9a-f]{40}|[0-9a-f]{64})$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex BranchSection = new("^\\[\\s*branch\\s+\"((?:\\\\.|[^\"\\\\])*)\"\\s*\\]\\s*(?:[#;].*)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    public static IReadOnlyList<UnpushedBranch> Read(IEnumerable<ProjectRepository> repositories, DateTimeOffset now, TimeSpan window, CancellationToken token = default)
    {
        var result = new List<(UnpushedBranch Branch, DateTimeOffset At)>();
        var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var repository in repositories)
        {
            token.ThrowIfCancellationRequested();
            // Worktrees of one repository share its branches; read each repository once.
            if (GitWorkspaceDiscovery.CommonDirectory(repository.LocalRoot) is not { } common || !seen.Add(common)) continue;
            var label = repository.GitHubRepositories.FirstOrDefault() ?? Path.GetFileName(Path.TrimEndingDirectorySeparator(repository.LocalRoot));
            try { result.AddRange(ReadRepository(common, label, repository.LocalRoot, now - window, token)); }
            catch (Exception error) when (GitWorkspaceDiscovery.IsFileError(error) || error is RegexMatchTimeoutException) { }
        }
        return result.OrderByDescending(item => item.At).Take(MaximumBranches).Select(item => item.Branch).ToArray();
    }

    private static IEnumerable<(UnpushedBranch, DateTimeOffset)> ReadRepository(string common, string label, string checkout, DateTimeOffset since, CancellationToken token)
    {
        var heads = Refs(common, "heads");
        var remotes = Refs(common, "remotes");
        // A repository that has never fetched or pushed is kept local on purpose.
        if (heads.Count == 0 || remotes.Count == 0) yield break;
        var upstreams = Upstreams(common);
        var remoteHashes = remotes.Values.ToHashSet(StringComparer.Ordinal);
        foreach (var (branch, hash) in heads)
        {
            token.ThrowIfCancellationRequested();
            var local = History(common, "heads/" + branch);
            if (local.Moved is not { } at || at < since) continue;
            var configured = upstreams.TryGetValue(branch, out var upstream);
            var tracking = configured ? upstream : remotes.ContainsKey("origin/" + branch) ? "origin/" + branch : null;
            if (tracking is not null && remotes.TryGetValue(tracking, out var remoteHash))
            {
                if (remoteHash == hash || History(common, "remotes/" + tracking).Moved is { } fetched && fetched >= at) continue;
                yield return (new(label, checkout, branch, "ahead", at.ToString("O", CultureInfo.InvariantCulture)), at);
            }
            // A configured upstream that no longer exists was usually deleted after a merge. Branches made by a fetch
            // (someone's pull request) or only created from another branch hold no work of the developer's own.
            else if (!configured && tracking is null && local.LocalWork && !remoteHashes.Contains(hash))
                yield return (new(label, checkout, branch, "never_pushed", at.ToString("O", CultureInfo.InvariantCulture)), at);
        }
    }

    /// <summary>Branch name (relative to refs/heads or refs/remotes) to object hash; loose refs override packed ones.</summary>
    private static Dictionary<string, string> Refs(string common, string kind)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var prefix = "refs/" + kind + "/";
        var packed = Path.Combine(common, "packed-refs");
        if (File.Exists(packed) && GitWorkspaceDiscovery.ReadBounded(packed, MaximumPackedRefsBytes) is { } text)
            foreach (var raw in text.Split('\n'))
            {
                var line = raw.TrimEnd('\r');
                var space = line.IndexOf(' ');
                if (space < 0 || line.StartsWith('#') || line.StartsWith('^') || !Hash.IsMatch(line[..space])) continue;
                var name = line[(space + 1)..];
                if (name.StartsWith(prefix, StringComparison.Ordinal) && ValidName(name[prefix.Length..])) result[name[prefix.Length..]] = line[..space];
            }
        var root = Path.Combine(common, "refs", kind);
        if (!Directory.Exists(root) || !GitWorkspaceDiscovery.HasSafeAncestors(root)) return result;
        var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, MaxRecursionDepth = 16, IgnoreInaccessible = true };
        foreach (var file in Directory.EnumerateFiles(root, "*", options).Take(MaximumRefs))
        {
            var name = Path.GetRelativePath(root, file).Replace('\\', '/');
            // Symbolic refs such as origin/HEAD hold "ref: ..." and are skipped.
            if (ValidName(name) && GitWorkspaceDiscovery.ReadBounded(file, 256)?.Trim() is { } value && Hash.IsMatch(value)) result[name] = value;
        }
        return result;
    }

    /// <summary>Branch to its remote-tracking branch ("origin/feature"), from [branch "x"] remote and merge.</summary>
    private static Dictionary<string, string> Upstreams(string common)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var file = Path.Combine(common, "config");
        if (!File.Exists(file) || GitWorkspaceDiscovery.ReadBounded(file, MaximumConfigBytes) is not { } config) return result;
        string? branch = null, remote = null, merge = null;
        void Flush()
        {
            if (branch is not null && remote is { Length: > 0 } and not "." && merge?.StartsWith("refs/heads/", StringComparison.Ordinal) == true && ValidName(merge[11..]))
                result[branch] = remote + "/" + merge[11..];
        }
        var continuation = "";
        foreach (var raw in config.Split('\n'))
        {
            var line = continuation + raw.TrimEnd('\r');
            if (GitWorkspaceDiscovery.HasContinuation(line)) { continuation = line[..^1]; continue; }
            continuation = "";
            line = line.Trim();
            if (line.StartsWith('['))
            {
                Flush();
                var section = BranchSection.Match(line);
                (branch, remote, merge) = (section.Success && ValidName(section.Groups[1].Value) ? section.Groups[1].Value : null, null, null);
                continue;
            }
            if (branch is null) continue;
            var equals = line.IndexOf('=');
            if (equals < 0) continue;
            var key = line[..equals].Trim();
            var value = GitWorkspaceDiscovery.ReadConfigValue(line[(equals + 1)..]);
            if (key.Equals("remote", StringComparison.OrdinalIgnoreCase)) remote = value;
            else if (key.Equals("merge", StringComparison.OrdinalIgnoreCase)) merge = value;
        }
        Flush();
        return result;
    }

    /// <summary>
    /// When a ref last moved (its newest reflog entry, or the loose ref file's write time without a reflog) and whether
    /// its recent reflog shows the developer's own work: a commit, merge, cherry-pick, revert or rebase.
    /// </summary>
    private static (DateTimeOffset? Moved, bool LocalWork) History(string common, string name)
    {
        var log = Path.Combine(common, "logs", "refs", name);
        if (File.Exists(log) && GitWorkspaceDiscovery.HasSafeAncestors(log))
        {
            using var input = new FileStream(log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var length = (int)Math.Min(input.Length, MaximumReflogBytes);
            input.Seek(-length, SeekOrigin.End);
            var buffer = new byte[length];
            input.ReadExactly(buffer);
            DateTimeOffset? moved = null;
            var work = false;
            // "<old> <new> <name> <email> <seconds> <zone>\t<message>"; a tail that starts mid-file begins with a partial line.
            foreach (var entry in System.Text.Encoding.UTF8.GetString(buffer).Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(length < input.Length ? 1 : 0))
            {
                var tab = entry.IndexOf('\t');
                var fields = (tab < 0 ? entry : entry[..tab]).Split(' ');
                if (fields.Length >= 4 && long.TryParse(fields[^2], NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)) moved = DateTimeOffset.FromUnixTimeSeconds(seconds);
                work |= tab >= 0 && LocalWorkEntry.IsMatch(entry[(tab + 1)..]);
            }
            if (moved is not null) return (moved, work);
        }
        var loose = Path.Combine(common, "refs", name);
        return (File.Exists(loose) ? new DateTimeOffset(File.GetLastWriteTimeUtc(loose), TimeSpan.Zero) : null, false);
    }

    private static bool ValidName(string name) =>
        name.Length is > 0 and <= 255 && !name.Contains("..", StringComparison.Ordinal) && !name.StartsWith('/') && !name.EndsWith('/')
        && !name.EndsWith(".lock", StringComparison.Ordinal) && name != "HEAD" && !name.EndsWith("/HEAD", StringComparison.Ordinal) && !name.Any(char.IsControl);
}

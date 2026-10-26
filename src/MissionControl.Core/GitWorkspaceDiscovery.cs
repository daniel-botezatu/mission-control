using System.Text;
using System.Text.RegularExpressions;

namespace MissionControl.Core;

public sealed record GitCheckout(string CheckoutPath, IReadOnlyList<string> GitHubRepositories);
/// <summary>LinkedWorktrees are temporary checkouts of a listed repository: attribution paths, never repositories.</summary>
public sealed record GitWorkspaceDiscoveryResult(string RootPath, string Name, IReadOnlyList<GitCheckout> Repositories, IReadOnlyList<string> Gaps, IReadOnlyList<string>? LinkedWorktrees = null);

public interface IGitWorkspaceDiscovery
{
    GitWorkspaceDiscoveryResult? Discover(string cwd, CancellationToken cancellationToken = default);
    /// <summary>Agent scratch space that must never become a project.</summary>
    bool IsExcludedWorkspace(string path) => false;
    bool IsLinkedWorktree(string path) => false;
}

/// <summary>Reads bounded Git metadata under an observed working directory. Never starts Git or reads include files.</summary>
/// <param name="excludedRoots">Folders whose sessions never form projects; the default excludes nothing.</param>
public sealed class GitWorkspaceDiscovery(IReadOnlyList<string>? excludedRoots = null) : IGitWorkspaceDiscovery
{
    private static readonly Regex CodexChatFolder = new(@"^\d{4}-\d{2}-\d{2}$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private readonly string[] _excludedRoots = (excludedRoots ?? []).Select(NormalizeLocalPath).OfType<string>().ToArray();
    private readonly string? _codexChats = excludedRoots is null ? null : CodexChatsRoot();

    /// <summary>Production policy: temporary folders (agent scratchpads) and Codex's date-stamped chat folders.</summary>
    public static GitWorkspaceDiscovery ForCurrentUser() => new([Path.GetTempPath()]);

    public bool IsExcludedWorkspace(string path)
    {
        var normalized = NormalizeLocalPath(path);
        if (normalized is null) return false;
        if (_excludedRoots.Any(root => IsWithin(normalized, root))) return true;
        // Codex creates <Documents>\Codex\<yyyy-mm-dd>\<chat> for conversations without a project.
        if (_codexChats is null || !IsWithin(normalized, _codexChats) || normalized.Length <= _codexChats.Length + 1) return false;
        var first = normalized[(_codexChats.Length + 1)..].Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
        return CodexChatFolder.IsMatch(first);
    }

    public bool IsLinkedWorktree(string path) => NormalizeLocalPath(path) is { } normalized && MainCheckout(normalized) is not null;

    private static string? CodexChatsRoot()
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        return string.IsNullOrWhiteSpace(documents) ? null : NormalizeLocalPath(Path.Combine(documents, "Codex"));
    }

    private static bool IsWithin(string candidate, string parent) =>
        string.Equals(candidate, parent, PathComparison) || candidate.StartsWith(parent.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, PathComparison);
    public const int MaximumDepth = 4;
    public const int MaximumDirectories = 512;
    public const int MaximumRepositories = 64;
    private const int MaximumConfigBytes = 64 * 1024;
    private const int MaximumPointerBytes = 4096;
    private static readonly StringComparison PathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", "node_modules", "vendor", "bin", "obj", "dist", "build", "target", ".next", ".nuxt", ".venv", "venv", "__pycache__", ".cache"
    };
    private static readonly Regex RemoteSection = new("^\\[\\s*remote\\s+(?:\"(?:\\\\.|[^\"])*\"|[^\\]\\s]+)\\s*\\]\\s*(?:[#;].*)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex RepositoryName = new("^[A-Za-z0-9_.-]{1,100}$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex OwnerName = new("^[A-Za-z0-9][A-Za-z0-9-]{0,38}$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    public GitWorkspaceDiscoveryResult? Discover(string cwd, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var requested = NormalizeLocalPath(cwd);
        if (requested is null || IsExcludedWorkspace(requested) || !Directory.Exists(requested) || !HasSafeAncestors(requested)) return null;
        var gaps = new HashSet<string>(StringComparer.Ordinal);
        var linked = new List<string>();
        string? nearest = null;
        var current = requested;
        for (var depth = 0; current is not null && depth < 128; depth++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (MarkerExists(current)) { nearest = current; break; }
            current = Directory.GetParent(current)?.FullName;
        }
        // A linked worktree is a temporary checkout of its main repository; group it there.
        if (nearest is not null && MainCheckout(nearest) is { } main)
        {
            linked.Add(nearest);
            nearest = main;
        }
        var root = nearest ?? requested;
        if (IsExcludedWorkspace(root)) return null;
        var checkouts = new List<GitCheckout>();
        if (nearest is not null) checkouts.Add(ReadCheckout(nearest, gaps, cancellationToken));
        if (IsBroadContainer(root))
        {
            gaps.Add("Automatic repository scanning skips drive roots, home folders, and Desktop containers; use a specific working folder.");
            return Result(root, checkouts, gaps, linked);
        }

        var queue = new Queue<(string Path, int Depth)>();
        queue.Enqueue((root, 0));
        var inspected = 1;
        while (queue.TryDequeue(out var directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (directory.Depth >= MaximumDepth) continue;
            try
            {
                foreach (var child in Directory.EnumerateDirectories(directory.Path))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var name = Path.GetFileName(child);
                    if (ExcludedDirectories.Contains(name) || name.StartsWith('.')) continue;
                    if (++inspected > MaximumDirectories)
                    {
                        gaps.Add("Automatic repository discovery reached its 512-directory limit.");
                        return Result(root, checkouts, gaps, linked);
                    }
                    if (!HasSafeAncestors(child)) { gaps.Add("Symbolic links, junctions, and inaccessible directories were skipped."); continue; }
                    if (MarkerExists(child))
                    {
                        if (MainCheckout(child) is not null) { linked.Add(child); continue; }
                        if (checkouts.Count == MaximumRepositories)
                        {
                            gaps.Add("Automatic repository discovery reached its 64-repository limit.");
                            return Result(root, checkouts, gaps, linked);
                        }
                        checkouts.Add(ReadCheckout(child, gaps, cancellationToken));
                        // A checkout's own source tree is not a workspace of further projects.
                        continue;
                    }
                    queue.Enqueue((child, directory.Depth + 1));
                }
            }
            catch (Exception error) when (IsFileError(error)) { gaps.Add("Some repository folders could not be read."); }
        }
        return Result(root, checkouts, gaps, linked);
    }

    private static GitWorkspaceDiscoveryResult Result(string root, List<GitCheckout> repositories, HashSet<string> gaps, List<string> linked) =>
        new(root, Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)), repositories.OrderBy(x => x.CheckoutPath, StringComparer.OrdinalIgnoreCase).ToArray(), gaps.Order(StringComparer.Ordinal).ToArray(),
            linked.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToArray());

    /// <summary>The main working tree of a linked worktree, from its gitdir and commondir pointers; null otherwise.</summary>
    private static string? MainCheckout(string checkout)
    {
        var marker = Path.Combine(checkout, ".git");
        try
        {
            if (!File.Exists(marker) || !HasSafeAncestors(marker)) return null;
            var pointer = ReadBounded(marker, MaximumPointerBytes)?.Trim();
            if (pointer is null || !pointer.StartsWith("gitdir:", StringComparison.OrdinalIgnoreCase) || pointer.Contains('\n') || pointer.Contains('\r')) return null;
            var gitDirectory = ResolveMetadataPath(checkout, pointer[7..].Trim());
            if (gitDirectory is null) return null;
            var commonFile = Path.Combine(gitDirectory, "commondir");
            // Submodules point at .git/modules/<name> without a commondir; they are not worktrees.
            if (!File.Exists(commonFile) || !HasSafeAncestors(commonFile)) return null;
            var common = ReadBounded(commonFile, MaximumPointerBytes)?.Trim();
            if (common is null || common.Contains('\n') || common.Contains('\r')) return null;
            var commonDirectory = ResolveMetadataPath(gitDirectory, common);
            // A bare repository has no main working tree, so its worktrees remain the checkouts.
            if (commonDirectory is null || !string.Equals(Path.GetFileName(commonDirectory), ".git", StringComparison.OrdinalIgnoreCase)) return null;
            var main = Path.GetDirectoryName(commonDirectory);
            return main is not null && !string.Equals(main, checkout, PathComparison) && Directory.Exists(main) && MarkerExists(main) && HasSafeAncestors(main) ? main : null;
        }
        catch (Exception error) when (IsFileError(error)) { return null; }
    }

    private static bool IsBroadContainer(string path)
    {
        if (string.Equals(path, Path.GetPathRoot(path), PathComparison)) return true;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        return (!string.IsNullOrWhiteSpace(home) && string.Equals(path, Path.TrimEndingDirectorySeparator(Path.GetFullPath(home)), PathComparison))
            || (!string.IsNullOrWhiteSpace(desktop) && string.Equals(path, Path.TrimEndingDirectorySeparator(Path.GetFullPath(desktop)), PathComparison));
    }

    private static string? NormalizeLocalPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 32768 || path.Contains('\0') || !Path.IsPathFullyQualified(path)) return null;
        // UNC, device paths, and remote mounts expressed as Windows paths are outside the native observer contract.
        if (path.StartsWith("\\\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal)) return null;
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
        catch (Exception error) when (IsFileError(error)) { return null; }
    }

    private static bool HasSafeAncestors(string path)
    {
        try
        {
            for (string? current = path; current is not null; current = Directory.GetParent(current)?.FullName)
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return false;
            }
            return true;
        }
        catch (Exception error) when (IsFileError(error)) { return false; }
    }

    private static bool MarkerExists(string checkout)
    {
        var marker = Path.Combine(checkout, ".git");
        return File.Exists(marker) || Directory.Exists(marker);
    }

    private static GitCheckout ReadCheckout(string checkout, HashSet<string> gaps, CancellationToken cancellationToken)
    {
        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var marker = Path.Combine(checkout, ".git");
        try
        {
            if (!HasSafeAncestors(marker))
            {
                gaps.Add("Symbolic links, junctions, and inaccessible Git metadata were skipped.");
                return new(checkout, []);
            }
            var gitDirectory = marker;
            if (File.Exists(marker))
            {
                var pointer = ReadBounded(marker, MaximumPointerBytes)?.Trim();
                if (pointer is null || !pointer.StartsWith("gitdir:", StringComparison.OrdinalIgnoreCase) || pointer.Contains('\n') || pointer.Contains('\r'))
                {
                    gaps.Add("A Git worktree pointer was invalid or exceeded its size limit.");
                    return new(checkout, []);
                }
                gitDirectory = ResolveMetadataPath(checkout, pointer[7..].Trim()) ?? "";
                if (!Directory.Exists(gitDirectory) || !HasSafeAncestors(gitDirectory))
                {
                    gaps.Add("A Git worktree metadata directory was unavailable or unsafe.");
                    return new(checkout, []);
                }
            }
            var commonDirectory = gitDirectory;
            var commonFile = Path.Combine(gitDirectory, "commondir");
            if (File.Exists(commonFile))
            {
                if (!HasSafeAncestors(commonFile)) { gaps.Add("An unsafe Git common-directory pointer was skipped."); return new(checkout, []); }
                var common = ReadBounded(commonFile, MaximumPointerBytes)?.Trim();
                commonDirectory = common is null || common.Contains('\n') || common.Contains('\r') ? "" : ResolveMetadataPath(gitDirectory, common) ?? "";
                if (!Directory.Exists(commonDirectory) || !HasSafeAncestors(commonDirectory))
                {
                    gaps.Add("A Git common metadata directory was unavailable or unsafe.");
                    return new(checkout, []);
                }
            }
            ReadAliases(Path.Combine(commonDirectory, "config"), aliases, gaps, cancellationToken);
            // config.worktree is a separate, direct metadata file; includes and global configuration are intentionally ignored.
            if (File.Exists(Path.Combine(gitDirectory, "config.worktree"))) ReadAliases(Path.Combine(gitDirectory, "config.worktree"), aliases, gaps, cancellationToken);
        }
        catch (Exception error) when (IsFileError(error)) { gaps.Add("Some Git metadata could not be read."); }
        return new(checkout, aliases.Order(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private static string? ResolveMetadataPath(string basePath, string pointer)
    {
        if (string.IsNullOrWhiteSpace(pointer)) return null;
        try { return NormalizeLocalPath(Path.IsPathFullyQualified(pointer) ? pointer : Path.GetFullPath(pointer, basePath)); }
        catch (Exception error) when (IsFileError(error)) { return null; }
    }

    private static string? ReadBounded(string path, int maximumBytes)
    {
        if (!HasSafeAncestors(path)) return null;
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (input.Length > maximumBytes) return null;
        var buffer = new byte[maximumBytes + 1];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = input.Read(buffer, length, buffer.Length - length);
            if (read == 0) break;
            length += read;
        }
        return length > maximumBytes ? null : new UTF8Encoding(false, true).GetString(buffer, 0, length).TrimStart('\uFEFF');
    }

    private static void ReadAliases(string file, HashSet<string> aliases, HashSet<string> gaps, CancellationToken cancellationToken)
    {
        if (!File.Exists(file)) return;
        var config = ReadBounded(file, MaximumConfigBytes);
        if (config is null) { gaps.Add("A Git configuration was unsafe or exceeded its 64 KiB size limit."); return; }
        var inRemote = false;
        var continuation = "";
        foreach (var rawLine in config.Split('\n'))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = continuation + rawLine.TrimEnd('\r');
            if (HasContinuation(line)) { continuation = line[..^1]; continue; }
            continuation = "";
            line = line.Trim();
            if (line.StartsWith('['))
            {
                inRemote = RemoteSection.IsMatch(line);
                if (line.StartsWith("[include", StringComparison.OrdinalIgnoreCase)) gaps.Add("Git config include directives are not followed during automatic discovery.");
                continue;
            }
            if (!inRemote) continue;
            var equals = line.IndexOf('=');
            if (equals < 0 || !line[..equals].Trim().Equals("url", StringComparison.OrdinalIgnoreCase)) continue;
            var value = ReadConfigValue(line[(equals + 1)..]);
            if (value is not null && NormalizeGitHubRepository(value) is { } repository) aliases.Add(repository);
        }
    }

    private static bool HasContinuation(string line)
    {
        var count = 0;
        for (var index = line.Length - 1; index >= 0 && line[index] == '\\'; index--) count++;
        return count % 2 == 1;
    }

    private static string? ReadConfigValue(string value)
    {
        var result = new StringBuilder();
        var quoted = false;
        var escaped = false;
        foreach (var character in value.Trim())
        {
            if (escaped)
            {
                var decoded = character switch { '\\' => '\\', '"' => '"', 'n' => '\n', 't' => '\t', 'b' => '\b', _ => '\0' };
                if (decoded == '\0') return null;
                result.Append(decoded); escaped = false;
            }
            else if (character == '\\') escaped = true;
            else if (character == '"') quoted = !quoted;
            else if (!quoted && character is '#' or ';') break;
            else result.Append(character);
        }
        return quoted || escaped ? null : result.ToString().Trim();
    }

    public static string? NormalizeGitHubRepository(string? remote)
    {
        if (string.IsNullOrWhiteSpace(remote) || remote.Length > 4096 || remote.Any(char.IsWhiteSpace) || remote.Any(char.IsControl)) return null;
        string path;
        if (remote.StartsWith("git@github.com:", StringComparison.OrdinalIgnoreCase)) path = remote[15..];
        else
        {
            if (!Uri.TryCreate(remote, UriKind.Absolute, out var uri) || !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
                || uri.Scheme is not ("https" or "ssh") || uri.Query.Length != 0 || uri.Fragment.Length != 0) return null;
            if (uri.Scheme == "https" && !uri.IsDefaultPort || uri.Scheme == "ssh" && uri.Port is not (-1 or 22)) return null;
            path = uri.AbsolutePath.TrimStart('/');
        }
        if (path.EndsWith('/')) path = path[..^1];
        if (path.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) path = path[..^4];
        var segments = path.Split('/');
        if (segments.Length != 2 || !OwnerName.IsMatch(segments[0]) || segments[0].EndsWith('-') || !RepositoryName.IsMatch(segments[1]) || segments[1] is "." or "..") return null;
        return $"{segments[0]}/{segments[1]}".ToLowerInvariant();
    }

    private static bool IsFileError(Exception error) => error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException;
}

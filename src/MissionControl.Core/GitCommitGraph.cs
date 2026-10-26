using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace MissionControl.Core;

/// <summary>
/// Reads commit parents and committer times from a repository's own object store: loose objects, version 2 pack
/// indexes with their packs (including delta chains) and listed alternates. Like repository discovery it never
/// starts Git. Whatever it cannot read makes an answer unknown rather than guessed.
/// </summary>
internal sealed class GitCommitGraph : IDisposable
{
    private const int MaximumObjectBytes = 1024 * 1024;
    private const int MaximumDeltaDepth = 64;
    private const int MaximumPacks = 256;
    private readonly int hashBytes;
    private readonly List<string> objectDirectories = [];
    private readonly HashSet<string> shallow = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Commit?> commits = new(StringComparer.Ordinal);
    private List<Pack>? packs;

    internal sealed record Commit(long Time, string[] Parents);

    internal GitCommitGraph(string commonDirectory, int hashHexLength)
    {
        hashBytes = hashHexLength / 2;
        var objects = Path.Combine(commonDirectory, "objects");
        objectDirectories.Add(objects);
        // Alternates hold objects shared with another local repository; one level is followed.
        var alternates = Path.Combine(objects, "info", "alternates");
        if (File.Exists(alternates) && GitWorkspaceDiscovery.ReadBounded(alternates, 64 * 1024) is { } listed)
            foreach (var line in listed.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0 && !line.StartsWith('#')).Take(4))
            {
                var directory = Path.GetFullPath(line, objects);
                if (Directory.Exists(directory) && GitWorkspaceDiscovery.HasSafeAncestors(directory)) objectDirectories.Add(directory);
            }
        // A shallow clone ends its history at these commits; their missing parents are not missing data.
        var shallowFile = Path.Combine(commonDirectory, "shallow");
        if (File.Exists(shallowFile) && GitWorkspaceDiscovery.ReadBounded(shallowFile, 4 * 1024 * 1024) is { } roots)
            foreach (var line in roots.Split('\n')) if (line.Trim() is { Length: > 0 } hash) shallow.Add(hash);
    }

    /// <summary>
    /// How many commits are reachable from <paramref name="tip"/> but from none of <paramref name="bases"/>
    /// (as <c>git rev-list tip --not bases</c> counts them); null when the object store cannot answer.
    /// </summary>
    internal int? CountOnlyIn(string tip, IReadOnlyCollection<string> bases, int maximumSteps = 20_000)
    {
        // Git's own walk for `rev-list tip --not bases` (limit_list): commits newest first; excluding a commit
        // excludes its already-read ancestors at once, so a descendant reached late because of an older commit
        // date still excludes ancestors walked before it; and the walk continues a few commits (Git's "slop")
        // after every queued commit is excluded, longer while dates run out of order.
        const int Slop = 5;
        var excluded = new HashSet<string>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var queue = new PriorityQueue<string, (long Time, long Order)>();
        var included = new List<string>();
        long order = 0;
        bool Add(string hash)
        {
            if (!seen.Add(hash)) return true;
            if (Read(hash) is not { } commit) return false;
            queue.Enqueue(hash, (-commit.Time, order++));
            return true;
        }
        void Exclude(string hash)
        {
            var pending = new Stack<string>();
            pending.Push(hash);
            while (pending.TryPop(out var current))
                if (excluded.Add(current) && commits.TryGetValue(current, out var known) && known is not null)
                    foreach (var parent in known.Parents) pending.Push(parent);
        }
        if (!Add(tip)) return null;
        foreach (var hash in bases)
        {
            if (!Add(hash)) return null;
            Exclude(hash);
        }
        var slop = Slop;
        for (var steps = 0; queue.TryDequeue(out var hash, out var priority); steps++)
        {
            if (steps == maximumSteps) return null;
            var leaving = excluded.Contains(hash);
            foreach (var parent in Read(hash)!.Parents)
            {
                if (!Add(parent)) { if (shallow.Contains(hash)) continue; return null; }
                if (leaving) Exclude(parent);
            }
            if (!leaving) { included.Add(hash); continue; }
            if (queue.Count == 0) break;
            queue.TryPeek(out _, out var next);
            slop = -priority.Time <= -next.Time || queue.UnorderedItems.Any(item => !excluded.Contains(item.Element)) ? Slop : slop - 1;
            if (slop == 0) break;
        }
        return included.Count(hash => !excluded.Contains(hash));
    }

    internal Commit? Read(string hash)
    {
        if (commits.TryGetValue(hash, out var known)) return known;
        Commit? commit = null;
        try
        {
            if (hash.Length == hashBytes * 2 && Object(hash, 0) is (1, var data)) commit = Parse(data);
        }
        // Malformed or truncated objects end in one of these; the commit then counts as unreadable.
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException or FormatException or ArgumentException
            or IndexOutOfRangeException or OverflowException) { }
        if (commits.Count < 200_000) commits[hash] = commit;
        return commit;
    }

    private static Commit? Parse(byte[] data)
    {
        var parents = new List<string>(2);
        long time = 0;
        foreach (var line in Encoding.UTF8.GetString(data).Split('\n'))
        {
            if (line.Length == 0) break;
            if (line.StartsWith("parent ", StringComparison.Ordinal)) parents.Add(line[7..].Trim());
            else if (line.StartsWith("committer ", StringComparison.Ordinal))
            {
                // "committer Name <email> <seconds> <zone>"
                var fields = line.Split(' ');
                if (fields.Length >= 3) long.TryParse(fields[^2], NumberStyles.None, CultureInfo.InvariantCulture, out time);
            }
        }
        return new(time, [.. parents]);
    }

    // (type, content): 1 commit, 2 tree, 3 blob, 4 tag; null when absent.
    private (int Type, byte[] Data)? Object(string hash, int depth)
    {
        if (depth > MaximumDeltaDepth) throw new InvalidDataException();
        foreach (var directory in objectDirectories)
        {
            var loose = Path.Combine(directory, hash[..2], hash[2..]);
            if (File.Exists(loose)) return Loose(loose);
        }
        var key = Convert.FromHexString(hash);
        foreach (var pack in Packs())
            if (pack.Find(key) is { } offset) return Packed(pack, offset, depth);
        return null;
    }

    private static (int, byte[]) Loose(string path)
    {
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var inflated = new ZLibStream(file, CompressionMode.Decompress);
        // "<type> <size>\0<content>"
        var header = new List<byte>(32);
        for (int b; (b = inflated.ReadByte()) > 0;)
        {
            if (header.Count == 32) throw new InvalidDataException();
            header.Add((byte)b);
        }
        var text = Encoding.ASCII.GetString([.. header]).Split(' ');
        if (text.Length != 2 || !int.TryParse(text[1], NumberStyles.None, CultureInfo.InvariantCulture, out var size) || size > MaximumObjectBytes) throw new InvalidDataException();
        var type = text[0] switch { "commit" => 1, "tree" => 2, "blob" => 3, "tag" => 4, _ => throw new InvalidDataException() };
        if (type != 1) return (type, []);
        var data = new byte[size];
        inflated.ReadExactly(data);
        return (type, data);
    }

    private (int, byte[]) Packed(Pack pack, long offset, int depth)
    {
        if (depth > MaximumDeltaDepth) throw new InvalidDataException();
        var header = pack.ReadAt(offset, 32 + hashBytes);
        var position = 0;
        int c = header[position++];
        var type = (c >> 4) & 7;
        long size = c & 15;
        for (var shift = 4; (c & 0x80) != 0; shift += 7)
        {
            if (shift > 56) throw new InvalidDataException();
            c = header[position++];
            size |= (long)(c & 0x7f) << shift;
        }
        if (size > MaximumObjectBytes) throw new InvalidDataException();
        switch (type)
        {
            case 1 or 2 or 3 or 4:
                return (type, type == 1 ? pack.Inflate(offset + position, (int)size) : []);
            case 6:
            {
                // Offset delta: the base lies the encoded distance back in the same pack.
                c = header[position++];
                long distance = c & 0x7f;
                while ((c & 0x80) != 0)
                {
                    if (distance > (long.MaxValue >> 8)) throw new InvalidDataException();
                    c = header[position++];
                    distance = ((distance + 1) << 7) | (long)(c & 0x7f);
                }
                if (distance <= 0 || distance > offset) throw new InvalidDataException();
                var (baseType, baseData) = Packed(pack, offset - distance, depth + 1);
                return (baseType, baseType == 1 ? Apply(baseData, pack.Inflate(offset + position, (int)size)) : []);
            }
            case 7:
            {
                var baseHash = Convert.ToHexStringLower(header.AsSpan(position, hashBytes));
                var resolved = Object(baseHash, depth + 1) ?? throw new InvalidDataException();
                return (resolved.Type, resolved.Type == 1 ? Apply(resolved.Data, pack.Inflate(offset + position + hashBytes, (int)size)) : []);
            }
            default: throw new InvalidDataException();
        }
    }

    // Git's delta format: source and target sizes, then copy-from-base and insert instructions.
    private static byte[] Apply(byte[] source, byte[] delta)
    {
        var position = 0;
        long Size()
        {
            long value = 0;
            for (var shift = 0; ; shift += 7)
            {
                if (position >= delta.Length || shift > 56) throw new InvalidDataException();
                var b = delta[position++];
                value |= (long)(b & 0x7f) << shift;
                if ((b & 0x80) == 0) return value;
            }
        }
        if (Size() != source.Length) throw new InvalidDataException();
        var length = Size();
        if (length > MaximumObjectBytes) throw new InvalidDataException();
        var target = new byte[length];
        var written = 0;
        while (position < delta.Length)
        {
            var op = delta[position++];
            if ((op & 0x80) != 0)
            {
                long from = 0, count = 0;
                for (var bit = 0; bit < 4; bit++) if ((op & (1 << bit)) != 0) from |= (long)delta[position++] << (8 * bit);
                for (var bit = 0; bit < 3; bit++) if ((op & (0x10 << bit)) != 0) count |= (long)delta[position++] << (8 * bit);
                if (count == 0) count = 0x10000;
                if (from + count > source.Length || written + count > target.Length) throw new InvalidDataException();
                Array.Copy(source, from, target, written, count);
                written += (int)count;
            }
            else if (op != 0)
            {
                if (position + op > delta.Length || written + op > target.Length) throw new InvalidDataException();
                Array.Copy(delta, position, target, written, op);
                position += op; written += op;
            }
            else throw new InvalidDataException();
        }
        if (written != target.Length) throw new InvalidDataException();
        return target;
    }

    private List<Pack> Packs()
    {
        if (packs is not null) return packs;
        packs = [];
        foreach (var directory in objectDirectories)
        {
            var folder = Path.Combine(directory, "pack");
            if (!Directory.Exists(folder)) continue;
            foreach (var index in Directory.EnumerateFiles(folder, "*.idx").Take(MaximumPacks))
                if (Pack.Open(index, hashBytes) is { } pack) packs.Add(pack);
        }
        return packs;
    }

    public void Dispose()
    {
        foreach (var pack in packs ?? []) pack.Dispose();
    }

    /// <summary>A version 2 pack index and its pack, read on demand without loading either into memory.</summary>
    private sealed class Pack : IDisposable
    {
        private readonly FileStream index, pack;
        private readonly int hashBytes;
        private readonly uint[] fanout = new uint[256];
        private readonly long count;

        private Pack(FileStream index, FileStream pack, int hashBytes)
        {
            (this.index, this.pack, this.hashBytes) = (index, pack, hashBytes);
            var table = ReadExact(index, 8, 1024);
            for (var i = 0; i < 256; i++) fanout[i] = BinaryPrimitives.ReadUInt32BigEndian(table.AsSpan(i * 4));
            count = fanout[255];
        }

        internal static Pack? Open(string indexPath, int hashBytes)
        {
            var packPath = Path.ChangeExtension(indexPath, ".pack");
            if (!File.Exists(packPath)) return null;
            FileStream? index = null, pack = null;
            try
            {
                index = new FileStream(indexPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.RandomAccess);
                var header = ReadExact(index, 0, 8);
                // "\377tOc", version 2
                if (header[0] != 0xff || header[1] != (byte)'t' || header[2] != (byte)'O' || header[3] != (byte)'c' || BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(4)) != 2) { index.Dispose(); return null; }
                pack = new FileStream(packPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.RandomAccess);
                return new Pack(index, pack, hashBytes);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                index?.Dispose(); pack?.Dispose();
                return null;
            }
        }

        internal long? Find(byte[] key)
        {
            long low = key[0] == 0 ? 0 : fanout[key[0] - 1], high = fanout[key[0]];
            const long hashes = 8 + 1024;
            while (low < high)
            {
                var middle = (low + high) / 2;
                var candidate = ReadExact(index, hashes + middle * hashBytes, hashBytes);
                var order = candidate.AsSpan().SequenceCompareTo(key);
                if (order == 0)
                {
                    var offsets = hashes + count * hashBytes + count * 4;
                    var small = BinaryPrimitives.ReadUInt32BigEndian(ReadExact(index, offsets + middle * 4, 4));
                    if ((small & 0x80000000) == 0) return small;
                    var large = offsets + count * 4 + (small & 0x7fffffff) * 8;
                    return (long)BinaryPrimitives.ReadUInt64BigEndian(ReadExact(index, large, 8));
                }
                if (order < 0) low = middle + 1; else high = middle;
            }
            return null;
        }

        internal byte[] ReadAt(long offset, int length)
        {
            var buffer = new byte[(int)Math.Max(0, Math.Min(length, pack.Length - offset))];
            pack.Position = offset;
            pack.ReadExactly(buffer);
            return buffer;
        }

        internal byte[] Inflate(long offset, int size)
        {
            pack.Position = offset;
            using var inflated = new ZLibStream(pack, CompressionMode.Decompress, leaveOpen: true);
            var data = new byte[size];
            inflated.ReadExactly(data);
            return data;
        }

        private static byte[] ReadExact(FileStream stream, long offset, int length)
        {
            stream.Position = offset;
            var buffer = new byte[length];
            stream.ReadExactly(buffer);
            return buffer;
        }

        public void Dispose() { index.Dispose(); pack.Dispose(); }
    }
}

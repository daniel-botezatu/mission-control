using ZstdSharp;

namespace MissionControl.Core;

/// <summary>
/// Codex compresses idle rollouts from <c>name.jsonl</c> to <c>name.jsonl.zst</c> and removes the original.
/// Both names are the same conversation: they share one stream identity and checkpoint, and records keep
/// their identities, so reading the compressed copy continues where the plain file left off and never
/// duplicates history. Source files are only ever read.
/// </summary>
internal static class RolloutFiles
{
    public const string CompressedSuffix = ".zst";

    public static bool IsCompressed(string path) => path.EndsWith(".jsonl" + CompressedSuffix, StringComparison.OrdinalIgnoreCase);

    /// <summary>The plain transcript path a file stands for: <c>name.jsonl</c> for both spellings.</summary>
    public static string Logical(string path) => IsCompressed(path) ? path[..^CompressedSuffix.Length] : path;

    /// <summary>The file holding a conversation now: the plain file while it exists, otherwise its compressed copy.</summary>
    public static string? Existing(string path)
    {
        var logical = Logical(path);
        if (File.Exists(logical)) return logical;
        var compressed = logical + CompressedSuffix;
        return File.Exists(compressed) ? compressed : null;
    }

    /// <summary>True when the conversation still exists under either spelling.</summary>
    public static bool Exists(string path) => Existing(path) is not null;

    /// <summary>The conversation's bytes as written: plain files as-is, compressed files decompressed.</summary>
    public static Stream Open(string path) => IsCompressed(path)
        ? new DecompressedFile(path)
        : new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
}

/// <summary>
/// Read-only, seekable view of a .zst file's decompressed bytes, without holding them in memory.
/// Seeking forward skips decompressed bytes; seeking backward restarts from the beginning.
/// </summary>
internal sealed class DecompressedFile(string path) : Stream
{
    private Stream source = OpenDecompressed(path);
    private readonly byte[] skip = new byte[65536];
    private long position;
    private long? length;

    private static Stream OpenDecompressed(string path) => new DecompressionStream(
        new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 65536, FileOptions.SequentialScan), leaveOpen: false);

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;

    // Counted once by decompressing, so a file holding several frames is measured correctly.
    public override long Length => length ??= Count();
    public override long Position { get => position; set => Seek(value, SeekOrigin.Begin); }

    private long Count()
    {
        using var counter = OpenDecompressed(path);
        long total = 0;
        for (int read; (read = counter.Read(skip, 0, skip.Length)) > 0;) total += read;
        return total;
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        var read = source.Read(buffer);
        position += read;
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
        position += read;
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override long Seek(long offset, SeekOrigin origin)
    {
        var target = origin switch { SeekOrigin.Begin => offset, SeekOrigin.Current => position + offset, _ => Length + offset };
        if (target < 0) throw new IOException("Cannot seek before the start of a compressed transcript.");
        if (target < position)
        {
            source.Dispose();
            source = OpenDecompressed(path);
            position = 0;
        }
        while (position < target)
        {
            var read = source.Read(skip, 0, (int)Math.Min(skip.Length, target - position));
            if (read == 0) break;
            position += read;
        }
        return position;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) source.Dispose();
        base.Dispose(disposing);
    }
}

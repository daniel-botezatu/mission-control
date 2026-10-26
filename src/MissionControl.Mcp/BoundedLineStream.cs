namespace MissionControl.Mcp;

/// <summary>Caps one newline-delimited MCP frame before the SDK allocates its JSON text.</summary>
internal sealed class BoundedLineStream(Stream inner, int maximumFrameBytes = 262144) : Stream
{
    private int frameBytes;
    public bool LimitExceeded { get; private set; }
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var read = inner.Read(buffer, offset, count);
        Validate(buffer.AsSpan(offset, read));
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        // Windows console reads do not reliably honor cancellation; release the SDK wait on shutdown.
        var read = await inner.ReadAsync(buffer, cancellationToken).AsTask().WaitAsync(cancellationToken).ConfigureAwait(false);
        Validate(buffer.Span[..read]);
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    private void Validate(ReadOnlySpan<byte> bytes)
    {
        foreach (var value in bytes)
        {
            if (value == (byte)'\n') frameBytes = 0;
            else if (++frameBytes > maximumFrameBytes)
            {
                LimitExceeded = true;
                throw new IOException("MCP request exceeds the 256 KiB frame limit.");
            }
        }
    }

    protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

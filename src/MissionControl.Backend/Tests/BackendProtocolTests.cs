using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Xunit;

namespace MissionControl.Backend.Tests;

public sealed class BackendProtocolTests
{
    [Fact]
    public async Task Shutdown_cancels_work_and_disposes_before_acknowledging()
    {
        await using var harness = new Harness();
        await harness.Ready();
        harness.Request("work", "blocked");
        await harness.Application.WaitForActive(1);
        harness.Request("stop", "shutdown");
        var response = await harness.ReadUntil("stop");
        Assert.True(response.GetProperty("result").GetProperty("stopped").GetBoolean());
        Assert.True(harness.Application.Disposed);
        Assert.Equal(0, harness.Application.Active);
        Assert.Equal(0, await harness.Running.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Parent_pipe_eof_cancels_collection_and_disposes_without_an_orphan()
    {
        await using var harness = new Harness();
        await harness.Ready();
        harness.Request("work", "blocked");
        await harness.Application.WaitForActive(1);
        harness.Input.Complete();
        Assert.Equal(0, await harness.Running.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(harness.Application.Disposed);
        Assert.Equal(0, harness.Application.Active);
    }

    [Fact]
    public async Task Cancellation_still_dispatches_when_normal_request_slots_are_full()
    {
        await using var harness = new Harness();
        await harness.Ready();
        for (var index = 0; index < 14; index++) harness.Request("work-" + index, "blocked");
        await harness.Application.WaitForActive(14);
        harness.Request("busy", "blocked");
        Assert.Contains("busy", (await harness.ReadUntil("busy")).GetProperty("error").GetString());
        harness.Request("cancel", "monitoring.pause");
        Assert.True((await harness.ReadUntil("cancel")).GetProperty("result").GetProperty("ok").GetBoolean());
        Assert.True(harness.Application.MaximumActive <= 14);
        harness.Input.Complete();
        Assert.Equal(0, await harness.Running.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Input_is_bounded_by_utf8_bytes_before_json_parsing_and_fails_closed()
    {
        await using var harness = new Harness();
        await harness.Ready();
        harness.Input.Send(Encoding.UTF8.GetBytes(new string('é', BackendProtocol.MaximumInputBytes / 2 + 1) + "\n"));
        var fatal = await harness.ReadEvent("fatal");
        Assert.Contains("oversized", fatal.GetProperty("error").GetString());
        Assert.Equal(1, await harness.Running.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(harness.Application.Disposed);
    }

    [Fact]
    public void Oversized_unicode_reply_has_an_explicit_correlated_error()
    {
        var bytes = BackendProtocol.SerializeReply(new { id = "large", result = new string('界', BackendProtocol.MaximumReplyBytes / 3) });
        Assert.True(bytes.Length < 1024);
        using var result = JsonDocument.Parse(bytes);
        Assert.Equal("large", result.RootElement.GetProperty("id").GetString());
        Assert.Contains("transfer limit", result.RootElement.GetProperty("error").GetString());
        Assert.False(result.RootElement.TryGetProperty("result", out _));
    }

    [Fact]
    public async Task Exceptions_cannot_echo_a_known_credential_into_the_pipe()
    {
        await using var harness = new Harness();
        await harness.Ready();
        harness.Request("error", "secret-error");
        var response = await harness.ReadUntil("error");
        Assert.DoesNotContain("sensitive-value", response.GetProperty("error").GetString());
        Assert.Contains("REDACTED", response.GetProperty("error").GetString());
    }

    [Fact]
    public void Startup_command_keeps_the_previous_data_directory_and_quotes_shell_path()
    {
        var data = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "Mission Control fixture"));
        var command = DesktopStartup.BuildWindowsCommand(@"C:\Program Files\Mission Control\MissionControl.exe", data);
        Assert.StartsWith("\"C:\\Program Files\\Mission Control\\MissionControl.exe\" \"--background\" \"--data-dir\" ", command);
        Assert.True(DesktopStartup.OwnsArguments(["old-host.exe", "--background", "--data-dir", data], data, StringComparison.OrdinalIgnoreCase));
        Assert.False(DesktopStartup.OwnsArguments(["old-host.exe", "--background", "--data-dir", data + "other"], data, StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("{\"id\":1,\"method\":\"snapshot\"}")]
    [InlineData("{\"id\":\"x\",\"method\":\"snapshot\",\"args\":[]}")]
    [InlineData("{\"id\":\"\",\"method\":\"snapshot\"}")]
    public void Malformed_envelopes_are_rejected(string json) =>
        Assert.Throws<InvalidDataException>(() => BackendProtocol.Parse(Encoding.UTF8.GetBytes(json)));

    private sealed class Harness : IAsyncDisposable
    {
        internal FakeApplication Application { get; } = new();
        internal InputStream Input { get; } = new();
        private readonly OutputStream _output = new();
        internal Task<int> Running { get; }
        internal Harness() => Running = new BackendProtocol(Application, Input, _output).RunAsync();
        internal void Request(string id, string method) => Input.Send(JsonSerializer.SerializeToUtf8Bytes(new { id, method, args = new { } }).Concat(new byte[] { 10 }).ToArray());
        internal Task<JsonElement> Ready() => ReadEvent("ready");
        internal Task<JsonElement> ReadUntil(string id) => ReadMatching(value => value.TryGetProperty("id", out var item) && item.GetString() == id);
        internal Task<JsonElement> ReadEvent(string name) => ReadMatching(value => value.TryGetProperty("event", out var item) && item.GetString() == name);
        private async Task<JsonElement> ReadMatching(Func<JsonElement, bool> predicate)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (true)
            {
                var value = await _output.Messages.Reader.ReadAsync(timeout.Token);
                if (predicate(value)) return value;
            }
        }
        public async ValueTask DisposeAsync()
        {
            Input.Complete();
            await Running.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private sealed class FakeApplication : IBackendApplication
    {
        private readonly CancellationTokenSource _requests = new();
        internal int Active;
        internal int MaximumActive;
        internal bool Disposed;
        public event Action? Changed;
        public Task InitializeAsync() { Changed?.Invoke(); return Task.CompletedTask; }
        public async Task<object?> DispatchAsync(string method, JsonElement args)
        {
            if (method == "secret-error") throw new InvalidOperationException("APP_KEY=sensitive-value");
            if (method == "monitoring.pause") { _requests.Cancel(); return new { ok = true }; }
            var active = Interlocked.Increment(ref Active);
            InterlockedExtensions.Max(ref MaximumActive, active);
            try { await Task.Delay(Timeout.Infinite, _requests.Token); return null; }
            finally { Interlocked.Decrement(ref Active); }
        }
        public void RequestStop() => _requests.Cancel();
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
        internal async Task WaitForActive(int count)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (Volatile.Read(ref Active) != count) await Task.Delay(10, timeout.Token);
        }
    }

    private static class InterlockedExtensions
    {
        internal static void Max(ref int location, int value)
        {
            int current;
            do { current = Volatile.Read(ref location); if (current >= value) return; }
            while (Interlocked.CompareExchange(ref location, value, current) != current);
        }
    }

    private abstract class TestStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class InputStream : TestStream
    {
        private readonly Channel<byte[]> _chunks = Channel.CreateUnbounded<byte[]>();
        private byte[]? _current;
        private int _offset;
        internal void Send(byte[] value) => _chunks.Writer.TryWrite(value);
        internal void Complete() => _chunks.Writer.TryComplete();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_current is null)
            {
                if (!await _chunks.Reader.WaitToReadAsync(cancellationToken)) return 0;
                _current = await _chunks.Reader.ReadAsync(cancellationToken); _offset = 0;
            }
            var count = Math.Min(buffer.Length, _current.Length - _offset);
            _current.AsMemory(_offset, count).CopyTo(buffer); _offset += count;
            if (_offset == _current.Length) _current = null;
            return count;
        }
    }

    private sealed class OutputStream : TestStream
    {
        internal Channel<JsonElement> Messages { get; } = Channel.CreateUnbounded<JsonElement>();
        private readonly MemoryStream _line = new();
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            foreach (var value in buffer.Span)
            {
                if (value != 10) { _line.WriteByte(value); continue; }
                using var document = JsonDocument.Parse(_line.ToArray());
                Messages.Writer.TryWrite(document.RootElement.Clone()); _line.SetLength(0);
            }
            return ValueTask.CompletedTask;
        }
    }
}

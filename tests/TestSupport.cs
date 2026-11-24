using NSubstitute;
using SoundBoard.PluginApi;

// The plugin's namespace and class share the name "DiscordBridgePlugin";
// alias the concrete type so test code can name it unambiguously.
using Bridge = DiscordBridgePlugin.DiscordBridgePlugin;

namespace DiscordBridgePluginTests;

/// <summary>
/// Hand-written fake for the borrowed Opus encoder. NSubstitute can't fake
/// <see cref="IAudioFrameEncoder.Encode"/> cleanly because its parameters
/// are <c>ref struct</c> spans, so we roll our own. Counts calls, records
/// the PCM length it was handed per call, and writes a deterministic
/// fixed-size packet so the test can assert how many bytes reached the
/// outbound stream.
/// </summary>
internal sealed class FakeFrameEncoder : IAudioFrameEncoder
{
    public int FrameSamples { get; init; } = 960;
    public int Channels { get; init; } = 2;
    public int SampleRate { get; init; } = 48000;

    /// <summary>Bytes this fake claims to write per encoded frame.</summary>
    public int PacketBytes { get; init; } = 8;

    public int EncodeCallCount { get; private set; }
    public List<int> ReceivedPcmLengths { get; } = new();
    public bool Disposed { get; private set; }

    public int Encode(ReadOnlySpan<float> pcm, Span<byte> packet)
    {
        EncodeCallCount++;
        ReceivedPcmLengths.Add(pcm.Length);
        for (int i = 0; i < PacketBytes; i++) packet[i] = (byte)i;
        return PacketBytes;
    }

    public void Dispose() => Disposed = true;
}

/// <summary>
/// Fake outbound stream whose <see cref="WriteAsync"/> blocks until its
/// cancellation token fires — reproduces Discord.Net's
/// <c>BufferedWriteStream</c> deadlock when its internal background loop
/// crashes silently and stops releasing <c>_queueLock</c>.
/// </summary>
internal sealed class HangingWriteStream : Stream
{
    public int WriteAttempts { get; private set; }
    public bool Disposed { get; private set; }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => 0;
    public override long Position { get => 0; set => throw new NotSupportedException(); }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        WriteAttempts++;
        return Task.Delay(Timeout.Infinite, cancellationToken);
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        WriteAttempts++;
        return new ValueTask(Task.Delay(Timeout.Infinite, cancellationToken));
    }

    public override Task FlushAsync(CancellationToken cancellationToken)
        => Task.Delay(Timeout.Infinite, cancellationToken);

    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        Disposed = true;
        base.Dispose(disposing);
    }
}

/// <summary>
/// Fake outbound stream whose <see cref="WriteAsync"/> throws an
/// <see cref="OperationCanceledException"/> from an internal token (NOT the
/// caller's token) — reproduces Discord.Net's <c>BufferedWriteStream</c>
/// after <c>AudioClient._connection.CancelToken</c> is cancelled on a voice
/// WebSocket drop/resume.
/// </summary>
internal sealed class ExternallyCancelledStream : Stream
{
    private readonly CancellationTokenSource _internalCts = new();

    public ExternallyCancelledStream()
    {
        _internalCts.Cancel();
    }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => 0;
    public override long Position { get => 0; set => throw new NotSupportedException(); }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => Task.FromCanceled(_internalCts.Token);

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        => ValueTask.FromCanceled(_internalCts.Token);

    public override void Flush() { }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        _internalCts.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>
/// Fake outbound stream that throws a configurable number of transient
/// <see cref="IOException"/>s before delegating writes to an inner
/// <see cref="MemoryStream"/> (or failing forever if <see cref="FailuresRemaining"/>
/// is set high).
/// </summary>
internal sealed class FlakyWriteStream : Stream
{
    private readonly MemoryStream _inner = new();
    public int FailuresRemaining { get; set; }
    public long WrittenBytes => _inner.Length;

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => _inner.Length;
    public override long Position { get => _inner.Position; set => _inner.Position = value; }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        if (FailuresRemaining > 0)
        {
            FailuresRemaining--;
            return Task.FromException(new IOException("Simulated UDP send failure"));
        }
        return _inner.WriteAsync(buffer, offset, count, cancellationToken);
    }

    public override void Flush() => _inner.Flush();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => _inner.SetLength(value);
    public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);

    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>Shared builders for the unit tests.</summary>
internal static class TestSupport
{
    public static string NewTempDir()
    {
        var dir = Path.Combine(
            Path.GetTempPath(), "gmsb-bridge-discord-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>An <see cref="IPluginContext"/> with a real, writable
    /// PluginDataPath and (by default) no codec registry.</summary>
    public static IPluginContext ContextWithDataPath(string dir)
    {
        var ctx = Substitute.For<IPluginContext>();
        ctx.PluginDataPath.Returns(dir);
        return ctx;
    }

    /// <summary>A bridge initialized against a throwaway data dir, with no
    /// codec.opus available (CodecRegistry is null).</summary>
    public static Bridge NewInitialized()
    {
        var plugin = new Bridge();
        plugin.Initialize(ContextWithDataPath(NewTempDir()));
        return plugin;
    }

    /// <summary>A bridge initialized with a codec registry whose
    /// <c>.opus</c> lookup returns <paramref name="codec"/>.</summary>
    public static Bridge NewInitializedWithCodec(IAudioCodecPlugin codec)
    {
        var ctx = ContextWithDataPath(NewTempDir());
        var registry = Substitute.For<IAudioCodecRegistry>();
        registry.GetByExtension(".opus").Returns(codec);
        ctx.CodecRegistry.Returns(registry);

        var plugin = new Bridge();
        plugin.Initialize(ctx);
        return plugin;
    }
}

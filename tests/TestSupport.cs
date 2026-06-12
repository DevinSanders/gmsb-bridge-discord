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

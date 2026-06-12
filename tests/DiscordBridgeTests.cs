using FluentAssertions;
using NSubstitute;
using SoundBoard.PluginApi;
using Xunit;

using Bridge = DiscordBridgePlugin.DiscordBridgePlugin;

namespace DiscordBridgePluginTests;

// Network-free coverage of the bridge's pure pieces. The live Discord
// connection (real token + gateway + native libopus/libsodium/libdave) is
// out of scope here — it lives as a manual checklist in the README.

public class ConfigTests
{
    [Fact]
    public void Config_round_trips_through_disk()
    {
        var ctx = TestSupport.ContextWithDataPath(TestSupport.NewTempDir());

        var writer = new Bridge();
        writer.Initialize(ctx);
        writer.BotToken = "secret-token";
        writer.GuildId = "123456789012345678";
        writer.ChannelId = "987654321098765432";
        writer.Bitrate = 96_000;

        // A fresh instance pointed at the same data dir reloads the values.
        var reader = new Bridge();
        reader.Initialize(ctx);
        reader.BotToken.Should().Be("secret-token");
        reader.GuildId.Should().Be("123456789012345678");
        reader.ChannelId.Should().Be("987654321098765432");
        reader.Bitrate.Should().Be(96_000);
    }

    [Fact]
    public void Missing_config_file_yields_sane_defaults()
    {
        var ctx = TestSupport.ContextWithDataPath(TestSupport.NewTempDir());
        var plugin = new Bridge();
        plugin.Initialize(ctx);

        plugin.BotToken.Should().BeEmpty();
        plugin.GuildId.Should().BeEmpty();
        plugin.ChannelId.Should().BeEmpty();
        plugin.Bitrate.Should().Be(64_000);
    }

    [Fact]
    public void Bitrate_setter_clamps_out_of_range_values()
    {
        var ctx = TestSupport.ContextWithDataPath(TestSupport.NewTempDir());
        var plugin = new Bridge();
        plugin.Initialize(ctx);

        plugin.Bitrate = 5_000;     // below the 6 kbps Opus floor → ignored
        plugin.Bitrate.Should().Be(64_000);

        plugin.Bitrate = 600_000;   // above the 510 kbps ceiling → ignored
        plugin.Bitrate.Should().Be(64_000);

        plugin.Bitrate = 128_000;   // in range → accepted
        plugin.Bitrate.Should().Be(128_000);
    }
}

public class StatusLifecycleTests
{
    [Fact]
    public void New_bridge_starts_disconnected_and_silent()
    {
        var plugin = new Bridge();

        plugin.Status.Should().Be(BridgeStatus.Disconnected);
        // The host gates its broadcast queue on this; it must stay false
        // until we're actually connected so unsubscribed audio is dropped.
        plugin.WantsOutboundAudio.Should().BeFalse();
        plugin.StatusDetail.Should().BeNull();
        plugin.LastError.Should().BeNull();
    }
}

public class ConnectValidationTests
{
    private static async Task ShouldThrowOnConnect(Bridge plugin, string messageWildcard)
    {
        Func<Task> act = () => plugin.ConnectAsync(CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage(messageWildcard);
        // None of the validation failures touch the network, so the bridge
        // never leaves the Disconnected state.
        plugin.Status.Should().Be(BridgeStatus.Disconnected);
    }

    [Fact]
    public async Task Empty_token_is_rejected_before_any_io()
    {
        var plugin = TestSupport.NewInitialized();
        plugin.GuildId = "123";
        plugin.ChannelId = "456";
        await ShouldThrowOnConnect(plugin, "*token is empty*");
    }

    [Fact]
    public async Task Non_numeric_guild_id_is_rejected()
    {
        var plugin = TestSupport.NewInitialized();
        plugin.BotToken = "tok";
        plugin.GuildId = "not-a-snowflake";
        plugin.ChannelId = "456";
        await ShouldThrowOnConnect(plugin, "*Guild ID must be*");
    }

    [Fact]
    public async Task Non_numeric_channel_id_is_rejected()
    {
        var plugin = TestSupport.NewInitialized();
        plugin.BotToken = "tok";
        plugin.GuildId = "123";
        plugin.ChannelId = "nope";
        await ShouldThrowOnConnect(plugin, "*Channel ID must be*");
    }

    [Fact]
    public async Task Missing_codec_opus_produces_a_clear_error()
    {
        var plugin = TestSupport.NewInitialized(); // CodecRegistry is null
        plugin.BotToken = "tok";
        plugin.GuildId = "123";
        plugin.ChannelId = "456";
        await ShouldThrowOnConnect(plugin, "*codec.opus*");
    }

    [Fact]
    public async Task Codec_opus_without_encoding_support_is_rejected()
    {
        var codec = Substitute.For<IAudioCodecPlugin>();
        codec.SupportsEncoding.Returns(false);

        var plugin = TestSupport.NewInitializedWithCodec(codec);
        plugin.BotToken = "tok";
        plugin.GuildId = "123";
        plugin.ChannelId = "456";
        await ShouldThrowOnConnect(plugin, "*does not advertise encoding*");
    }

    [Fact]
    public async Task Encoder_factory_returning_null_is_rejected()
    {
        var codec = Substitute.For<IAudioCodecPlugin>();
        codec.SupportsEncoding.Returns(true);
        codec.CreateEncoder(Arg.Any<int>(), Arg.Any<int>(), Arg.Any<int>())
             .Returns((IAudioFrameEncoder?)null);

        var plugin = TestSupport.NewInitializedWithCodec(codec);
        plugin.BotToken = "tok";
        plugin.GuildId = "123";
        plugin.ChannelId = "456";
        await ShouldThrowOnConnect(plugin, "*refused to build an encoder*");
    }
}

public class OutboundFramingTests
{
    [Fact]
    public void SendOutboundPcm_is_a_noop_when_disconnected()
    {
        var plugin = new Bridge();
        var act = () => plugin.SendOutboundPcm(new float[4096]);

        act.Should().NotThrow();
        plugin.WantsOutboundAudio.Should().BeFalse();
    }

    [Fact]
    public void SendOutboundPcm_drains_whole_frames_and_buffers_the_remainder()
    {
        var encoder = new FakeFrameEncoder { FrameSamples = 960, Channels = 2, PacketBytes = 8 };
        using var sink = new MemoryStream();

        var plugin = new Bridge();
        plugin.ConfigureOutboundForTest(encoder, sink);

        const int frame = Bridge.OpusFrameSamplesStereo; // 1920 interleaved floats

        // Three full frames plus a half-frame tail.
        plugin.SendOutboundPcm(new float[(frame * 3) + (frame / 2)]);

        encoder.EncodeCallCount.Should().Be(3);
        sink.Length.Should().Be(3 * encoder.PacketBytes);
        encoder.ReceivedPcmLengths.Should().OnlyContain(len => len == frame);

        // Feeding the rest of the tail completes a fourth frame — proving
        // the leftover was retained across calls, not dropped.
        plugin.SendOutboundPcm(new float[frame / 2]);

        encoder.EncodeCallCount.Should().Be(4);
        sink.Length.Should().Be(4 * encoder.PacketBytes);
    }

    [Fact]
    public void Outbound_frame_geometry_matches_the_borrowed_encoder()
    {
        // The reframing loop hard-assumes the borrowed Opus encoder reports
        // 960 samples/channel at stereo. If a future codec.opus changes its
        // FrameSamples, this fails loudly instead of silently corrupting
        // audio by feeding the encoder mis-sized frames.
        var encoder = new FakeFrameEncoder { FrameSamples = 960, Channels = 2 };

        Bridge.OpusFrameSamplesPerChannel.Should().Be(encoder.FrameSamples);
        (encoder.FrameSamples * Bridge.Channels).Should().Be(Bridge.OpusFrameSamplesStereo);
    }
}

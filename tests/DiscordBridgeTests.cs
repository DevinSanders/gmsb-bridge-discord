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

public class SelfHealingTests
{
    private static Bridge NewFastRecoveryBridge()
    {
        var plugin = new Bridge
        {
            WriteTimeout = TimeSpan.FromMilliseconds(50),
            FlushTimeout = TimeSpan.FromMilliseconds(50),
            RebuildEscalationWindow = TimeSpan.FromSeconds(10),
            GatewayWaitTimeout = TimeSpan.FromSeconds(2),
            ReconnectBackoffs = new[]
            {
                TimeSpan.FromMilliseconds(10),
                TimeSpan.FromMilliseconds(10),
                TimeSpan.FromMilliseconds(10),
            },
        };
        return plugin;
    }

    [Fact]
    public async Task Write_timeout_does_not_deadlock_and_rebuilds_stream_in_place()
    {
        var encoder = new FakeFrameEncoder { FrameSamples = 960, Channels = 2, PacketBytes = 12 };
        using var deadStream = new HangingWriteStream();
        using var healthyStream = new MemoryStream();

        int rebuildCalls = 0;
        int rejoinCalls = 0;

        var plugin = NewFastRecoveryBridge();
        plugin.ConfigureRecoveryForTest(
            rebuildStreamAsync: _ =>
            {
                rebuildCalls++;
                return Task.FromResult<Stream?>(healthyStream);
            },
            rejoinVoiceAsync: _ =>
            {
                rejoinCalls++;
                return Task.FromResult<Stream>(healthyStream);
            });
        plugin.ConfigureOutboundForTest(encoder, deadStream);

        // Sending one frame hits the hanging stream, times out after ~50ms
        // instead of deadlocking forever, and triggers Level 1 in-place rebuild.
        plugin.SendOutboundPcm(new float[Bridge.OpusFrameSamplesStereo]);
        await plugin.WaitForRecoveryIdleForTestAsync();

        rebuildCalls.Should().Be(1);
        rejoinCalls.Should().Be(0);
        plugin.Status.Should().Be(BridgeStatus.Connected);
        plugin.WantsOutboundAudio.Should().BeTrue();

        // Audio now flows into the replacement stream.
        plugin.SendOutboundPcm(new float[Bridge.OpusFrameSamplesStereo * 2]);
        healthyStream.Length.Should().Be(2 * encoder.PacketBytes);
    }

    [Fact]
    public async Task Repeated_stream_stalls_escalate_from_rebuild_to_full_voice_rejoin()
    {
        var encoder = new FakeFrameEncoder { FrameSamples = 960, Channels = 2, PacketBytes = 10 };
        using var firstDead = new HangingWriteStream();
        using var secondDead = new HangingWriteStream();
        using var healedStream = new MemoryStream();

        int rebuildCalls = 0;
        int rejoinCalls = 0;

        var plugin = NewFastRecoveryBridge();
        plugin.ConfigureRecoveryForTest(
            rebuildStreamAsync: _ =>
            {
                rebuildCalls++;
                // Return a stream that also stalls (e.g. wedged DAVE session).
                return Task.FromResult<Stream?>(secondDead);
            },
            rejoinVoiceAsync: _ =>
            {
                rejoinCalls++;
                return Task.FromResult<Stream>(healedStream);
            });
        plugin.ConfigureOutboundForTest(encoder, firstDead);

        // First stall → Level 1 in-place stream rebuild.
        plugin.SendOutboundPcm(new float[Bridge.OpusFrameSamplesStereo]);
        await plugin.WaitForRecoveryIdleForTestAsync();
        rebuildCalls.Should().Be(1);
        rejoinCalls.Should().Be(0);

        // Second stall within RebuildEscalationWindow → escalates to Level 2 full voice rejoin.
        plugin.SendOutboundPcm(new float[Bridge.OpusFrameSamplesStereo]);
        await plugin.WaitForRecoveryIdleForTestAsync();
        rebuildCalls.Should().Be(1);
        rejoinCalls.Should().Be(1);
        plugin.Status.Should().Be(BridgeStatus.Connected);
        plugin.WantsOutboundAudio.Should().BeTrue();

        // Subsequent frames stream cleanly to the rejoined voice stream.
        plugin.SendOutboundPcm(new float[Bridge.OpusFrameSamplesStereo]);
        healedStream.Length.Should().Be(encoder.PacketBytes);
    }

    [Fact]
    public async Task External_stream_cancellation_escalates_directly_to_voice_rejoin()
    {
        var encoder = new FakeFrameEncoder { FrameSamples = 960, Channels = 2, PacketBytes = 8 };
        using var cancelledStream = new ExternallyCancelledStream();
        using var healedStream = new MemoryStream();

        int rebuildCalls = 0;
        int rejoinCalls = 0;

        var plugin = NewFastRecoveryBridge();
        plugin.ConfigureRecoveryForTest(
            rebuildStreamAsync: _ =>
            {
                rebuildCalls++;
                return Task.FromResult<Stream?>(healedStream);
            },
            rejoinVoiceAsync: _ =>
            {
                rejoinCalls++;
                return Task.FromResult<Stream>(healedStream);
            });
        plugin.ConfigureOutboundForTest(encoder, cancelledStream);

        // Discord.Net's _connection.CancelToken cancelled the stream — must NOT
        // silently swallow the OCE or trust Discord.Net's broken voice Resume.
        plugin.SendOutboundPcm(new float[Bridge.OpusFrameSamplesStereo]);
        await plugin.WaitForRecoveryIdleForTestAsync();

        rebuildCalls.Should().Be(0);
        rejoinCalls.Should().Be(1);
        plugin.Status.Should().Be(BridgeStatus.Connected);
        plugin.WantsOutboundAudio.Should().BeTrue();

        plugin.SendOutboundPcm(new float[Bridge.OpusFrameSamplesStereo]);
        healedStream.Length.Should().Be(encoder.PacketBytes);
    }

    [Fact]
    public async Task Audio_client_disconnect_event_rejoins_and_restores_audio()
    {
        var encoder = new FakeFrameEncoder { FrameSamples = 960, Channels = 2, PacketBytes = 8 };
        using var initialStream = new MemoryStream();
        using var rejoinedStream = new MemoryStream();

        var observedStatuses = new List<BridgeStatus>();
        int rejoinCalls = 0;

        var plugin = NewFastRecoveryBridge();
        plugin.StatusChanged += (_, _) => observedStatuses.Add(plugin.Status);
        plugin.ConfigureRecoveryForTest(
            rejoinVoiceAsync: _ =>
            {
                rejoinCalls++;
                return Task.FromResult<Stream>(rejoinedStream);
            });
        plugin.ConfigureOutboundForTest(encoder, initialStream);
        observedStatuses.Clear();

        plugin.SimulateAudioClientDisconnectedForTest(new IOException("Voice WebSocket closed 4006"));
        await plugin.WaitForRecoveryIdleForTestAsync();

        rejoinCalls.Should().Be(1);
        observedStatuses.Should().Contain(BridgeStatus.Connecting);
        plugin.Status.Should().Be(BridgeStatus.Connected);
        plugin.WantsOutboundAudio.Should().BeTrue();

        plugin.SendOutboundPcm(new float[Bridge.OpusFrameSamplesStereo]);
        rejoinedStream.Length.Should().Be(encoder.PacketBytes);
    }

    [Fact]
    public async Task Rejoin_retries_transient_failures_and_heals_when_voice_returns()
    {
        var encoder = new FakeFrameEncoder { FrameSamples = 960, Channels = 2, PacketBytes = 8 };
        using var initialStream = new MemoryStream();
        using var healedStream = new MemoryStream();

        int attempts = 0;
        var plugin = NewFastRecoveryBridge();
        plugin.ConfigureRecoveryForTest(
            rejoinVoiceAsync: _ =>
            {
                attempts++;
                if (attempts < 3) throw new TimeoutException("Voice handshake timed out");
                return Task.FromResult<Stream>(healedStream);
            });
        plugin.ConfigureOutboundForTest(encoder, initialStream);

        plugin.SimulateAudioClientDisconnectedForTest(new IOException("Network hiccup"));
        await plugin.WaitForRecoveryIdleForTestAsync();

        attempts.Should().Be(3);
        plugin.Status.Should().Be(BridgeStatus.Connected);
        plugin.WantsOutboundAudio.Should().BeTrue();
        plugin.LastError.Should().BeNull();
    }

    [Fact]
    public async Task Exhausted_rejoin_attempts_transition_honestly_to_failed()
    {
        var encoder = new FakeFrameEncoder { FrameSamples = 960, Channels = 2, PacketBytes = 8 };
        using var initialStream = new MemoryStream();

        int attempts = 0;
        var plugin = NewFastRecoveryBridge();
        plugin.ConfigureRecoveryForTest(
            rejoinVoiceAsync: _ =>
            {
                attempts++;
                throw new InvalidOperationException("No permission to connect");
            });
        plugin.ConfigureOutboundForTest(encoder, initialStream);

        plugin.SimulateAudioClientDisconnectedForTest(new IOException("Kicked"));
        await plugin.WaitForRecoveryIdleForTestAsync();

        attempts.Should().Be(3);
        plugin.Status.Should().Be(BridgeStatus.Failed);
        plugin.WantsOutboundAudio.Should().BeFalse();
        plugin.LastError.Should().BeOfType<InvalidOperationException>();
        plugin.StatusDetail.Should().Contain("No permission to connect");
    }

    [Fact]
    public async Task Watchdog_detects_silent_unhealthy_voice_and_triggers_rejoin()
    {
        var encoder = new FakeFrameEncoder { FrameSamples = 960, Channels = 2, PacketBytes = 8 };
        using var initialStream = new MemoryStream();
        using var healedStream = new MemoryStream();

        bool voiceHealthy = false;
        int rejoinCalls = 0;

        var plugin = NewFastRecoveryBridge();
        plugin.ConfigureRecoveryForTest(
            rejoinVoiceAsync: _ =>
            {
                rejoinCalls++;
                voiceHealthy = true;
                return Task.FromResult<Stream>(healedStream);
            },
            checkVoiceHealth: () => voiceHealthy
                ? (true, null)
                : (false, "No voice heartbeat ACK for over 90s"));
        plugin.ConfigureOutboundForTest(encoder, initialStream);

        plugin.EvaluateVoiceHealthOnce();
        await plugin.WaitForRecoveryIdleForTestAsync();

        rejoinCalls.Should().Be(1);
        plugin.Status.Should().Be(BridgeStatus.Connected);
        plugin.WantsOutboundAudio.Should().BeTrue();

        // Subsequent check sees healthy voice and does not rejoin again.
        plugin.EvaluateVoiceHealthOnce();
        await plugin.WaitForRecoveryIdleForTestAsync();
        rejoinCalls.Should().Be(1);
    }

    [Fact]
    public async Task Gateway_disconnect_pauses_outbound_and_rejoins_when_gateway_returns()
    {
        var encoder = new FakeFrameEncoder { FrameSamples = 960, Channels = 2, PacketBytes = 8 };
        using var initialStream = new MemoryStream();
        using var healedStream = new MemoryStream();

        int rejoinCalls = 0;
        var plugin = NewFastRecoveryBridge();
        plugin.ConfigureRecoveryForTest(
            rejoinVoiceAsync: _ =>
            {
                rejoinCalls++;
                return Task.FromResult<Stream>(healedStream);
            });
        plugin.ConfigureOutboundForTest(encoder, initialStream);

        await plugin.SimulateGatewayDisconnectedForTest(new IOException("Gateway dropped"));
        plugin.WantsOutboundAudio.Should().BeFalse();
        plugin.Status.Should().Be(BridgeStatus.Connecting);

        // Rejoin should wait until gateway actually reconnects.
        await Task.Delay(40, TestContext.Current.CancellationToken);
        rejoinCalls.Should().Be(0);

        await plugin.SimulateGatewayConnectedForTest();
        await plugin.WaitForRecoveryIdleForTestAsync();

        rejoinCalls.Should().Be(1);
        plugin.Status.Should().Be(BridgeStatus.Connected);
        plugin.WantsOutboundAudio.Should().BeTrue();
    }

    [Fact]
    public async Task Transient_single_write_error_drops_frame_without_rebuilding_but_consecutive_errors_trigger_recovery()
    {
        var encoder = new FakeFrameEncoder { FrameSamples = 960, Channels = 2, PacketBytes = 8 };
        using var flakyStream = new FlakyWriteStream { FailuresRemaining = 1 };
        using var healedStream = new MemoryStream();

        int rebuildCalls = 0;
        var plugin = NewFastRecoveryBridge();
        plugin.ConfigureRecoveryForTest(
            rebuildStreamAsync: _ =>
            {
                rebuildCalls++;
                return Task.FromResult<Stream?>(healedStream);
            });
        plugin.ConfigureOutboundForTest(encoder, flakyStream);

        // One transient error followed by a successful frame → stays on same stream.
        plugin.SendOutboundPcm(new float[Bridge.OpusFrameSamplesStereo * 2]);
        await plugin.WaitForRecoveryIdleForTestAsync();
        rebuildCalls.Should().Be(0);
        flakyStream.WrittenBytes.Should().Be(encoder.PacketBytes);

        // Three consecutive write errors → triggers automatic stream rebuild.
        flakyStream.FailuresRemaining = 10;
        plugin.SendOutboundPcm(new float[Bridge.OpusFrameSamplesStereo * 3]);
        await plugin.WaitForRecoveryIdleForTestAsync();

        rebuildCalls.Should().Be(1);
        plugin.Status.Should().Be(BridgeStatus.Connected);
        plugin.SendOutboundPcm(new float[Bridge.OpusFrameSamplesStereo]);
        healedStream.Length.Should().Be(encoder.PacketBytes);
    }

    [Fact]
    public async Task DisconnectAsync_completes_quickly_even_when_stream_flush_hangs()
    {
        var encoder = new FakeFrameEncoder { FrameSamples = 960, Channels = 2, PacketBytes = 8 };
        using var hungStream = new HangingWriteStream();

        var plugin = NewFastRecoveryBridge();
        plugin.ConfigureOutboundForTest(encoder, hungStream);

        // FlushAsync on hungStream blocks forever unless bounded by FlushTimeout.
        var disconnectTask = plugin.DisconnectAsync();
        var completed = await Task.WhenAny(
            disconnectTask,
            Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));

        completed.Should().Be(disconnectTask);
        await disconnectTask;
        plugin.Status.Should().Be(BridgeStatus.Disconnected);
        plugin.WantsOutboundAudio.Should().BeFalse();
        hungStream.Disposed.Should().BeTrue();
    }
}

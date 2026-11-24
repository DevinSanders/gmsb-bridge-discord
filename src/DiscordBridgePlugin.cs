using Discord;
using Discord.Audio;
using Discord.WebSocket;
using SoundBoard.PluginApi;
using System;
using System.Buffers;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace DiscordBridgePlugin;

/// <summary>
/// <see cref="IAudioBridgePlugin"/> that streams the host's master mix
/// into a Discord voice channel. Pre-Phase-3 this lived in
/// <c>SoundBoard.Core</c> as a baked-in service; now it's a separate
/// plugin so users who don't run a bot don't pay for ~25 MB of
/// Discord.Net + libdave native binaries.
///
/// <para><b>Opus is borrowed.</b> Discord voice requires Opus-encoded
/// audio. Rather than bundling Concentus a second time, this plugin
/// asks the host's <see cref="IPluginContext.CodecRegistry"/> for
/// <c>codec.opus</c> at connect time and uses its
/// <see cref="IAudioCodecPlugin.CreateEncoder"/>. If codec.opus isn't
/// installed, the bridge refuses to connect with a clear error.</para>
///
/// <para><b>DAVE.</b> Discord's end-to-end voice encryption is rolling
/// out and will be mandatory by 2026-03-01. The Discord.Net.Dave +
/// libdave packages are referenced; <c>EnableVoiceDaveEncryption</c> is
/// set on the socket config below. Backward-compatible — channels that
/// don't require DAVE still accept the connection.</para>
///
/// <para><b>Self-healing audio stream.</b> Discord.Net's
/// <c>CreateOpusStream()</c> returns a <c>BufferedWriteStream</c> whose
/// internal loop can die silently (for example when a DAVE encryption
/// transition leaves <c>RTPWriteStream</c> with a dangling header or a
/// transient UDP error faults its background task, deadlocking
/// <c>WriteAsync</c>) or get permanently cancelled whenever the voice
/// WebSocket blips or migrates. Furthermore, Discord.Net 3.20.x's voice
/// resume path recreates <c>DaveSessionManager</c> without re-running
/// the DAVE handshake on <c>Resumed</c>, leaving resumed sessions unable
/// to transmit DAVE-encrypted audio. To recover automatically without
/// manual intervention, this bridge enforces a per-write timeout,
/// monitors voice/gateway events and heartbeat freshness, rebuilds the
/// Opus stream in place on transient stream stalls, and escalates to a
/// clean voice channel rejoin with exponential backoff whenever the
/// voice transport drops or stream rebuilds fail repeatedly.</para>
/// </summary>
public sealed class DiscordBridgePlugin : IAudioBridgePlugin
{
    private const int ReadyTimeoutSeconds = 15;
    private const int VoiceConnectTimeoutSeconds = 20;
    private const int ConsecutiveWriteFailureThreshold = 3;
    private const int MaxInPlaceRebuildsInWindow = 1;

    private static readonly TimeSpan[] DefaultReconnectBackoffs =
    {
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(4),
        TimeSpan.FromSeconds(8),
        TimeSpan.FromSeconds(15),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(30),
    };

    public string Id => "bridge.discord";
    public string Name => "Discord Bridge";
    public string Description => "Streams the master mix into a Discord voice channel. Requires codec.opus.";
    public string Version => PluginVersion.OfAssembly(typeof(DiscordBridgePlugin));
    public string Author => "Devin Sanders";

    private DiscordSocketClient? _client;
    private IAudioClient? _audioClient;
    private IAudioFrameEncoder? _opusEncoder;
    private TaskCompletionSource? _readyTcs;
    private TaskCompletionSource? _gatewayConnectedTcs;

    // Atomic handle to the active outbound Opus stream and its per-stream
    // cancellation token. Replaced via Interlocked.Exchange during connect,
    // stream rebuild, voice rejoin, and disconnect so SendOutboundPcm never
    // races against a disposed stream or CTS.
    private OutboundStreamState? _outboundState;
    private int _streamGeneration;

    // Session lifetime CTS: created on ConnectAsync, cancelled ONLY when the
    // user explicitly calls DisconnectAsync() (or Shutdown). Distinguishes
    // intentional disconnect from transient stream/voice/gateway failures.
    private CancellationTokenSource? _sessionCts;
    private Task? _watchdogTask;

    // Single-flight recovery coordinator. Prevents concurrent voice rejoins
    // while preserving escalation requests (e.g., a voice disconnect arriving
    // while an in-place stream rebuild is running).
    private readonly object _recoverySync = new();
    private Task? _recoveryTask;
    private bool _recoveryRunning;
    private bool _pendingRecovery;
    private bool _pendingForceRejoin;
    private string? _pendingRecoveryReason;
    private Exception? _pendingRecoveryError;

    // Tracks in-place rebuild frequency so repeated stream stalls escalate
    // to a full voice rejoin (fresh AudioClient + DAVE session + UDP socket).
    private long _lastInPlaceRebuildTicks;
    private int _recentInPlaceRebuilds;

    // Voice health tracking (Stopwatch ticks are monotonic and immune to
    // Environment.TickCount's 24.85-day signed 32-bit wrap).
    private long _lastVoiceHeartbeatTicks;
    private long _lastVoiceConnectedTicks;
    private int _consecutiveWriteErrors;
    private int _gatewayConnected;
    private int _sessionEstablished;

    // Active target captured on connect so automatic rejoin targets the same
    // guild/channel even if config properties are edited later.
    private ulong _activeGuildId;
    private ulong _activeChannelId;
    private string _activeGuildName = "";
    private string _activeChannelName = "";

    // Continuous PCM accumulator. Mixer chunks arrive at ~10 ms; Opus
    // wants 20 ms frames. Re-frame in the worker thread before encoding.
    private readonly object _accumLock = new();
    private float[] _accumBuffer = new float[OpusFrameSamplesStereo * 4];
    private int _accumLength;

    // Outbound framing geometry. The bridge always runs stereo; Opus at
    // 48 kHz uses 960 samples/channel for a 20 ms frame, so a frame is
    // 960 * 2 = 1920 interleaved floats. These are internal (not private)
    // so the test suite can guard the coupling to the borrowed encoder's
    // FrameSamples — a future encoder reporting a different frame size
    // must fail a test loudly instead of silently corrupting audio.
    internal const int Channels = 2;
    internal const int OpusFrameSamplesPerChannel = 960;
    internal const int OpusFrameSamplesStereo = OpusFrameSamplesPerChannel * Channels;

    // Per-Opus-packet upper bound. Concentus + Discord agree 4000 bytes
    // is the safe max for any single Opus frame.
    private const int MaxOpusPacketBytes = 4000;

    // Tunable timings (internal so tests can run deterministically in ms
    // instead of sleeping for real-world seconds).
    internal TimeSpan WriteTimeout { get; set; } = TimeSpan.FromSeconds(2);
    internal TimeSpan FlushTimeout { get; set; } = TimeSpan.FromMilliseconds(500);
    internal TimeSpan WatchdogInterval { get; set; } = TimeSpan.FromSeconds(10);
    internal TimeSpan HeartbeatStaleThreshold { get; set; } = TimeSpan.FromSeconds(90);
    internal TimeSpan VoiceStateGracePeriod { get; set; } = TimeSpan.FromSeconds(5);
    internal TimeSpan RebuildEscalationWindow { get; set; } = TimeSpan.FromSeconds(30);
    internal TimeSpan GatewayWaitTimeout { get; set; } = TimeSpan.FromSeconds(20);
    internal TimeSpan[] ReconnectBackoffs { get; set; } = DefaultReconnectBackoffs;

    // Optional test hooks overriding Discord.Net voice transport operations.
    // Production leaves all three null and runs the live Discord.Net paths.
    private Func<CancellationToken, Task<Stream?>>? _testRebuildStreamAsync;
    private Func<CancellationToken, Task<Stream>>? _testRejoinVoiceAsync;
    private Func<(bool Healthy, string? Reason)>? _testCheckVoiceHealth;

    private IPluginContext? _context;
    private DiscordBridgeConfig _config = new();
    private string? _configPath;

    // ── IAudioBridgePlugin status surface ─────────────────────────────

    private BridgeStatus _status = BridgeStatus.Disconnected;
    private string? _statusDetail;
    private Exception? _lastError;
    private int _wantsOutboundAudio;

    public BridgeStatus Status => _status;
    public string? StatusDetail => _statusDetail;
    public Exception? LastError => _lastError;
    public bool WantsOutboundAudio => Volatile.Read(ref _wantsOutboundAudio) != 0;

    public event EventHandler? StatusChanged;

    public void Initialize(IPluginContext context)
    {
        _context = context;
        _configPath = Path.Combine(context.PluginDataPath, "config.json");
        LoadConfigFromDisk();
    }

    public void Shutdown()
    {
        // Defensive: the host's AudioBridgeHost.DisconnectAllBridges runs
        // before this and is the primary "leave Discord cleanly" path.
        // But IPlugin.Shutdown is the documented final-cleanup hook, so
        // we re-disconnect here in case some other host (a test rig, a
        // future headless variant) skipped the bridge-host path. Bounded
        // wait so a hung Discord.Net call can't freeze plugin teardown.
        try
        {
            var task = DisconnectAsync();
            if (!task.Wait(TimeSpan.FromSeconds(3)))
            {
                // No log channel from Shutdown — host wires Console.WriteLine
                // through its log mirror, so this still lands in gmsound.log.
                Console.WriteLine("[bridge.discord] Shutdown: DisconnectAsync did not complete within 3s — abandoning.");
            }
        }
        catch
        {
            /* shutdown best-effort — host's logger may already be torn down */
        }
    }

    // ── Public config (read by the settings UI) ───────────────────────

    public string BotToken
    {
        get => _config.BotToken;
        set { _config.BotToken = value; SaveConfigToDisk(); }
    }

    public string GuildId
    {
        get => _config.GuildId;
        set { _config.GuildId = value; SaveConfigToDisk(); }
    }

    public string ChannelId
    {
        get => _config.ChannelId;
        set { _config.ChannelId = value; SaveConfigToDisk(); }
    }

    public int Bitrate
    {
        get => _config.Bitrate;
        set
        {
            if (value < 6000 || value > 510000) return;
            _config.Bitrate = value;
            SaveConfigToDisk();
        }
    }

    // ── Connect / Disconnect ──────────────────────────────────────────

    public async Task ConnectAsync(CancellationToken ct)
    {
        if (_status is BridgeStatus.Connecting or BridgeStatus.Connected) return;

        // Validate config up front so we throw cleanly before doing any IO.
        if (string.IsNullOrWhiteSpace(_config.BotToken))
            throw new InvalidOperationException("Bot token is empty. Paste a token from the Discord Developer Portal.");
        if (!ulong.TryParse(_config.GuildId, out ulong guildId))
            throw new InvalidOperationException("Guild ID must be a Discord snowflake (digits only).");
        if (!ulong.TryParse(_config.ChannelId, out ulong channelId))
            throw new InvalidOperationException("Channel ID must be a Discord snowflake (digits only).");

        // Borrow Opus from codec.opus. This is the linchpin of the
        // bridge architecture: if the user hasn't installed codec.opus,
        // we surface that as a meaningful error instead of crashing later
        // inside Discord.Net's voice handshake.
        var opus = _context?.CodecRegistry?.GetByExtension(".opus");
        if (opus == null)
        {
            throw new InvalidOperationException(
                "Discord Bridge requires the Opus codec plugin (codec.opus) to be installed. " +
                "Install gmsb-codec-opus from the plugin catalog, enable it, restart the app, and try again.");
        }
        if (!opus.SupportsEncoding)
        {
            throw new InvalidOperationException(
                "The installed codec.opus plugin does not advertise encoding support. " +
                "Update to v1.0.0 or later — older versions only supported decode.");
        }

        var encoder = opus.CreateEncoder(48000, Channels, _config.Bitrate);
        if (encoder == null)
        {
            throw new InvalidOperationException(
                "codec.opus refused to build an encoder for 48 kHz stereo. " +
                "Check the codec plugin's log for details.");
        }

        // If reconnecting from a prior Failed state, make sure no stale
        // client/audio/session resources are leaked before opening a new one.
        await CleanupResourcesAsync(flushStream: false).ConfigureAwait(false);

        Volatile.Write(ref _opusEncoder, encoder);
        _activeGuildId = guildId;
        _activeChannelId = channelId;
        _activeGuildName = "";
        _activeChannelName = "";
        _recentInPlaceRebuilds = 0;
        _lastInPlaceRebuildTicks = 0;
        Interlocked.Exchange(ref _consecutiveWriteErrors, 0);
        Volatile.Write(ref _gatewayConnected, 0);

        var sessionCts = new CancellationTokenSource();
        _sessionCts = sessionCts;

        TransitionTo(BridgeStatus.Connecting, "Connecting to Discord gateway…", null);

        var config = new DiscordSocketConfig
        {
            GatewayIntents = GatewayIntents.Guilds | GatewayIntents.GuildVoiceStates,
            LogLevel = LogSeverity.Info,
            EnableVoiceDaveEncryption = true,
        };
        _client = new DiscordSocketClient(config);
        _client.Log += OnDiscordLog;
        _client.Ready += OnReady;
        _client.Connected += OnGatewayConnected;
        _client.Disconnected += OnGatewayDisconnected;
        _client.UserVoiceStateUpdated += OnUserVoiceStateUpdated;

        _readyTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _gatewayConnectedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            await _client.LoginAsync(TokenType.Bot, _config.BotToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            TransitionTo(BridgeStatus.Failed, "LoginAsync rejected the token", ex);
            await CleanupResourcesAsync(flushStream: false).ConfigureAwait(false);
            throw new InvalidOperationException(
                "Discord login failed. The bot token is either malformed or was rejected by Discord. " +
                "Reset the token in the Developer Portal and paste the fresh value.",
                ex);
        }

        await _client.StartAsync().ConfigureAwait(false);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct, sessionCts.Token);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(ReadyTimeoutSeconds));
        var timeoutTask = Task.Delay(Timeout.Infinite, timeoutCts.Token);
        var winner = await Task.WhenAny(_readyTcs.Task, timeoutTask).ConfigureAwait(false);

        if (winner != _readyTcs.Task)
        {
            TransitionTo(BridgeStatus.Failed,
                $"Gateway did not reach Ready within {ReadyTimeoutSeconds}s — token most likely invalid",
                new TimeoutException("Gateway Ready timeout"));
            await CleanupResourcesAsync(flushStream: false).ConfigureAwait(false);
            throw new TimeoutException(
                $"Discord gateway did not reach Ready within {ReadyTimeoutSeconds} seconds. " +
                "Usually means the bot token is invalid, was reset in the Developer Portal, " +
                "or the bot was removed from the target server.");
        }

        // Surface any auth error captured by the Log handler.
        try
        {
            await _readyTcs.Task.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            TransitionTo(BridgeStatus.Failed, ex.Message, ex);
            await CleanupResourcesAsync(flushStream: false).ConfigureAwait(false);
            throw;
        }

        // Gateway is up — now the voice channel.
        var guild = _client.GetGuild(guildId);
        if (guild == null)
        {
            TransitionTo(BridgeStatus.Failed, $"Guild {guildId} not found", null);
            await CleanupResourcesAsync(flushStream: false).ConfigureAwait(false);
            throw new InvalidOperationException(
                $"Guild {guildId} not found. Either the Guild ID is wrong, or the bot isn't a member of that server.");
        }

        var channel = guild.GetVoiceChannel(channelId);
        if (channel == null)
        {
            TransitionTo(BridgeStatus.Failed, $"Voice channel {channelId} not found in '{guild.Name}'", null);
            await CleanupResourcesAsync(flushStream: false).ConfigureAwait(false);
            throw new InvalidOperationException(
                $"Voice channel {channelId} not found in guild '{guild.Name}'. " +
                "Check the Channel ID and that the bot can see the channel.");
        }

        _activeGuildName = guild.Name;
        _activeChannelName = channel.Name;

        TransitionTo(BridgeStatus.Connecting, $"Joining voice channel '{channel.Name}'…", null);

        try
        {
            using var joinCts = CancellationTokenSource.CreateLinkedTokenSource(ct, sessionCts.Token);
            joinCts.CancelAfter(TimeSpan.FromSeconds(VoiceConnectTimeoutSeconds));
            var stream = await AttachVoiceChannelAsync(channel, joinCts.Token).ConfigureAwait(false);
            InstallOutboundStream(stream);
        }
        catch (Exception ex)
        {
            TransitionTo(BridgeStatus.Failed, $"Failed to join voice channel '{channel.Name}'", ex);
            await CleanupResourcesAsync(flushStream: false).ConfigureAwait(false);
            throw;
        }

        Volatile.Write(ref _wantsOutboundAudio, 1);
        Volatile.Write(ref _sessionEstablished, 1);
        TransitionTo(BridgeStatus.Connected, $"Streaming to '{channel.Name}' in '{guild.Name}'", null);

        // Start background health watchdog so silent voice drops or stalled
        // heartbeats are detected and healed even when no write throws.
        _watchdogTask = Task.Run(() => RunWatchdogLoopAsync(sessionCts.Token));
    }

    public async Task DisconnectAsync()
    {
        Volatile.Write(ref _wantsOutboundAudio, 0);

        // Cancel the session first so any in-flight recovery backoff, voice
        // handshake, watchdog wait, or blocked write exits immediately.
        var sessionCts = Interlocked.Exchange(ref _sessionCts, null);
        if (sessionCts != null)
        {
            try { sessionCts.Cancel(); } catch { }
        }

        await CleanupResourcesAsync(flushStream: true).ConfigureAwait(false);

        if (sessionCts != null)
        {
            try { sessionCts.Dispose(); } catch { }
        }

        TransitionTo(BridgeStatus.Disconnected, null, null);
    }

    private async Task CleanupResourcesAsync(bool flushStream)
    {
        Volatile.Write(ref _sessionEstablished, 0);
        Volatile.Write(ref _wantsOutboundAudio, 0);

        var sessionCts = Interlocked.Exchange(ref _sessionCts, null);
        if (sessionCts != null)
        {
            try { sessionCts.Cancel(); } catch { }
        }

        // Cancel and dispose the outbound stream first so any worker thread
        // blocked inside WriteAsync unblocks immediately.
        var oldOutbound = Interlocked.Exchange(ref _outboundState, null);
        if (oldOutbound != null)
        {
            await DisposeOutboundStateAsync(oldOutbound, flushStream).ConfigureAwait(false);
        }

        // Wait briefly for any in-flight recovery or watchdog task to observe
        // session cancellation before tearing down the Discord clients.
        Task? recoveryTask;
        lock (_recoverySync)
        {
            _pendingRecovery = false;
            _pendingForceRejoin = false;
            recoveryTask = _recoveryTask;
        }
        if (recoveryTask != null)
        {
            try { await recoveryTask.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { }
        }

        var watchdog = Interlocked.Exchange(ref _watchdogTask, null);
        if (watchdog != null)
        {
            try { await watchdog.WaitAsync(TimeSpan.FromSeconds(1)).ConfigureAwait(false); } catch { }
        }

        await DetachAndDisposeAudioClientAsync().ConfigureAwait(false);
        await TryTeardownClientAsync().ConfigureAwait(false);

        var encoder = Interlocked.Exchange(ref _opusEncoder, null);
        try { encoder?.Dispose(); } catch { }

        lock (_accumLock) _accumLength = 0;
        Interlocked.Exchange(ref _consecutiveWriteErrors, 0);
        Volatile.Write(ref _gatewayConnected, 0);

        if (sessionCts != null)
        {
            try { sessionCts.Dispose(); } catch { }
        }
    }

    private async Task DisposeOutboundStateAsync(OutboundStreamState state, bool flush)
    {
        // On clean user disconnect, attempt a bounded FlushAsync BEFORE
        // cancelling the stream token (BufferedWriteStream.FlushAsync loops
        // forever if its background pump crashed, so always bound it).
        if (flush)
        {
            try
            {
                using var flushCts = new CancellationTokenSource(FlushTimeout);
                await state.Stream.FlushAsync(flushCts.Token).ConfigureAwait(false);
            }
            catch { }
        }

        try { state.Cts.Cancel(); } catch { }
        try { await state.Stream.DisposeAsync().ConfigureAwait(false); } catch { }
        try { state.Cts.Dispose(); } catch { }
    }

    private async Task<Stream> AttachVoiceChannelAsync(SocketVoiceChannel channel, CancellationToken ct)
    {
        // CreateOpusStream takes pre-encoded frames, so we hand Discord.Net
        // Opus packets we already built via Concentus (borrowed from
        // codec.opus) and skip its *internal Opus encoder* for outbound.
        // This does NOT remove the native dependency: Discord.Net still
        // loads native libopus (inbound decode + voice handshake) and
        // libsodium (legacy packet auth + DAVE/MLS curve primitives), and
        // both still ship in this plugin's zip.
        var audioClient = await channel.ConnectAsync().WaitAsync(ct).ConfigureAwait(false);

        // Wire lifecycle + heartbeat monitoring on this specific client instance.
        audioClient.Disconnected += ex => OnAudioClientDisconnected(audioClient, ex);
        audioClient.LatencyUpdated += (before, after) => OnAudioClientLatencyUpdated(audioClient, before, after);

        _audioClient = audioClient;
        long now = Stopwatch.GetTimestamp();
        Volatile.Write(ref _lastVoiceConnectedTicks, now);
        Volatile.Write(ref _lastVoiceHeartbeatTicks, now);

        // Reset Discord.Net's cached _isSpeaking flag so the first frame sent
        // through the new stream reliably emits the Speaking opcode (Op 5).
        try
        {
            using var speakCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            speakCts.CancelAfter(TimeSpan.FromSeconds(2));
            await audioClient.SetSpeakingAsync(false).WaitAsync(speakCts.Token).ConfigureAwait(false);
        }
        catch { /* best effort */ }

        return audioClient.CreateOpusStream();
    }

    private async Task DetachAndDisposeAudioClientAsync()
    {
        var audioClient = Interlocked.Exchange(ref _audioClient, null);
        if (audioClient == null) return;

        try { await audioClient.StopAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { }
        try { audioClient.Dispose(); } catch { }
    }

    private async Task TryTeardownClientAsync()
    {
        var client = Interlocked.Exchange(ref _client, null);
        if (client == null) return;

        try { client.Log -= OnDiscordLog; } catch { }
        try { client.Ready -= OnReady; } catch { }
        try { client.Connected -= OnGatewayConnected; } catch { }
        try { client.Disconnected -= OnGatewayDisconnected; } catch { }
        try { client.UserVoiceStateUpdated -= OnUserVoiceStateUpdated; } catch { }
        try { await client.StopAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { }
        try { await client.LogoutAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { }
        try { client.Dispose(); } catch { }
        _readyTcs = null;
        _gatewayConnectedTcs = null;
    }

    private void InstallOutboundStream(Stream stream)
    {
        int nextGen = Interlocked.Increment(ref _streamGeneration);
        var nextState = new OutboundStreamState(stream, nextGen);
        var prev = Interlocked.Exchange(ref _outboundState, nextState);
        if (prev != null)
        {
            _ = Task.Run(() => DisposeOutboundStateAsync(prev, flush: false));
        }

        lock (_accumLock) _accumLength = 0;
        Interlocked.Exchange(ref _consecutiveWriteErrors, 0);
    }

    private void InvalidateOutboundStream()
    {
        var prev = Interlocked.Exchange(ref _outboundState, null);
        if (prev != null)
        {
            try { prev.Cts.Cancel(); } catch { }
            _ = Task.Run(() => DisposeOutboundStateAsync(prev, flush: false));
        }
    }

    // ── Outbound audio: host worker calls this per chunk ──────────────

    public void SendOutboundPcm(ReadOnlySpan<float> pcm)
    {
        if (Volatile.Read(ref _wantsOutboundAudio) == 0) return;

        var state = Volatile.Read(ref _outboundState);
        var encoder = Volatile.Read(ref _opusEncoder);
        if (state == null || encoder == null) return;

        lock (_accumLock)
        {
            // Grow accumulator if a giant chunk arrives. Bridges typically
            // see 10 ms chunks (480 samples * 2 = 960 floats); the
            // initial 4× buffer is way oversized but we still guard.
            int need = _accumLength + pcm.Length;
            if (need > _accumBuffer.Length)
            {
                var bigger = new float[Math.Max(need, _accumBuffer.Length * 2)];
                Array.Copy(_accumBuffer, bigger, _accumLength);
                _accumBuffer = bigger;
            }
            pcm.CopyTo(_accumBuffer.AsSpan(_accumLength));
            _accumLength += pcm.Length;
        }

        // Drain whole 20 ms frames out of the accumulator until we don't
        // have enough left for another. Each frame: encode → write to the
        // Discord opus stream. We synchronously block on the host's
        // AudioBridgeHost worker thread (never the audio thread), with a
        // strict per-write timeout so a dead BufferedWriteStream can never
        // deadlock the worker.
        var packet = ArrayPool<byte>.Shared.Rent(MaxOpusPacketBytes);
        var frame = ArrayPool<float>.Shared.Rent(OpusFrameSamplesStereo);
        try
        {
            while (true)
            {
                if (Volatile.Read(ref _wantsOutboundAudio) == 0) return;

                // Re-snapshot in case recovery swapped the stream mid-batch.
                state = Volatile.Read(ref _outboundState);
                encoder = Volatile.Read(ref _opusEncoder);
                if (state == null || encoder == null) return;

                lock (_accumLock)
                {
                    if (_accumLength < OpusFrameSamplesStereo) break;
                    Array.Copy(_accumBuffer, frame, OpusFrameSamplesStereo);
                    // Slide the remainder down.
                    int remaining = _accumLength - OpusFrameSamplesStereo;
                    if (remaining > 0)
                        Array.Copy(_accumBuffer, OpusFrameSamplesStereo, _accumBuffer, 0, remaining);
                    _accumLength = remaining;
                }

                int written;
                try
                {
                    written = encoder.Encode(frame.AsSpan(0, OpusFrameSamplesStereo), packet);
                }
                catch
                {
                    // Encoder errors don't bring down the bridge; just
                    // drop the frame. The next frame might succeed.
                    continue;
                }

                CancellationToken streamToken;
                CancellationTokenSource writeCts;
                try
                {
                    streamToken = state.Cts.Token;
                    if (streamToken.IsCancellationRequested) return;
                    writeCts = CancellationTokenSource.CreateLinkedTokenSource(streamToken);
                }
                catch (ObjectDisposedException)
                {
                    // Stream state was just replaced or torn down cleanly.
                    return;
                }

                using (writeCts)
                {
                    writeCts.CancelAfter(WriteTimeout);

                    try
                    {
                        // CreateOpusStream's WriteAsync expects the raw Opus
                        // packet length. Bounded by WriteTimeout so if
                        // BufferedWriteStream's background loop faults silently
                        // and stops releasing its internal semaphore, we detect
                        // the stall and self-heal instead of hanging forever.
                        state.Stream.WriteAsync(packet, 0, written, writeCts.Token).GetAwaiter().GetResult();
                        Interlocked.Exchange(ref _consecutiveWriteErrors, 0);
                    }
                    catch (OperationCanceledException oce)
                    {
                        if (streamToken.IsCancellationRequested)
                        {
                            // Intentional teardown or stream swap — exit quietly.
                            return;
                        }

                        if (writeCts.IsCancellationRequested)
                        {
                            // Our per-write timeout fired: BufferedWriteStream's
                            // background pump is stalled or dead. Trigger fast
                            // stream rebuild (which escalates to full voice rejoin
                            // if stalls persist).
                            Console.WriteLine(
                                $"[bridge.discord] Audio stream write timed out after {WriteTimeout.TotalMilliseconds:F0} ms; scheduling automatic recovery.");
                            ScheduleRecovery(state.Generation, forceRejoin: false, "Audio stream write timed out", oce);
                            return;
                        }

                        // Neither our stream token nor our write timeout fired,
                        // which means Discord.Net's internal _connection.CancelToken
                        // cancelled the stream (voice WebSocket drop/resume).
                        // Escalate straight to voice rejoin because Discord.Net
                        // 3.20.x's voice resume drops the DAVE session.
                        Console.WriteLine(
                            "[bridge.discord] Discord voice stream was cancelled externally; scheduling voice rejoin.");
                        ScheduleRecovery(state.Generation, forceRejoin: true, "Voice audio stream was terminated", oce);
                        return;
                    }
                    catch (ObjectDisposedException ode)
                    {
                        if (streamToken.IsCancellationRequested) return;

                        Console.WriteLine(
                            "[bridge.discord] Audio stream was disposed unexpectedly; scheduling automatic recovery.");
                        ScheduleRecovery(state.Generation, forceRejoin: false, "Audio stream was disposed", ode);
                        return;
                    }
                    catch (Exception ex)
                    {
                        if (streamToken.IsCancellationRequested) return;

                        int errors = Interlocked.Increment(ref _consecutiveWriteErrors);
                        if (errors == 1)
                        {
                            Console.WriteLine($"[bridge.discord] Audio stream write error: {ex.Message}");
                        }

                        if (errors >= ConsecutiveWriteFailureThreshold)
                        {
                            Console.WriteLine(
                                $"[bridge.discord] {errors} consecutive audio write errors; scheduling automatic recovery.");
                            ScheduleRecovery(state.Generation, forceRejoin: false,
                                $"Audio stream write failed: {ex.Message}", ex);
                            return;
                        }
                    }
                }
            }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(frame);
            ArrayPool<byte>.Shared.Return(packet);
        }
    }

    // ── Automatic self-healing coordinator ────────────────────────────

    private bool IsSessionActive
    {
        get
        {
            if (Volatile.Read(ref _sessionEstablished) == 0) return false;
            var cts = _sessionCts;
            return cts != null && !cts.IsCancellationRequested;
        }
    }

    /// <summary>
    /// Queue a single-flight recovery pass. Safe to call from any thread
    /// (audio worker, Discord.Net event dispatcher, watchdog). Never blocks
    /// the caller.
    /// </summary>
    private void ScheduleRecovery(int? failedStreamGeneration, bool forceRejoin, string reason, Exception? error)
    {
        if (!IsSessionActive) return;
        var sessionCts = _sessionCts;
        if (sessionCts == null || sessionCts.IsCancellationRequested) return;

        // Ignore stale failure reports from an already-replaced stream unless
        // this is a voice/gateway-level forced rejoin.
        if (failedStreamGeneration.HasValue && !forceRejoin)
        {
            int currentGen = Volatile.Read(ref _streamGeneration);
            if (failedStreamGeneration.Value != currentGen) return;
        }

        // Stop sending into a known-broken stream right away so the worker
        // thread doesn't spin on repeated timeouts while recovery boots up.
        Volatile.Write(ref _wantsOutboundAudio, 0);
        InvalidateOutboundStream();

        lock (_recoverySync)
        {
            if (sessionCts.IsCancellationRequested) return;

            _pendingRecovery = true;
            _pendingForceRejoin |= forceRejoin;
            _pendingRecoveryReason = reason;
            _pendingRecoveryError = error;

            if (_recoveryRunning) return;
            _recoveryRunning = true;
            _recoveryTask = Task.Run(() => RunRecoveryLoopAsync(sessionCts.Token));
        }
    }

    private async Task RunRecoveryLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                bool forceRejoin;
                string reason;
                Exception? error;

                lock (_recoverySync)
                {
                    if (!_pendingRecovery)
                    {
                        _recoveryRunning = false;
                        return;
                    }

                    _pendingRecovery = false;
                    forceRejoin = _pendingForceRejoin;
                    _pendingForceRejoin = false;
                    reason = _pendingRecoveryReason ?? "Audio stream interrupted";
                    error = _pendingRecoveryError;
                    _pendingRecoveryReason = null;
                    _pendingRecoveryError = null;
                }

                bool healed = await ExecuteSingleRecoveryAsync(forceRejoin, reason, error, ct).ConfigureAwait(false);
                if (!healed)
                {
                    lock (_recoverySync)
                    {
                        _pendingRecovery = false;
                        _pendingForceRejoin = false;
                        _recoveryRunning = false;
                    }
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // User disconnected — normal exit.
        }
        finally
        {
            lock (_recoverySync)
            {
                _recoveryRunning = false;
            }
        }
    }

    private async Task<bool> ExecuteSingleRecoveryAsync(
        bool forceRejoin,
        string reason,
        Exception? initialError,
        CancellationToken ct)
    {
        // Level 1: Fast in-place stream rebuild when the underlying voice
        // connection is still healthy and we haven't already rebuilt too
        // recently. This heals transient BufferedWriteStream / DAVE-RTP
        // stalls in milliseconds without leaving the voice channel.
        if (!forceRejoin && CanAttemptInPlaceRebuild())
        {
            RecordInPlaceRebuild();
            Console.WriteLine($"[bridge.discord] Attempting in-place Opus stream rebuild ({reason})…");

            try
            {
                var rebuilt = await TryRebuildStreamInPlaceAsync(ct).ConfigureAwait(false);
                if (rebuilt != null)
                {
                    InstallOutboundStream(rebuilt);
                    Volatile.Write(ref _wantsOutboundAudio, 1);
                    TransitionTo(BridgeStatus.Connected, ConnectedStatusDetail(), null);
                    Console.WriteLine("[bridge.discord] Rebuilt outbound Opus stream in place; audio resumed.");
                    return true;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"[bridge.discord] In-place stream rebuild failed ({ex.Message}); escalating to full voice rejoin.");
                initialError = ex;
            }
        }
        else if (!forceRejoin)
        {
            Console.WriteLine(
                $"[bridge.discord] Stream stalled again within {RebuildEscalationWindow.TotalSeconds:F0}s ({reason}); escalating to full voice rejoin.");
        }

        // Level 2: Full voice channel rejoin with exponential backoff.
        // Re-establishes a brand-new AudioClient, fresh DAVE MLS session,
        // fresh UDP discovery/socket, fresh SecretKey, and fresh Opus stream.
        TransitionTo(BridgeStatus.Connecting, $"Reconnecting voice ({reason})…", null);

        var backoffs = ReconnectBackoffs;
        Exception? lastException = initialError;

        for (int attempt = 1; attempt <= backoffs.Length; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            // Wait before retry (brief delay even on attempt 1 so Discord's
            // side can finish tearing down the dropped voice session).
            var delay = backoffs[attempt - 1];
            if (delay > TimeSpan.Zero)
            {
                TransitionTo(BridgeStatus.Connecting,
                    $"Rejoining voice channel (attempt {attempt}/{backoffs.Length})…", null);
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }

            try
            {
                Console.WriteLine(
                    $"[bridge.discord] Rejoining voice channel (attempt {attempt}/{backoffs.Length})…");
                var freshStream = await PerformFullVoiceRejoinAsync(ct).ConfigureAwait(false);

                // Reset escalation counter after a clean full rejoin.
                _recentInPlaceRebuilds = 0;
                _lastInPlaceRebuildTicks = 0;

                InstallOutboundStream(freshStream);
                Volatile.Write(ref _wantsOutboundAudio, 1);
                TransitionTo(BridgeStatus.Connected, ConnectedStatusDetail(), null);
                Console.WriteLine("[bridge.discord] Voice channel rejoined cleanly; audio stream healed.");
                return true;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastException = ex;
                Console.WriteLine(
                    $"[bridge.discord] Voice rejoin attempt {attempt}/{backoffs.Length} failed: {ex.Message}");
            }
        }

        // All automatic recovery attempts exhausted — surface honest Failed
        // status so the user can see what happened and reconnect manually.
        Volatile.Write(ref _wantsOutboundAudio, 0);
        InvalidateOutboundStream();
        TransitionTo(BridgeStatus.Failed,
            $"Voice stream lost and could not auto-reconnect ({lastException?.Message ?? reason})",
            lastException);
        Console.WriteLine(
            $"[bridge.discord] Auto-recovery exhausted after {backoffs.Length} attempts: {lastException?.Message ?? reason}");
        return false;
    }

    private bool CanAttemptInPlaceRebuild()
    {
        if (_lastInPlaceRebuildTicks == 0) return true;
        var elapsed = Stopwatch.GetElapsedTime(_lastInPlaceRebuildTicks);
        if (elapsed >= RebuildEscalationWindow) return true;
        return _recentInPlaceRebuilds < MaxInPlaceRebuildsInWindow;
    }

    private void RecordInPlaceRebuild()
    {
        long now = Stopwatch.GetTimestamp();
        if (_lastInPlaceRebuildTicks == 0 ||
            Stopwatch.GetElapsedTime(_lastInPlaceRebuildTicks) >= RebuildEscalationWindow)
        {
            _recentInPlaceRebuilds = 1;
        }
        else
        {
            _recentInPlaceRebuilds++;
        }
        _lastInPlaceRebuildTicks = now;
    }

    private async Task<Stream?> TryRebuildStreamInPlaceAsync(CancellationToken ct)
    {
        if (_testRebuildStreamAsync != null)
            return await _testRebuildStreamAsync(ct).ConfigureAwait(false);

        var audioClient = _audioClient;
        if (audioClient == null || audioClient.ConnectionState != ConnectionState.Connected)
            return null;

        // Verify the guild hasn't already detached/disposed this audio client.
        var guild = _client?.GetGuild(_activeGuildId);
        if (guild != null && !ReferenceEquals(guild.AudioClient, audioClient))
            return null;

        // Reset cached _isSpeaking so the new stream sends Speaking=true (Op 5).
        try
        {
            using var speakCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            speakCts.CancelAfter(TimeSpan.FromSeconds(2));
            await audioClient.SetSpeakingAsync(false).WaitAsync(speakCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // If SetSpeakingAsync itself fails/times out, the voice WebSocket
            // is likely wedged — return null to escalate to full voice rejoin.
            return null;
        }

        return audioClient.CreateOpusStream();
    }

    private async Task<Stream> PerformFullVoiceRejoinAsync(CancellationToken ct)
    {
        // If the main gateway is mid-reconnect, wait for it before attempting
        // to resolve the guild/channel and send VoiceStateUpdate.
        if (Volatile.Read(ref _gatewayConnected) == 0 ||
            (_client != null && _client.ConnectionState != ConnectionState.Connected))
        {
            TransitionTo(BridgeStatus.Connecting, "Waiting for Discord gateway to reconnect…", null);
            await WaitForGatewayConnectedAsync(ct).ConfigureAwait(false);
        }

        if (_testRejoinVoiceAsync != null)
        {
            var stream = await _testRejoinVoiceAsync(ct).ConfigureAwait(false);
            long now = Stopwatch.GetTimestamp();
            Volatile.Write(ref _lastVoiceConnectedTicks, now);
            Volatile.Write(ref _lastVoiceHeartbeatTicks, now);
            return stream;
        }

        var client = _client ?? throw new InvalidOperationException("Discord client is no longer available.");

        var guild = client.GetGuild(_activeGuildId)
            ?? throw new InvalidOperationException($"Guild {_activeGuildId} is currently unavailable.");
        var channel = guild.GetVoiceChannel(_activeChannelId)
            ?? throw new InvalidOperationException($"Voice channel {_activeChannelId} not found in '{guild.Name}'.");

        _activeGuildName = guild.Name;
        _activeChannelName = channel.Name;

        // Cleanly tear down the previous AudioClient and tell Discord we left
        // so the subsequent ConnectAsync performs a full fresh Identify +
        // UDP discovery + DAVE MLS handshake (avoiding Discord.Net's broken
        // voice Resume path).
        await DetachAndDisposeAudioClientAsync().ConfigureAwait(false);
        try
        {
            using var leaveCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            leaveCts.CancelAfter(TimeSpan.FromSeconds(3));
            await channel.DisconnectAsync().WaitAsync(leaveCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch { /* best effort */ }

        using var joinCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        joinCts.CancelAfter(TimeSpan.FromSeconds(VoiceConnectTimeoutSeconds));
        return await AttachVoiceChannelAsync(channel, joinCts.Token).ConfigureAwait(false);
    }

    private async Task WaitForGatewayConnectedAsync(CancellationToken ct)
    {
        var client = _client;
        if (client != null && client.ConnectionState == ConnectionState.Connected) return;

        var tcs = Volatile.Read(ref _gatewayConnectedTcs);
        if (tcs == null)
            throw new InvalidOperationException("Discord gateway is disconnected.");

        using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        waitCts.CancelAfter(GatewayWaitTimeout);
        await tcs.Task.WaitAsync(waitCts.Token).ConfigureAwait(false);
    }

    // ── Background voice health watchdog ──────────────────────────────

    private async Task RunWatchdogLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(WatchdogInterval, ct).ConfigureAwait(false);
                EvaluateVoiceHealthOnce();
            }
        }
        catch (OperationCanceledException)
        {
            // Normal exit on DisconnectAsync.
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[bridge.discord] Watchdog loop error: {ex.Message}");
        }
    }

    internal void EvaluateVoiceHealthOnce()
    {
        if (!IsSessionActive) return;
        if (_status != BridgeStatus.Connected) return;

        lock (_recoverySync)
        {
            if (_recoveryRunning) return;
        }

        var (healthy, reason) = CheckVoiceHealth();
        if (!healthy)
        {
            Console.WriteLine($"[bridge.discord] Voice health watchdog detected failure: {reason}. Scheduling voice rejoin.");
            ScheduleRecovery(null, forceRejoin: true, reason ?? "Voice health check failed", null);
        }
    }

    private (bool Healthy, string? Reason) CheckVoiceHealth()
    {
        if (_testCheckVoiceHealth != null)
            return _testCheckVoiceHealth();

        var audioClient = _audioClient;
        if (audioClient == null || audioClient.ConnectionState != ConnectionState.Connected)
            return (false, "Voice client is no longer connected");

        var guild = _client?.GetGuild(_activeGuildId);
        if (guild != null)
        {
            if (!ReferenceEquals(guild.AudioClient, audioClient))
                return (false, "Guild voice client was detached");

            long connectedTicks = Volatile.Read(ref _lastVoiceConnectedTicks);
            if (connectedTicks != 0 &&
                Stopwatch.GetElapsedTime(connectedTicks) >= VoiceStateGracePeriod &&
                guild.CurrentUser?.VoiceChannel == null)
            {
                return (false, "Bot is no longer in the voice channel");
            }
        }

        long heartbeatTicks = Volatile.Read(ref _lastVoiceHeartbeatTicks);
        if (heartbeatTicks != 0 &&
            Stopwatch.GetElapsedTime(heartbeatTicks) > HeartbeatStaleThreshold)
        {
            return (false, $"No voice heartbeat ACK for over {HeartbeatStaleThreshold.TotalSeconds:F0}s");
        }

        return (true, null);
    }

    private string ConnectedStatusDetail()
    {
        if (!string.IsNullOrEmpty(_activeChannelName) && !string.IsNullOrEmpty(_activeGuildName))
            return $"Streaming to '{_activeChannelName}' in '{_activeGuildName}'";
        return "Streaming audio";
    }

    // ── Test seams ────────────────────────────────────────────────────

    /// <summary>
    /// Test-only hook that wires the outbound encode→write path without a
    /// live Discord connection. Production sets these same fields inside
    /// <see cref="ConnectAsync"/> after the voice handshake; tests can't
    /// reach that path (it needs a real bot token + gateway + native
    /// libs), so this lets a unit test drive <see cref="SendOutboundPcm"/>
    /// against a faked encoder and an in-memory <paramref name="outStream"/>.
    /// </summary>
    internal void ConfigureOutboundForTest(IAudioFrameEncoder encoder, Stream outStream)
    {
        Volatile.Write(ref _opusEncoder, encoder);
        _sessionCts?.Dispose();
        _sessionCts = new CancellationTokenSource();
        Volatile.Write(ref _gatewayConnected, 1);
        Volatile.Write(ref _sessionEstablished, 1);
        _gatewayConnectedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _gatewayConnectedTcs.TrySetResult();
        _recentInPlaceRebuilds = 0;
        _lastInPlaceRebuildTicks = 0;
        InstallOutboundStream(outStream);
        Volatile.Write(ref _wantsOutboundAudio, 1);
        TransitionTo(BridgeStatus.Connected, "Test stream", null);
    }

    /// <summary>
    /// Test-only hook that wires custom recovery delegates so unit tests can
    /// exercise Level 1 stream rebuild, Level 2 voice rejoin, escalation,
    /// backoff retries, watchdog checks, and gateway/voice disconnect events
    /// deterministically without network IO.
    /// </summary>
    internal void ConfigureRecoveryForTest(
        Func<CancellationToken, Task<Stream?>>? rebuildStreamAsync = null,
        Func<CancellationToken, Task<Stream>>? rejoinVoiceAsync = null,
        Func<(bool Healthy, string? Reason)>? checkVoiceHealth = null)
    {
        _testRebuildStreamAsync = rebuildStreamAsync;
        _testRejoinVoiceAsync = rejoinVoiceAsync;
        _testCheckVoiceHealth = checkVoiceHealth;
    }

    internal void SimulateAudioClientDisconnectedForTest(Exception? ex)
    {
        if (!IsSessionActive) return;
        Console.WriteLine($"[bridge.discord] Voice client disconnected (test: {ex?.Message ?? "none"}); scheduling voice rejoin.");
        ScheduleRecovery(null, forceRejoin: true, ex?.Message ?? "Voice connection lost", ex);
    }

    internal Task SimulateGatewayDisconnectedForTest(Exception? ex) => OnGatewayDisconnected(ex);

    internal Task SimulateGatewayConnectedForTest() => OnGatewayConnected();

    internal Task WaitForRecoveryIdleForTestAsync()
    {
        Task? task;
        lock (_recoverySync) task = _recoveryTask;
        return task ?? Task.CompletedTask;
    }

    // ── Discord.Net event handlers ────────────────────────────────────

    private Task OnDiscordLog(LogMessage msg)
    {
        // The host wires its own Log mirror in PluginService — anything
        // a plugin Console.WriteLines lands in gmsound.log. Use that path.
        var detail = msg.Exception != null && !string.IsNullOrEmpty(msg.Message)
            ? $"{msg.Message} ({msg.Exception.GetType().Name}: {msg.Exception.Message})"
            : msg.Message ?? msg.Exception?.Message;
        Console.WriteLine($"[Discord.{msg.Source}] {msg.Severity}: {detail}");

        if (msg.Severity == LogSeverity.Critical || msg.Severity == LogSeverity.Error)
        {
            _readyTcs?.TrySetException(
                msg.Exception ?? new InvalidOperationException($"Discord gateway error: {msg.Message}"));
        }
        return Task.CompletedTask;
    }

    private Task OnReady()
    {
        Volatile.Write(ref _gatewayConnected, 1);
        _readyTcs?.TrySetResult();
        _gatewayConnectedTcs?.TrySetResult();
        return Task.CompletedTask;
    }

    private Task OnGatewayConnected()
    {
        Volatile.Write(ref _gatewayConnected, 1);
        _gatewayConnectedTcs?.TrySetResult();

        // If we were mid-session and paused because the gateway dropped,
        // ensure voice recovery kicks off now that the gateway is back.
        if (IsSessionActive && _status != BridgeStatus.Connected)
        {
            bool needsRecovery;
            lock (_recoverySync) needsRecovery = !_recoveryRunning;
            if (needsRecovery)
            {
                ScheduleRecovery(null, forceRejoin: true, "Gateway reconnected; restoring voice", null);
            }
        }
        return Task.CompletedTask;
    }

    private Task OnGatewayDisconnected(Exception? ex)
    {
        Volatile.Write(ref _gatewayConnected, 0);
        // Reset the waiter so any recovery loop waits for the NEXT Connected event.
        Interlocked.Exchange(ref _gatewayConnectedTcs,
            new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));

        _readyTcs?.TrySetException(ex ?? new InvalidOperationException("Disconnected before Ready"));

        if (IsSessionActive)
        {
            Console.WriteLine(
                $"[bridge.discord] Gateway disconnected ({ex?.Message ?? "unknown reason"}); pausing audio and awaiting reconnect.");
            Volatile.Write(ref _wantsOutboundAudio, 0);
            InvalidateOutboundStream();
            TransitionTo(BridgeStatus.Connecting, "Gateway disconnected; waiting to reconnect…", null);
            ScheduleRecovery(null, forceRejoin: true, "Gateway disconnected", ex);
        }
        return Task.CompletedTask;
    }

    private Task OnAudioClientDisconnected(IAudioClient sourceClient, Exception? ex)
    {
        // Ignore disconnect callbacks from a previous AudioClient we already
        // replaced or are tearing down during DisconnectAsync / rejoin.
        if (!ReferenceEquals(_audioClient, sourceClient)) return Task.CompletedTask;
        if (!IsSessionActive) return Task.CompletedTask;

        Console.WriteLine(
            $"[bridge.discord] Voice client disconnected ({ex?.Message ?? "unknown reason"}); scheduling full voice rejoin.");
        ScheduleRecovery(null, forceRejoin: true, ex?.Message ?? "Voice connection lost", ex);
        return Task.CompletedTask;
    }

    private Task OnAudioClientLatencyUpdated(IAudioClient sourceClient, int before, int after)
    {
        if (ReferenceEquals(_audioClient, sourceClient))
        {
            Volatile.Write(ref _lastVoiceHeartbeatTicks, Stopwatch.GetTimestamp());
        }
        return Task.CompletedTask;
    }

    private Task OnUserVoiceStateUpdated(SocketUser user, SocketVoiceState before, SocketVoiceState after)
    {
        var selfId = _client?.CurrentUser?.Id;
        if (selfId == null || user.Id != selfId.Value) return Task.CompletedTask;
        if (!IsSessionActive) return Task.CompletedTask;

        if (after.VoiceChannel != null && after.VoiceChannel.Id != _activeChannelId)
        {
            // Bot was moved to another voice channel — follow it so status
            // and any subsequent recovery target the new channel.
            _activeChannelId = after.VoiceChannel.Id;
            _activeChannelName = after.VoiceChannel.Name;
            Console.WriteLine(
                $"[bridge.discord] Bot moved to voice channel '{_activeChannelName}' ({_activeChannelId}).");
            if (_status == BridgeStatus.Connected)
            {
                TransitionTo(BridgeStatus.Connected, ConnectedStatusDetail(), null);
            }
        }
        else if (before.VoiceChannel != null && after.VoiceChannel == null && _status == BridgeStatus.Connected)
        {
            Console.WriteLine(
                "[bridge.discord] Bot voice state was cleared while connected; scheduling voice rejoin.");
            ScheduleRecovery(null, forceRejoin: true, "Disconnected from voice channel", null);
        }

        return Task.CompletedTask;
    }

    private void TransitionTo(BridgeStatus newStatus, string? detail, Exception? error)
    {
        _status = newStatus;
        _statusDetail = detail;
        _lastError = error;
        try { StatusChanged?.Invoke(this, EventArgs.Empty); }
        catch { /* swallow — host marshals and we don't want a UI bug to take us out */ }
    }

    // ── Settings UI ───────────────────────────────────────────────────

    public object CreateSettingsControl(IBridgeHost host, IPluginContext context)
    {
        // TODO (inbound audio): the IBridgeHost handle is intentionally
        // unused for now. To surface remote Discord speakers as inbound
        // mixer cards inside the host, call host.OpenInboundStream(displayName)
        // for each speaking voice-state user and push their decoded PCM
        // (Opus → 48 kHz IEEE-float stereo via codec.opus's IAudioFrameDecoder)
        // into the returned IInboundAudioSink. Discord.Net's SpeakingAudio
        // event has the per-user packets — needs Opus decode + sample-rate
        // matching before sink.Push. Tracked in README's "Future work" list.
        _ = host;
        _ = context;
        return DiscordBridgeView.Build(this);
    }

    // ── Config persistence ────────────────────────────────────────────

    private void LoadConfigFromDisk()
    {
        if (_configPath == null) return;
        try
        {
            if (!File.Exists(_configPath)) return;
            var json = File.ReadAllText(_configPath);
            var loaded = JsonSerializer.Deserialize<DiscordBridgeConfig>(json);
            if (loaded != null) _config = loaded;
        }
        catch
        {
            // Ignore — fall back to empty config. The user will re-enter
            // credentials in the settings UI.
        }
    }

    private void SaveConfigToDisk()
    {
        if (_configPath == null) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_configPath)!);
            File.WriteAllText(_configPath, JsonSerializer.Serialize(_config,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Don't propagate — UI persistence shouldn't crash the app.
        }
    }

    private sealed class OutboundStreamState
    {
        public Stream Stream { get; }
        public CancellationTokenSource Cts { get; }
        public int Generation { get; }

        public OutboundStreamState(Stream stream, int generation)
        {
            Stream = stream;
            Cts = new CancellationTokenSource();
            Generation = generation;
        }
    }
}

/// <summary>Persistent state shape stored in the plugin's data folder
/// (<see cref="IPluginContext.PluginDataPath"/>/config.json). Plain
/// fields, JSON-serialized via System.Text.Json.</summary>
internal sealed class DiscordBridgeConfig
{
    [JsonPropertyName("botToken")]
    public string BotToken { get; set; } = "";

    [JsonPropertyName("guildId")]
    public string GuildId { get; set; } = "";

    [JsonPropertyName("channelId")]
    public string ChannelId { get; set; } = "";

    [JsonPropertyName("bitrate")]
    public int Bitrate { get; set; } = 64_000; // VoIP-grade stereo default
}

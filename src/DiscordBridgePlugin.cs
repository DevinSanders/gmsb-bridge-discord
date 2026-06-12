using Discord;
using Discord.Audio;
using Discord.WebSocket;
using SoundBoard.PluginApi;
using System;
using System.Buffers;
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
/// </summary>
public sealed class DiscordBridgePlugin : IAudioBridgePlugin
{
    private const int ReadyTimeoutSeconds = 15;

    public string Id => "bridge.discord";
    public string Name => "Discord Bridge";
    public string Description => "Streams the master mix into a Discord voice channel. Requires codec.opus.";
    public string Version => PluginVersion.OfAssembly(typeof(DiscordBridgePlugin));
    public string Author => "Devin Sanders";

    private DiscordSocketClient? _client;
    private IAudioClient? _audioClient;
    // Typed as Stream (not Discord.Audio's AudioOutStream) so the outbound
    // encode→write path can be exercised against an in-memory sink in
    // tests. CreateOpusStream() returns an AudioOutStream, which is a
    // Stream; we only ever call WriteAsync/FlushAsync/DisposeAsync on it.
    private Stream? _opusOutStream;
    private IAudioFrameEncoder? _opusEncoder;
    private TaskCompletionSource? _readyTcs;
    private CancellationTokenSource? _streamCts;

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
        _opusEncoder = encoder;

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
        _client.Disconnected += OnDisconnected;

        _readyTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            await _client.LoginAsync(TokenType.Bot, _config.BotToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            TransitionTo(BridgeStatus.Failed, "LoginAsync rejected the token", ex);
            await TryTeardownAsync().ConfigureAwait(false);
            throw new InvalidOperationException(
                "Discord login failed. The bot token is either malformed or was rejected by Discord. " +
                "Reset the token in the Developer Portal and paste the fresh value.",
                ex);
        }

        await _client.StartAsync().ConfigureAwait(false);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(ReadyTimeoutSeconds));
        var timeoutTask = Task.Delay(Timeout.Infinite, timeoutCts.Token);
        var winner = await Task.WhenAny(_readyTcs.Task, timeoutTask).ConfigureAwait(false);

        if (winner != _readyTcs.Task)
        {
            TransitionTo(BridgeStatus.Failed,
                $"Gateway did not reach Ready within {ReadyTimeoutSeconds}s — token most likely invalid",
                new TimeoutException("Gateway Ready timeout"));
            await TryTeardownAsync().ConfigureAwait(false);
            throw new TimeoutException(
                $"Discord gateway did not reach Ready within {ReadyTimeoutSeconds} seconds. " +
                "Usually means the bot token is invalid, was reset in the Developer Portal, " +
                "or the bot was removed from the target server.");
        }

        // Surface any auth error captured by the Log handler.
        await _readyTcs.Task.ConfigureAwait(false);

        // Gateway is up — now the voice channel.
        var guild = _client.GetGuild(guildId);
        if (guild == null)
        {
            TransitionTo(BridgeStatus.Failed, $"Guild {guildId} not found", null);
            await TryTeardownAsync().ConfigureAwait(false);
            throw new InvalidOperationException(
                $"Guild {guildId} not found. Either the Guild ID is wrong, or the bot isn't a member of that server.");
        }

        var channel = guild.GetVoiceChannel(channelId);
        if (channel == null)
        {
            TransitionTo(BridgeStatus.Failed, $"Voice channel {channelId} not found in '{guild.Name}'", null);
            await TryTeardownAsync().ConfigureAwait(false);
            throw new InvalidOperationException(
                $"Voice channel {channelId} not found in guild '{guild.Name}'. " +
                "Check the Channel ID and that the bot can see the channel.");
        }

        TransitionTo(BridgeStatus.Connecting, $"Joining voice channel '{channel.Name}'…", null);

        _audioClient = await channel.ConnectAsync().ConfigureAwait(false);
        // CreateOpusStream takes pre-encoded frames, so we hand Discord.Net
        // Opus packets we already built via Concentus (borrowed from
        // codec.opus) and skip its *internal Opus encoder* for outbound.
        // This does NOT remove the native dependency: Discord.Net still
        // loads native libopus (inbound decode + voice handshake) and
        // libsodium (legacy packet auth + DAVE/MLS curve primitives), and
        // both still ship in this plugin's zip. The borrow buys one shared
        // Concentus instance across the host, not a smaller native payload.
        _opusOutStream = _audioClient.CreateOpusStream();

        _streamCts = new CancellationTokenSource();
        Volatile.Write(ref _wantsOutboundAudio, 1);
        TransitionTo(BridgeStatus.Connected, $"Streaming to '{channel.Name}' in '{guild.Name}'", null);
    }

    public async Task DisconnectAsync()
    {
        Volatile.Write(ref _wantsOutboundAudio, 0);

        _streamCts?.Cancel();

        if (_opusOutStream != null)
        {
            try { await _opusOutStream.FlushAsync().ConfigureAwait(false); } catch { }
            try { await _opusOutStream.DisposeAsync().ConfigureAwait(false); } catch { }
            _opusOutStream = null;
        }

        if (_audioClient != null)
        {
            try { await _audioClient.StopAsync().ConfigureAwait(false); } catch { }
            try { _audioClient.Dispose(); } catch { }
            _audioClient = null;
        }

        await TryTeardownAsync().ConfigureAwait(false);

        _streamCts?.Dispose();
        _streamCts = null;

        _opusEncoder?.Dispose();
        _opusEncoder = null;

        lock (_accumLock) _accumLength = 0;

        TransitionTo(BridgeStatus.Disconnected, null, null);
    }

    private async Task TryTeardownAsync()
    {
        if (_client == null) return;
        try { _client.Log -= OnDiscordLog; } catch { }
        try { _client.Ready -= OnReady; } catch { }
        try { _client.Disconnected -= OnDisconnected; } catch { }
        try { await _client.StopAsync().ConfigureAwait(false); } catch { }
        try { await _client.LogoutAsync().ConfigureAwait(false); } catch { }
        try { _client.Dispose(); } catch { }
        _client = null;
        _readyTcs = null;
    }

    // ── Outbound audio: host worker calls this per chunk ──────────────

    public void SendOutboundPcm(ReadOnlySpan<float> pcm)
    {
        if (_opusOutStream == null || _opusEncoder == null) return;
        if (Volatile.Read(ref _wantsOutboundAudio) == 0) return;

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
        // Discord opus stream. WriteAsync returns a ValueTask; we
        // synchronously block on the worker thread (this method is called
        // from the AudioBridgeHost worker, not the audio thread).
        var packet = ArrayPool<byte>.Shared.Rent(MaxOpusPacketBytes);
        try
        {
            while (true)
            {
                float[]? frame = null;
                lock (_accumLock)
                {
                    if (_accumLength < OpusFrameSamplesStereo) break;
                    frame = new float[OpusFrameSamplesStereo];
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
                    written = _opusEncoder.Encode(frame, packet);
                }
                catch
                {
                    // Encoder errors don't bring down the bridge; just
                    // drop the frame. The next frame might succeed.
                    continue;
                }

                try
                {
                    // CreateOpusStream's WriteAsync expects the raw Opus
                    // packet length. We block synchronously on the worker
                    // thread — Discord.Net's send queue is internal so
                    // this returns quickly except under backpressure.
                    var ct = _streamCts?.Token ?? CancellationToken.None;
                    _opusOutStream.WriteAsync(packet, 0, written, ct).GetAwaiter().GetResult();
                }
                catch (OperationCanceledException) { return; }
                catch
                {
                    // Network blip — Discord.Net's reconnector will rebuild
                    // the stream. Drop this frame and keep going.
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(packet);
        }
    }

    // ── Test seam ─────────────────────────────────────────────────────

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
        _opusEncoder = encoder;
        _opusOutStream = outStream;
        _streamCts = new CancellationTokenSource();
        Volatile.Write(ref _wantsOutboundAudio, 1);
    }

    // ── Discord.Net event handlers ────────────────────────────────────

    private Task OnDiscordLog(LogMessage msg)
    {
        // The host wires its own Log mirror in PluginService — anything
        // a plugin Console.WriteLines lands in gmsound.log. Use that path.
        Console.WriteLine($"[Discord.{msg.Source}] {msg.Severity}: {msg.Message ?? msg.Exception?.Message}");

        if (msg.Severity == LogSeverity.Critical || msg.Severity == LogSeverity.Error)
        {
            _readyTcs?.TrySetException(
                msg.Exception ?? new InvalidOperationException($"Discord gateway error: {msg.Message}"));
        }
        return Task.CompletedTask;
    }

    private Task OnReady()
    {
        _readyTcs?.TrySetResult();
        return Task.CompletedTask;
    }

    private Task OnDisconnected(Exception? ex)
    {
        _readyTcs?.TrySetException(ex ?? new InvalidOperationException("Disconnected before Ready"));
        if (_status == BridgeStatus.Connected)
        {
            // Mid-stream disconnect — surface it but don't tear down.
            // Discord.Net's reconnector will rebuild.
            TransitionTo(BridgeStatus.Failed, "Gateway disconnected; awaiting reconnect", ex);
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

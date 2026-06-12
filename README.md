# gmsb-bridge-discord

Discord voice-bridge plugin for [Game Master Sound Board](https://github.com/DevinSanders/game-master-soundboard).

Streams the host's master mix into a Discord voice channel — so the music, ambience, and SFX you trigger in the soundboard also play for everyone in the session, no screen-share required.

## Install

**Paid plugin.** The source is open here for reference, but the pre-built
binary is distributed pay-what-you-want on itch.io:

**→ https://dsand64.itch.io/gmsb-bridge-discord**

Download the `.zip` from that page and drop it onto **Settings → Plugin
Manager** in Game Master Sound Board. Restart the app, then set your bot token / guild / channel under **Settings → Bridges → Discord Bridge** and click **Connect & Join**. Requires the Opus codec plugin (`codec.opus`) installed.

## Required companion

This plugin **requires the [Opus codec plugin](https://github.com/DevinSanders/gmsb-codec-opus) to also be installed and enabled**. Discord voice is Opus-only, and rather than bundling a second Opus implementation, the bridge borrows the encoder from `codec.opus` via the host's [`IAudioCodecRegistry`](../Game%20Master%20Sound%20Board/SoundBoard.PluginApi/IAudioCodecRegistry.cs). Connect attempts surface a clear error if `codec.opus` is missing.

## Discord setup

1. Create a Discord application at https://discord.com/developers/applications.
2. Add a Bot user. Copy the token.
3. Generate an OAuth2 URL with scope `bot` + permissions `View Channel`, `Connect`, `Speak`. Use it to invite the bot to your server.
4. Find the **Guild ID** (right-click the server icon → Copy Server ID — requires Developer Mode in Discord settings).
5. Find the **Voice Channel ID** (right-click the voice channel → Copy Channel ID).
6. Paste all three into the Discord Bridge settings card.

## DAVE (E2E voice encryption)

Discord is rolling out [DAVE](https://discord.com/safety/using-end-to-end-encryption-for-dms), their end-to-end voice encryption protocol. **It will be mandatory for all voice/video by March 1, 2026.** This plugin bundles `libdave` (the encryption library) and enables `EnableVoiceDaveEncryption` on the Discord.Net config, so DAVE-required channels work today and the plugin remains usable past the cutoff.

## What's in the zip

The plugin zip is large because Discord.Net's voice stack requires three native libraries, shipped under `runtimes/<rid>/native/`:

- **`libdave`** — DAVE/MLS protocol implementation.
- **`opus`** — Discord.Net needs native libopus for inbound audio decoding and parts of the voice session handshake, even though this plugin uses the pre-encoded `CreateOpusStream()` path for outbound. The "borrow Opus from codec.opus" architecture still pays off: outbound encoding stays in pure-managed Concentus (one Opus implementation across the host), and only the unavoidable Discord.Net-internal native libopus ships here.
- **`libsodium`** — required by both the legacy non-DAVE voice protocol (packet authentication) AND the elliptic-curve primitives DAVE/MLS uses under the hood.

Note: the package script currently ships **every** RID the upstream NuGet packages carry (it strips only `.pdb`), including mobile/wasm RID folders the desktop host never loads. Only the five desktop RIDs matter at runtime — `win-x64`, `osx-x64`, `osx-arm64`, `linux-x64`, `linux-arm64`. Pruning the unused `runtimes/` folders is a future packaging-size optimization. Either way, users who don't install this plugin pay 0 bytes — that's the whole point of moving the bridge out of the host.

## Why this is a separate plugin

The Discord stack (Discord.Net + Discord.Net.Dave + libdave) weighs in around 12 MB compressed / 25 MB on disk. Users who only want a local soundboard shouldn't pay for that. Moving Discord into a separate plugin keeps the base GMSB install lean and opens the same architecture to future Zoom / Google Meet / Mumble bridges — same `IAudioBridgePlugin` contract, different remote.

## Build

```powershell
dotnet build src/DiscordBridgePlugin.csproj
pwsh scripts/package.ps1                      # → dist/github.DevinSanders-bridge.discord-1.0.0.zip
```

Pushing a `v<semver>` tag (e.g. `v1.0.0`) triggers `.github/workflows/release.yml`, which derives the version from the tag, stamps it into both `plugin.json` and the assembly, and publishes the zip to itch.io via `butler`. The repo is public; the binary is not — this is a paid plugin and there is no GitHub Release.

## Source

- [src/DiscordBridgePlugin.cs](src/DiscordBridgePlugin.cs) — `IAudioBridgePlugin` entry point + Discord.Net plumbing.
- [src/DiscordBridgeView.cs](src/DiscordBridgeView.cs) — Avalonia settings UI (pure code, no AXAML).
- [src/plugin.json](src/plugin.json) — manifest.

## Future work

- **Inbound audio** (`IBridgeHost.OpenInboundStream`) — surface remote
  speakers in the voice channel as inbound mixer cards inside the host
  so the GM can mix Discord users with local playback. The plumbing is
  in place on the host side; this plugin currently ignores its
  `IBridgeHost` handle in `CreateSettingsControl`. Tracked separately.

## Security note

The bot token is stored in **plaintext** at
`<plugin-data>/config.json`. The settings UI masks the input field with
`PasswordChar`, but the on-disk file is unencrypted. Protect the
`%LocalAppData%\GameMasterSoundBoard\Plugins\Data\bridge.discord\`
folder accordingly (and revoke + regenerate the token via the Discord
Developer Portal if the file ever leaks).

## License

Released under the [MIT License](LICENSE). Bundled third-party
components retain their original licenses — see
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) for the full
attribution list.

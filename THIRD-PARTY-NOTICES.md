# Third-party notices

This plugin bundles or depends on the following third-party components.
Each retains its original license; this file aggregates them for clarity.

## Managed dependencies (NuGet)

These managed assemblies ship inside the plugin zip alongside the plugin
DLL. (`NAudio.Core`, `Avalonia`, and `SoundBoard.PluginApi` are referenced
at compile time only — `ExcludeAssets="runtime"` / host-provided — and are
NOT bundled.)

| Component | License | Source |
|---|---|---|
| `Discord.Net` family (`Discord.Net.Core` / `.Rest` / `.WebSocket` / `.Commands` / `.Interactions` / `.Webhook`) | MIT | https://github.com/discord-net/Discord.Net |
| `Discord.Net.Dave` | MIT | https://github.com/discord-net/Discord.Net |
| `Microsoft.Extensions.DependencyInjection.Abstractions` | MIT | https://github.com/dotnet/runtime |
| `Newtonsoft.Json` | MIT | https://github.com/JamesNK/Newtonsoft.Json |

## Native libraries bundled per-RID

Delivered by three natives-only NuGet packages — `OpusSharp.Natives`
(`opus`), `libsodium`, and `libdave` — none of which contributes a managed
assembly. They ship inside the plugin zip under `runtimes/<rid>/native/`.

Note: the package script currently ships **all** RIDs present in the
upstream packages (it strips only `.pdb` symbols), so the zip carries
mobile/wasm RID folders the desktop host never loads. Pruning the
`runtimes/` tree to the five desktop RIDs the host actually runs on
(`win-x64`, `osx-x64`, `osx-arm64`, `linux-x64`, `linux-arm64`) is a
future packaging-size optimization, not a correctness issue.

| Library | NuGet package | License | Source | Why bundled |
|---|---|---|---|---|
| `libopus` (`opus.dll`/`.so`/`.dylib`) | `OpusSharp.Natives` | BSD 3-Clause | https://opus-codec.org/ | Discord.Net's voice subsystem decodes inbound Opus packets natively and uses parts of the native library for the voice session handshake, even though outbound encoding happens in pure-managed Concentus (via `gmsb-codec-opus`). |
| `libsodium` | `libsodium` | ISC | https://libsodium.org/ | Discord voice protocol uses libsodium for both legacy packet authentication and the elliptic-curve primitives DAVE/MLS relies on. |
| `libdave` | `libdave` | MIT (Discord) | https://github.com/discord/libdave | DAVE / MLS end-to-end voice encryption — mandatory for all Discord voice/video starting March 1, 2026. |

## LGPL compliance

None of the bundled components are LGPL or GPL. The bridge plugin itself
is MIT (see [LICENSE](LICENSE)). If you swap one of the bundled
components for an LGPL-licensed variant (a custom libopus build, for
example), drop a copy of the upstream COPYING into a
`THIRD-PARTY-LICENSES/` folder of the plugin zip and add a written
source offer alongside it.

## Host SDK

This plugin interacts with Game Master Sound Board only via the
`SoundBoard.PluginApi` SDK interfaces. The host's
[LICENSE-EXCEPTION](https://github.com/DevinSanders/game-master-soundboard/blob/main/LICENSE-EXCEPTION)
permits this plugin to ship under MIT despite the host being GPL-3.0.

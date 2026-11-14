<div align="center">

# Jungle Nexus

[![CI](https://img.shields.io/github/actions/workflow/status/Dopeman501/nexus/ci.yml?branch=main&label=CI&logo=github)](https://github.com/Dopeman501/nexus/actions/workflows/ci.yml)
[![Latest release](https://img.shields.io/github/v/release/Dopeman501/nexus?include_prereleases&label=Windows%20release)](https://github.com/Dopeman501/nexus/releases)
[![License](https://img.shields.io/badge/license-AGPL--3.0-blue)](LICENSE)
[![Platforms](https://img.shields.io/badge/platform-Windows%20%7C%20macOS%20%7C%20Linux-lightgrey)](#prerequisites)

</div>

An independent Dopeman501 community fork of
[Nexus](https://github.com/hello-nexus/nexus), adding Jungle Leopard D215 support,
audio-reactive backgrounds and lighting, transitions and Y70 page controls.
This is not an official HYTE or hello-nexus release.

## Windows download and installation

**[Download the full Windows installer — 3.1.8-beta.2-d215.23](https://github.com/Dopeman501/nexus/releases/tag/v3.1.8-beta.2-d215.23)**

Choose `Jungle-Nexus-3.1.8-beta.2-d215.23-Setup.exe`. It contains the full native
service, dashboard, desktop/panel host, RGB engine, media tools and bundled
device support, including all additions below. It is a full application
installer. Checksums, payload inventory, provenance and a source archive with
all four components are available alongside it.

- **Requirements:** Windows 10 build 19041+ / Windows 11, x64, administrator
  access and Microsoft Edge WebView2. Setup downloads Microsoft's runtime when
  absent, so that case requires internet access.
- **Unsigned community build:** Windows may show an unknown-publisher or
  SmartScreen prompt. Compare `Get-FileHash .\Jungle-Nexus-3.1.8-beta.2-d215.23-Setup.exe -Algorithm SHA256`
  with the release's `SHA256SUMS`.
- **In-place upgrade:** shares `NexusService`, `%ProgramFiles%\Nexus` and
  `%ProgramData%\Nexus` with existing builds; side-by-side installations are not
  supported. Back up application data first. The normal installer lifecycle
  retains existing settings. This new setup wrapper has not been exercised in
  a clean Windows VM.
- **Local-only:** official accounts, cloud profile sync, relay remote access,
  leaderboard, telemetry and official automatic updates are unavailable.
  Install future community releases manually from this repository.

## Functions added in this fork

### Jungle Leopard D215

- Windows discovery/live output for model `D215-NOR-JD9365-6.67inch-hor`, verified
  with firmware **3.4**; saved **0°/90°/180°/270°** mounting orientation.
- Configurable frame-rate cap up to **60 fps** and MP4 import up to 60 fps while
  preserving lower source rates. A cap does not guarantee achieved throughput.
- Hardware-confirmed **20/40/60/80/100% brightness** controls.
- **Restart screen:** bounded firmware restart, same-device reconnection and
  brightness restoration. Connection recovery also offers **Restart Nexus
  Control**; screen restart remains available without a healthy active stream.

### D215 layout and circle backgrounds

- Up to **six circular Panels** with compatible built-in/SDK widgets,
  per-instance settings, move/resize, keyboard controls, snapping, centering and
  equal-size guides.
- One movable/resizable ordinary **Widget** beneath the circles, retaining its
  pages/playlists. Widget/Panels visibility is independent, including
  background-only output.
- Each circle has independent solid colour, opacity, animation, media/slideshow
  and frosted-glass settings. **Panels Backgrounds** off retains saved looks.

### Audio Trigger backgrounds

- Independent library of up to **32 solid/animation/media looks**, sensitivity,
  activation/silence delay, ordered cycling or shuffle. Audio attacks advance
  trigger looks; silence returns to the saved normal background.
- Initial/New drafts preview before **Add**. Selected saved entries preview
  and save edits automatically. Persistent Add/New controls remain accessible.

### Lighting Audio Trigger and universal widget

- Separate lighting colour/built-in-effect/media looks, shared look editor,
  sensitivity, delay and shuffle, preserving normal lighting/pins/presets.
  Lighting retains explicit **Update**. Saved custom animations are not
  Lighting Audio Trigger look types.
- **Audio Trigger widget:** compact enable/intensity controls, immersive
  all-device/individual background and lighting controls, and per-lighting-card
  overrides. Supports desktop/Y70/monitor/phone; excluded from display-only
  single-widget LCD surfaces.

### Lighting, animation and Monitoring

- Compact Lighting colour/style choices release old pins after a confirmed
  start and resume paused lighting. Immersive Lighting includes saved custom
  animation browsing, names, search, controls and preset retention.
- Selected pinned-effect preview stays separate from the Audio Trigger stream,
  with a still-image fallback when the live renderer is unavailable.
- Built-in and saved custom animations share rotation and **10–300% zoom**
  controls across backgrounds/Lighting, with save/reopen/reset support.
- Monitoring widgets have per-instance preset/custom text colours and reset,
  including standard, Micro captions and immersive views.

### Full-screen transitions

- **226 effects: 214 GL Transitions + 12 Curtains**, static thumbnails, ordered
  multi-selection and a shared main toggle/duration.
- Independent Audio Trigger **Beat transitions** toggle/duration reuses the
  same effect list. Entry/return uses main timing; rhythm-to-rhythm changes use
  beat timing. Off retains effect selections.
- Decoded opaque images/videos, solid colours and shaders participate, retaining
  slideshow **Finish Videos** pacing. Alpha/GIF, circles and Lighting retain
  their existing rendering paths.
- Temporary device/simulator auditions preview an effect with authored stills
  without changing saved backgrounds or consuming the normal transition cycle.

### Y70 Home and page reordering

- The outline-house button beside the preview sets the displayed real page as
  **Home**. Startup opens Home; empty-background double tap returns there.
  Missing/deleted Home falls back to page one. Presets/profiles/imports preserve
  the target identity.
- Drag either top preview corner to reorder with an anchored live page and
  frosted stacked neighbours. Edge dwell traverses all ten pages while
  preserving complete contents, current selection and Home.
- Keyboard pickup/drop, arrows, Escape, announcements, reduced motion,
  interrupted-gesture cancellation and save-failure recovery are supported.

Details: [`.23` Home/reorder](release-notes/v3.1.8-beta.2-d215.23.md),
[`.22` transitions](release-notes/v3.1.8-beta.2-d215.22.md),
[`.19` consolidated additions](release-notes/v3.1.8-beta.2-d215.19.md),
[`.8` composition/backgrounds](release-notes/v3.1.8-beta.2-d215.8.md) and
[Audio Trigger editing](release-notes/08-11-26-audio-trigger-live-edit-source.md).

## Verification and limits

The matching `.23` payload was checked across **905 files** (**842 dashboard
assets**) and installed/restarted locally with saved settings preserved.
Its product source passed **1,349 dashboard owner cases**, **180 related native
cases**, type/lint/build gates, **28 source-browser cases**, **28 installed-file
mocked browser cases** and **nine isolated Native AOT checks**.

Earlier D215 circles, brightness readbacks, orientations and basic audio
colour/silence-return behavior have physical acceptance. Physical Y70
Home/reorder touch/appearance, newer transition timing, real Lighting Audio
Trigger behavior and custom-animation transforms retain acceptance limits.
Mocked browsers and package hashes establish software behavior/identity,
separately from visible physical rendering.

The unchanged npm baseline has **29 inherited audit findings**, including one
critical and 12 high. An existing standalone Node-hosting compression risk is
separate from this Windows package, which serves through ASP.NET and includes
no Node/Express server. Some added widget strings are untranslated and may
display raw translation keys outside English.
[Release provenance and build instructions](release-notes/windows-d215.23-release.md)
describe the exact package and checks.

## About the base application

Nexus is a local-first hardware monitoring, control, RGB lighting and fan
control suite for Windows, macOS, and Linux. A small native service runs on the
machine, talks to the hardware, and serves a web dashboard you open in the
browser or in an app.

There is no heavy Electron runtime and it uses your OS-native WebView. It uses a
minimal amount of CPU and RAM, and it unloads systems that you're not actively
using. By default, it will use your integrated GPU to run any rendering, freeing
up your dedicated GPU for your gaming or workstation tasks.

This repository is the umbrella: it pulls each component in as a git submodule
and carries the cross-component CI. Each component is developed in its own
repository and documented by its own `README`.

## Components

| Submodule | What it is |
|---|---|
| [`nexus-service`](https://github.com/Dopeman501/nexus-service) | The local service: hardware monitoring + control, RGB engine, dashboard host. .NET 10, native AOT. |
| [`nexus-web`](https://github.com/Dopeman501/nexus-web) | The React + TypeScript dashboard, served by the service. |
| [`nexus-overlay`](https://github.com/hello-nexus/nexus-overlay) | The desktop / in-game overlay. |
| [`nexus-rgb`](https://github.com/hello-nexus/nexus-rgb) | OpenRGB-headless workspace; provides the RGB device backends. |

## Prerequisites

- git
- .NET 10 SDK (for `nexus-service`)
- Node.js 22.12+ (for `nexus-web`)

## Quick start

```bash
# Clone with all components
git clone --recurse-submodules https://github.com/Dopeman501/nexus.git
cd nexus
# (if cloned without submodules)
git submodule update --init --recursive

# Install dashboard dependencies once
cd nexus-web && npm ci && cd ..

# Build + run the service (it embeds and serves the dashboard)
cd nexus-service && dotnet run
```

Open <http://localhost:9400> for the dashboard.

For dashboard development with hot reload, keep the service running and start the
Vite dev server in a second terminal:

```bash
cd nexus-web && npm run dev    # http://localhost:5173
```

RGB lighting needs the OpenRGB-headless binaries under
`nexus-service/Bundled/<rid>/openrgb/`, built from `nexus-rgb`; everything else
runs without them. Per-component build, publish, and test commands live in each
submodule's `README`.

## Layout

```
nexus/
  nexus-service/   Local service (.NET 10 AOT; Windows / macOS / Linux)
  nexus-web/       React dashboard, served by the service
  nexus-overlay/   Desktop / in-game overlay
  nexus-rgb/       OpenRGB-headless workspace (RGB device backends)
  .github/         Cross-component CI
  brand/           Brand assets
```

## Releases

Community Windows installers are published in this repository's
[Releases](https://github.com/Dopeman501/nexus/releases). Beta versions are
marked prerelease. This credential-free fork uses manual updates.

The separate [five-library transition comparison lab](https://github.com/Dopeman501/nexus-web/tree/main/transition-lab)
is a developer tool excluded from the installer. Only the GL/Curtains
production engines ship.

## Building from source

A build you make yourself is fully local: devices, lighting, cooling, panels,
monitoring, and LAN phone pairing all work exactly as they do in a release.

Upstream hosted services are not part of it. Accounts, profile sync, relay
remote access, leaderboard, telemetry and the in-app updater require upstream
build credentials, which are absent from this community release.

Technically, an official release carries a signed build credential injected by CI;
its presence is what compiles those surfaces in. There is no credential in this
repository, so `dotnet publish` and `npm run build` here produce the local-only
client described above. Point `NEXUS_API_BASE` at your own server if you want
to run the cloud half yourself.

For Windows packaging, see [the release instructions](release-notes/windows-d215.23-release.md)
and [`installer/Build-WindowsRelease.ps1`](installer/Build-WindowsRelease.ps1).
The release's full source archive includes the pinned submodules; GitHub's
automatic umbrella source archive alone omits their contents.

## Trademark

The AGPL covers the code, not the name. "Nexus", the Nexus logo, and the
`hellonexus.com` domain are not licensed with it: a fork must ship under its
own name and branding, and must not present itself as, or connect to, the
official service.

This repository and its installer use the Jungle Nexus community identity.
Existing executable/service/data identifiers are retained for upgrade
compatibility. Original authorship and bundled license notices are preserved.

## License

[AGPL-3.0](LICENSE). Each submodule carries its own license; the bundled
OpenRGB engine is GPLv2 and runs as a separate child process.

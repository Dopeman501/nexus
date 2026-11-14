# Jungle Nexus Windows community release

Full application version **3.1.8-beta.2-d215.23**, with the additions listed in
the [README](../README.md). The release wraps the previously verified `.23`
native/dashboard payload in a full Inno Setup installer. No runtime product
code changes are made for this packaging operation. The service documentation
is corrected to describe the existing GPL/x264 FFmpeg build accurately.

## Source and payload

| Component | Commit |
| --- | --- |
| Service | `d082e0e61b650e63388ccd3d1f01f2e3cd943316` |
| Dashboard | `fc984cf25b3c59b5a94b210045bbb08d84db8e70` |
| Overlay | `3dfddf5781b9ebdcf835941d8a45e00dda30c716` |
| RGB | `25a3bc5d5bc0dfc4c470ff2a52cc78746c21b4db` |

The release's `RELEASE.json` records the exact umbrella commit, these pins,
native hash and dashboard index hash. The executable remains
`eafb2af5da19154681eeb39ce06c68766eac3e3edb06c6b4616e49f5f37b76e4`;
the dashboard index remains
`137631ec6d024f003bb926ba8510cd70691a0e70f96b5533356a3a8c234e48c96`.

All 905 accepted application files are verified against their original complete
manifest. Historical `COMPARISON-BUILD.json` is replaced with portable
`RELEASE.json`, and `THIRD-PARTY.md` corrects the inherited FFmpeg license/source
description. The other 903 files are unchanged, including original license texts,
compressed dashboard assets, both production transition engines, native
overlay, RGB engine, media tools, drivers and support libraries. Private source
freezes, backup data, settings, credentials, logs and compiler tools are excluded.

The service commit above is the tested runtime-source baseline. `RELEASE.json`
records the release's service pin, which adds only README/third-party notice
corrections to that baseline. The source ZIP includes the application's recursive
Git trees and FFmpeg build recipe. The historical static x264 commit is unknown;
this ZIP is not a claim of complete verified corresponding third-party runtime
source. The unchanged binary reports FFmpeg 7.1, GPLv2-or-later, x264 core 165.

The community-labelled setup is unsigned; existing driver/vendor signatures
are preserved. It retains the compatible application directory, service, data
paths and installer AppId, so this is an in-place upgrade, not a side-by-side
product. It uses the existing service install/uninstall lifecycle. WebView2 is
downloaded from Microsoft if missing. It does not download the official app.

## Build the full installer

Use PowerShell 7 and Inno Setup **6.7.4**. The compiler can be installed in a
local portable tools directory with the publisher's `/PORTABLE=1` option.
Verify the official download's hash and publisher signature before running it.
No upstream signing account or hosted-service build credential is used.

First build a complete `win-x64` Native AOT publish, including the service-target
dashboard, overlay and all runtime prerequisites documented by the components.
For the local build version, pass
`-p:_NexusDevVersion=3.1.8-beta.2-d215.23` to `dotnet publish`. Do not use an
ordinary development-version binary, a marketing-site dashboard build, or an
incomplete directory staged solely from tracked Git files: bundled Windows
runtime prerequisites can be ignored inputs. Freeze and validate the complete
publish inventory before wrapping it. A fresh compilation need not produce
byte-identical executables to this pre-existing verified payload.

With all reviewed source committed and matching submodule pins:

```powershell
./installer/Build-WindowsRelease.ps1 `
  -PublishDir C:/release-input/publish `
  -PayloadManifest C:/release-input/PAYLOAD-SHA256.txt `
  -Compiler C:/tools/inno-6.7.4/ISCC.exe `
  -OutputDirectory C:/release-output/d215.23
```

The helper verifies every input hash, rejects missing/extra files, redirected
paths and debug/private-state-shaped files, checks native version and required
runtime files, and stages only the accepted inventory. It adapts the pinned
full installer recipe with guarded single replacements, preserving its runtime
lifecycle. It does not execute the installer or access live hardware/settings.

## Release assets and verification boundary

- `Jungle-Nexus-3.1.8-beta.2-d215.23-Setup.exe`: complete application installer.
- `Jungle-Nexus-3.1.8-beta.2-d215.23-source.zip`: umbrella and recursively pinned
  application component source, including OpenRGB-headless and the FFmpeg build
  recipe; no `.git` or private build tree. See the x264 provenance limit above.
- `RELEASE.json`: exact source and application identities.
- `PAYLOAD-SHA256.txt`: every embedded application file's hash.
- `SHA256SUMS`: hashes of all other downloadable release assets.

The publication process checks installer compilation/version, extracts and
compares its embedded payload without installing, validates source archive
contents against the pinned Git trees, and checks uploaded asset sizes/digests.
The underlying `.23` checks and remaining physical-acceptance limits are in
[the original release notes](v3.1.8-beta.2-d215.23.md) and README. A fresh-machine
execution of this public setup wrapper is not claimed. Publishing this release
does not change the currently installed application, user settings or hardware.

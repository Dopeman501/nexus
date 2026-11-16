# K55 black reaction restart — 16 November 2026

Rapid black-only Custom typing in K55 CORE Over effect now replaces the previous Ripple or Zone fade reaction with the latest press. Previously, concurrent widened masks could cover the whole board; effect durations were not being added together.

`KeyReactionRenderer.Press` validates the target and resolves its colour before retiring that card's prior reactions. It preserves the normal duration, speed, size, peak hold, physical-neighbour mask and colour cursor. Mixed palettes, other colours/backgrounds/devices and separate cards retain their existing behaviour. Live output and preview share the renderer; no schema, dashboard, HID or dependency changes are required.

Validation: ten regressions failed before the fix; the final 244 native owner cases pass, including rapid-burst recovery, invalid targets, preserved modes and queued-press/card isolation. Scoped reactive coverage is 96.38% of lines and 87.61% of branches. Independent code/security and payload/deployment reviews found no blockers.

From the service checkout:

```powershell
dotnet test tests/Nexus.Service.Tests/Nexus.Service.Tests.csproj -c Release -p:BuildWeb=false --filter "FullyQualifiedName~KeyReactive|FullyQualifiedName~KeyReaction|FullyQualifiedName~K55CoreReaction|FullyQualifiedName~K55BlackNeighbourReaction|FullyQualifiedName~KeyboardGeometry"
```

Matching local `.29` Native AOT installation completed on 16 November 2026. All 915 payload files and seven saved configurations/custom-effect bytes were verified; only the native executable and release metadata differ from `.28`. The dashboard and headless OpenRGB are unchanged. The user confirmed normal rapid-typing recovery in both Zone fade and Ripple. The user's selected Ripple mode and other settings were retained.

These notes describe the source and local acceptance; they do not publish a binary release or claim resolution of the separately stopped intermittent transport-freeze investigation.

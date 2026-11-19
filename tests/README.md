# Overlay verification

Run commands from `nexus-overlay`. The projects target .NET10. The Windows source-linked suite requires a64-bit Windows10 build19041+ desktop/runtime; the synthetic graphics layer also requires a working D3D11 adapter and hardware H.264 MFT. Nothing here grants a live Nexus or physical-device session.

## Pure owners

```powershell
dotnet test tests/Nexus.Overlay.Tests/Nexus.Overlay.Tests.csproj -c Release
```

These source-linked logic tests include the actual stream manager/admission owner with an inert test host. They do not initialize WebView2, capture or hardware. They are not graphics/runtime acceptance.

## Windows ownership without startup

```powershell
dotnet test tests/Nexus.Overlay.Native.Tests/Nexus.Overlay.Native.Tests.csproj -c Release --filter 'Category!=Graphics'
```

This project compiles actual native owners, excluding the production entry point and replacing logging/startup notifications. Fake COM objects, fake HTTP streams and inert hosts exercise release, deadline, cancellation, failed readback and actual host-retirement sequencing. No installed profile/log/data path is used. `native.runsettings` explicitly includes the linked product assembly in coverage and excludes test namespaces; an empty coverage report is a failed measurement.

The restricted test runner can compile yet fail VSTest/datacollector IPC. Preserve that failure and rerun the same isolated filter in the local runner; do not label an unexecuted collector as passing coverage.

## Synthetic graphics

```powershell
$env:NEXUS_GRAPHICS_EVIDENCE = 'C:\absolute\owned\evidence'
dotnet test tests/Nexus.Overlay.Native.Tests/Nexus.Overlay.Native.Tests.csproj -c Release --filter 'Category=Graphics'
```

These tests use private D3D textures, a test-owned shared section and a temporary synthetic window. They exercise real WGC arrival/restart, complete raw BGRA pixels, four-slot tracked-sample reclamation and both800×800/1024×600 H.264 pipelines at30/60fps. Optional H.264 files can be decoded with FFmpeg for frame/pixel evidence. They do not start Nexus or send device commands. The complete native project runs these graphics cases too; select `Category!=Graphics` explicitly when only fake owners are intended.

## Matched capture benchmark

`Nexus.Overlay.Bench` compiles the actual owners into a separate diagnostic executable. It runs one static WGC scene and one authored30fps WGC/hardware-encoder scene. An independent high-resolution scene timer keeps authored input unchanged when the original pump requests `timeBeginPeriod(1)`. It emits `NEXUS_BENCH_READY`, warms for the requested interval, then records process CPU/private bytes/handles/threads and frame/wake/output counts. No service URL or installed configuration is contacted.

```powershell
dotnet build tests/Nexus.Overlay.Bench/Nexus.Overlay.Bench.csproj -c Release -o C:\owned\candidate-driver
# Freeze/extract the intended original checkout first; do not substitute current source for the baseline.
dotnet build tests/Nexus.Overlay.Bench/Nexus.Overlay.Bench.csproj -c Release -p:Baseline=true -p:OverlaySource=C:\owned\baseline-source\src -o C:\owned\baseline-driver
C:\owned\candidate-driver\Nexus.Overlay.Bench.exe 120 300 C:\owned\candidate-samples.json
```

The baseline switch adapts only the old frame API and production poll/timer behavior. Both builds consume their specified complete source trees. Freeze source, complete driver directories, authored scene and tool identities before comparing. Output files are never overwritten. Warmup/measurement starts at readiness; keep CPU/GPU builds/tests out of controlled paired runs. The web efficiency launcher can coordinate this prebuilt driver with an identical three-surface browser workload. Report driver/browser data separately and distinguish required safety storage from eliminated idle work.

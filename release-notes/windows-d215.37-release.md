# Jungle Nexus 3.1.8-beta.2-d215.37 Windows prerelease

This full Windows x64 installer updates the community build from the previous public d215.23 package.

- Adds K55 CORE RGB support and custom typing palettes for supported layouts, plus per-device Red Shift controls.
- Improves RGB refresh and wireless device-list recovery and reduces allocations in LCD streaming and lighting audio media playback.
- Fixes thumbnail memory retention when leaving the picker and reduces temporary overlay memory growth during capture.
- Fixes the previous background flashing at the end of still-image and video transitions. The fix was confirmed on both connected displays and in the app preview.

Download `Jungle-Nexus-3.1.8-beta.2-d215.37-Setup.exe` and compare its SHA-256 with `SHA256SUMS`. The unsigned community installer uses the existing Nexus installation path and Windows service. It downloads WebView2 from Microsoft if needed.

`RELEASE.json` identifies the source commits and native and dashboard hashes. The recursive source ZIP contains the application source and FFmpeg build recipe. The historical x264 source revision for the bundled FFmpeg binary is unknown.

The installer payload was verified by extraction. Fresh-machine setup execution was not tested.

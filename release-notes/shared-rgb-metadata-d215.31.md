# Shared RGB metadata isolation —16 November2026

The `.31` candidate isolates metadata refresh from a controller performing slow hardware I/O. It addresses the measured Razer Basilisk V3 failure that caused a shared SDK reconnect and simultaneous freezes across otherwise healthy lighting devices. The underlying cause of the Razer HID write's3.3-second duration remains below the measured firmware/driver boundary.

## Contract and owners

- Headless `NetworkProtocol.h` defines optional client/server capability1<<17 and Busy6. `NetworkServer` grants it only when requested at protocol6+; legacy routing remains unchanged.
- `NexusMetadataSnapshot.h` reserves cached metadata with nonblocking shared topology guards in IDs→threads order, queue mutex, and shared controller access. Both queued and popped-but-active work prevent overtaking. The snapshot releases these guards before sending. `ReentrantSharedMutex.try_lock_shared` preserves recursion and cleans up failed acquisition state.
- Native `OpenRgbController` treats only negotiated Busy as a transient typed result, rejects partial enumeration and leaves the SDK connection available for frames/counts. `RgbBridge` and `OpenRgbMetadataRetry` preserve the full graph/buffers, coalesce retries at the existing3s poll and retire them on success, deactivation or session replacement. Post-resize re-fetch follows the same rule.

There are no new retry timers, worker threads, hardware queries or queues. Global timeout/reconnect policy, HID protocol, lighting settings, K55 indication and dashboard remain unchanged. Both native and headless endpoints must be deployed together to receive the capability; an old peer deliberately retains legacy behavior.

## Verification and delivery state

Source tests pass:34 headless assertions using actual controller mutex semantics;10 focused native cases;321 RGB regression cases. Added native code coverage is100% lines/90.91% branches, separately from broader pre-existing legacy gaps. Independent correctness/security review is clear. All22 pre-existing unrelated Red Shift source/test files match their initial hashes.

Matching full Windows headless build, isolated actual-binary protocol smoke and Native AOT publication pass. The915-file complete payload changes only `Nexus.exe`, `openrgb/OpenRGB-headless.exe` and `RELEASE.json`; all852 dashboard files and60 other runtime/support files match `.30`. All921 hash-pinned deployment inputs verify offline. Native SHA256 is `704fa7c4c09a803d39eb83e44f81f4669b1264d91f1cddb1763aff53397e630e`; headless SHA256 is `cb797f2128b726eaf2abf3d592a9d82e8414cdbb39f8d519e721a3f18dbe281b`. Build warnings match the14 existing `.30` messages.

The user-authorized `.31` installation completed16 November2026 at08:32:23 UTC and initialized08:32:47 UTC, preserving seven saved configurations and exact custom-effect bytes against a fresh protected stopped-state backup.915 installed payload files and72 served assets verify. A five-minute sample received9091 frames with maximum receipt gap48.673ms; a separate read-only live probe returned17 Busy6 responses among60 cached-metadata requests, slowest0.806ms. No RGB timeout/reconnect warnings occurred in the main checked window. Optional tracer setup did not produce network/HID evidence; its old target was retired through a final normal restart, with607 healthy frames in a20-second final-generation check and settings guards passing again.

The local verification separates these layers and retains a single early repeated-content episode on some cards. The user subsequently confirms keyboard/RAM/stand/motherboard normal with no freezes since the update, while mouse RGB alone is frozen and movement/clicks work. This supports isolation of the remaining mouse-specific fault. No new slow-HID duration was captured; the Razer lighting defect/driver or firmware cause remains unresolved. Final process generation remains unchanged with no RGB timeout/reconnect/close warnings through09:06:29 UTC. The remaining mouse fault was subsequently recovered and guarded in `.32`; see [the follow-up release note](razer-write-d215.32.md). This source publication includes no installer or binary release.

Reproducible source checks: headless `tests/Run-NexusMetadataTests.ps1`; native `tests/Nexus.Service.Tests/Lighting/Rgb/OpenRgbMetadataBusyTests.cs` and `RgbBridgeMetadataBusyTests.cs`. The local capture/build evidence and protected settings backups are not part of the source repository.

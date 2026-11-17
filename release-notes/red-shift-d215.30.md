# Per-device Red Shift — `.30`

Implemented and installed locally on16 November2026; source published with the `.31`/`.32` RGB fixes. No installer or binary release is published by this change.

An independent checked device-menu toggle maps eligible red/pink/peach colours to logical red through the existing shared adjustment path. It updates only its own card, preserves brightness/blackout and other devices, and remains separate from Color tuning Reset. Absent/false settings retain legacy output. Firmware-authored effects that bypass Nexus frames remain outside this transform.

The native adjustment classifies source and actual brightness-adjusted output, preserving the old gain/clamp ordering for unrelated colours. HydroShift2/SLv3 preference signatures retire cached output after a toggle. Persistence, presets and HTTP DTOs carry the optional per-device boolean. Dashboard requests retire stale profile/device scopes, including profile A→B→A.

Verification:182 native owner cases,226 web cases, TypeScript/focused lint, dashboard build and one compiled Edge/mock-service scenario passed. Native transform coverage96.67% lines/95.83% branches; numerical web coverage unavailable. Golden vectors cover18 supplied colours, hue boundaries, dimming, wire ordering and unrelated-colour preservation. The compiled browser case also passed against installed dashboard files with all APIs mocked.

Authorized `.30` installation verified915 payload files,72 served assets, seven saved configurations and exact custom-effect bytes. Later `.32` installation retained this behavior and subsequent user-confirmed Red Shift edits. Physical appearance of the full golden colour set was not measured; passing RGB vectors is distinct from perceived colour.

Owners: native `DeviceColorAdjust`, device preference/DTO/routes and HydroShift2/SLv3 writers; dashboard `useDeviceColorAdjust`, `DevicePanel` and `ZoneCard`. Regression entry points: service `DeviceRedShiftTests`/`RedShiftRoutesTests`, web lighting-page owner suites and `playwright.red-shift.config.ts`. Local saved settings/captures remain outside the source publication.

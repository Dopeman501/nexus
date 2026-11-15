# Type reactive custom colour palettes — 15 November 2026

Adds exact black (`#000000`) to the Custom preset row and lets Type reactive cycle through multiple selected colours. This requires matching `.28` native service and dashboard. Matching local installation completed 22:29:07 UTC with saved-state preservation. The user confirmed real typing colour cycles in both Ripple and Zone fade. Fresh original exact-black settings/pin/locks were restored, and final 915-payload/seven-configuration/custom-effect preservation passed 22:51:05 UTC. These notes do not create a binary release.

## Behavior and ownership

`TypeReactivePalette` and `reactionPaletteUtils` under the shared TypeReactiveEditor own numbered multi-selection, minimum one/maximum sixteen colours and explicit custom-colour Add. Both the keyboard page and Lighting modal use that editor. Other colour pickers retain their single-select behavior.

Each accepted press uses the next selected colour, in selection order, and wraps. One selected colour retains single-colour behavior. A semantic palette change or renderer reset starts at the first colour; invalid/inactive presses and render frames do not consume selections. Cursor state belongs to each renderer/card, matching existing Rainbow/Random ownership; independently partitioned cards have independent sequences.

Optional `KeyReaction.colors` contains normalized distinct `#rrggbb` values in selection order. Native `KeyReactionCatalog` inspects only the first sixteen submitted slots, drops invalid/duplicate entries and copies the list. Missing/empty/invalid-only palettes use legacy `color`; valid palettes keep canonical `color` equal to their first entry. Null is omitted on serialization, and reads/startup do not migrate saved settings. Existing epoch/projection guards, profile copies and ordered saves remain.

`KeyReactionRenderer` owns the bounded palette cursor and captured per-press RGB. Explicit palette Sparks uses the selected RGB for the full cluster; legacy Sparks and Heatmap remain unchanged. Random/Rainbow retain their behavior. Captured black K55 Over effect reactions retain the `.27` wider physical-neighbour mask. Existing additive overlap remains: earlier coloured reactions can remain visible during a black step, including briefly after changing to an all-black palette. An all-black palette reaches the verified fully dark pattern once prior coloured tails retire.

The existing generated preview remains a finite 3.2-second scripted sample (seven ordinary/ten CORE presses), so it may sample only the first colours of a longer palette. The editor displays actual output frames; live typing reaches all sixteen selections. No new transport, input source, timer, dependency or per-frame allocation.

## Verification

- Native: 234 owner cases pass; scoped line/branch coverage 96.37%/87.43%. New sanitizer and custom-colour cursor methods have complete measured line/branch coverage.
- Web: 76 owner cases and four compiled mocked-browser cases pass; typecheck, focused lint and service build pass. Numerical web coverage is unavailable because its provider is absent.
- Compiled browser coverage includes exact black, numbered selection order, custom Add and save/reopen, alongside profile/projection retirement. These are mocked service/output tests, separate from live installed API and physical keyboard results.
- Frozen native/dashboard AOT, payload, reviewed installation and physical acceptance are tracked in the [local verification record](../builds/type-reactive-palette-20261115/Verification.md) and [implementation plan](../../tasks/Completed%20Tasks/15-11-26-type-reactive-palette-e-plan.md).

Detailed verification/plan links refer to local ignored workspace records, separate from the portable source and tests; they are not prerequisites for a clean-clone build.

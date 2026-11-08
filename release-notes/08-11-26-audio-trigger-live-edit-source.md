# Audio Trigger background live editing — source checkpoint

8 November 2026. Local source commits only; no push or installation is part of this checkpoint.

The subsequent user-authorized complete `.17` integration installed this source and the initial/New draft follow-up at **06:29:57 UTC** on 8 November, together with the corrected Lighting preview. Full inclusion, package and installed/served hashes passed; settings, presets and layout contents were retained. Physical acceptance remains pending. [Combined installation evidence](../builds/combined-preview-update-20261108/Verification.md).

- Service: `3e1ac14dc02b2700e2f5ab6ce3e761837a573e94`.
- Dashboard: `fb899d6e64e61a88df3c211006478c7cbba74524`.

Selecting an existing Audio Trigger background temporarily previews it on every supported local panel surface, including while the trigger is disabled. Completed edits automatically save that selected snapshot. New drafts retain explicit Add; the background Update action is removed. Normal backgrounds, other selections and Lighting's separate Update workflow are preserved.

Native preview state uses validated expiring sessions, ordered requests, bounded storage, pre-deserialization body limits, a shared receiving rate budget and cancellable concurrent publication. The dashboard coalesces preview/save traffic and protects selected snapshots from stale list changes and record echoes.

The commit isolates this work from other uncommitted Lighting, rotation, restart and display-preview changes. Its shared editor/toolbar prerequisite is included; optional Lighting adapter changes remain in the working tree. The preview integration fixture is independently hardware-disabled.

Validation uses clean exports of the previous native/dashboard commits overlaid only with the reviewed commit files. Dashboard TypeScript/service build, 51 focused tests and four isolated production Edge cases passed. Native offline Release/symbol builds and all 56 preview/hub tests passed; scoped coverage is 93.55% lines/89.25% branches, including 100% coverage of the receiving limiter. No real service or hardware was used for browser/native checks.

Dependency manifests are unchanged. The current online npm audit reports the existing 29 advisories (2 low, 14 moderate, 12 high, 1 critical). The critical proxy-addr advisory requires an Express trust-subnet configuration absent from `server.js`; this native preview path does not use Express. No dependency-clean claim is made.

The LF/CRLF palette fixture failure and initial/New draft preview are explicitly follow-up work requested after this commit.

Detailed local evidence: `builds/audio-trigger-live-edit-20261108/Verification.md` and its `commit-scope/` packet manifests/build logs. Workspace-root task, knowledge and action records remain outside this Git checkout.

## Initial/New draft preview and palette-test follow-up

Dashboard `d97570dfe1f663834b777512fbec51d5e1747f9c` commits initial/New draft preview before Add and normalizes LF/CRLF/CR in the raw stylesheet test. New drafts remain unsaved until Add; closing discards them. Remote-list invalidation and existing-entry automatic saves remain protected.

All 1,022 web owner tests and eight production-browser cases passed during implementation, plus the unsaved shader/media case. The exact four-file commit packet separately passed 52 focused tests and its TypeScript/service build before commit. Existing dependency audit debt remains unchanged. Other working changes remain uncommitted; nothing was pushed or installed by this commit operation.

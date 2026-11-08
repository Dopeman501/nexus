# Audio Trigger background live editing — source checkpoint

8 November 2026. Local source commits only; no push or installation is part of this checkpoint.

- Service: `3e1ac14dc02b2700e2f5ab6ce3e761837a573e94`.
- Dashboard: `fb899d6e64e61a88df3c211006478c7cbba74524`.

Selecting an existing Audio Trigger background temporarily previews it on every supported local panel surface, including while the trigger is disabled. Completed edits automatically save that selected snapshot. New drafts retain explicit Add; the background Update action is removed. Normal backgrounds, other selections and Lighting's separate Update workflow are preserved.

Native preview state uses validated expiring sessions, ordered requests, bounded storage, pre-deserialization body limits, a shared receiving rate budget and cancellable concurrent publication. The dashboard coalesces preview/save traffic and protects selected snapshots from stale list changes and record echoes.

The commit isolates this work from other uncommitted Lighting, rotation, restart and display-preview changes. Its shared editor/toolbar prerequisite is included; optional Lighting adapter changes remain in the working tree. The preview integration fixture is independently hardware-disabled.

Validation uses clean exports of the previous native/dashboard commits overlaid only with the reviewed commit files. Dashboard TypeScript/service build, 51 focused tests and four isolated production Edge cases passed. Native offline Release/symbol builds and all 56 preview/hub tests passed; scoped coverage is 93.55% lines/89.25% branches, including 100% coverage of the receiving limiter. No real service or hardware was used for browser/native checks.

Dependency manifests are unchanged. The current online npm audit reports the existing 29 advisories (2 low, 14 moderate, 12 high, 1 critical). The critical proxy-addr advisory requires an Express trust-subnet configuration absent from `server.js`; this native preview path does not use Express. No dependency-clean claim is made.

The LF/CRLF palette fixture failure and initial/New draft preview are explicitly follow-up work requested after this commit.

Detailed local evidence: `builds/audio-trigger-live-edit-20261108/Verification.md` and its `commit-scope/` packet manifests/build logs. Workspace-root task, knowledge and action records remain outside this Git checkout.

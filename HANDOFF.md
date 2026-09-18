# Messenger Remote Control v0.2.1 handoff

## Stable release policy — 2026-09-18

The user requested regular Releases rather than pre-releases. CI now accepts the exact `v<source-version>` tag and publishes a stable Release marked Latest. The v0.2.1 application behavior and compatibility are unchanged; this update changes delivery only. Existing RC tags/assets remain historical and are not overwritten.

Published [v0.2.1 stable Release](https://github.com/yunhyok/Messenger-Remote-Control/releases/tag/v0.2.1) through [CI run 35288794647](https://github.com/yunhyok/Messenger-Remote-Control/actions/runs/35288794647), source/tag `b422307386a071d8a01606d6103f4badf6fddcdc`. Both builds, actual EXE self-tests and both installer install/same-version repair/uninstall/configuration-preservation checks passed on Windows Server 2025. The public API confirms `prerelease=false`, `draft=false` and Latest points to v0.2.1. All five assets were downloaded anonymously; GitHub digests, sizes, SHA256SUMS, exact ZIP entries, product/file versions, embedded source commit and identical Slave binaries across ZIPs passed. Real Win7/Win11 and messenger field limitations below remain unchanged. Later verification-record changes are documentation only.

- [Master installer](https://github.com/yunhyok/Messenger-Remote-Control/releases/download/v0.2.1/Messenger-Remote-Control-Master-Setup-0.2.1.exe), SHA256 `A463BE21596CA4F25362B4E82B76F653345AC6F45DA31A5A4ED9009DF40A6813`.
- [Slave installer](https://github.com/yunhyok/Messenger-Remote-Control/releases/download/v0.2.1/Messenger-Remote-Control-Slave-Setup-0.2.1.exe) / [Slave ZIP](https://github.com/yunhyok/Messenger-Remote-Control/releases/download/v0.2.1/Messenger-Remote-Control-Slave-v0.2.1-win11-net48.zip), ZIP SHA256 `D12EC20A7B78DB89AA5F8B8A49650E6C87CB448DA6AB9787B0CCC69A45AC5658`.
- [Full ZIP](https://github.com/yunhyok/Messenger-Remote-Control/releases/download/v0.2.1/Messenger-Remote-Control-v0.2.1-win7-win11-net48.zip) / [SHA256SUMS.txt](https://github.com/yunhyok/Messenger-Remote-Control/releases/download/v0.2.1/SHA256SUMS.txt).

## Current update — 2026-09-17

- User-requested production notifications: a bound, locally started plain-command session sends `Master Ready`, sends the fixed processing/wait notice before a `pwrsi` or `total status` query, sends the result parts, then sends `Master Ready` again. No notice is attempted before the self-chat/window is selected and validated. Help skips the slow-query notice.
- The private Win7 SP1 v0.2.0 field log proves command recognition and a completed Slave query, followed by `SEND_WRITE_NOT_VERIFIED`: one SetValue returned, input classified OTHER, zero Send clicks. The log does not contain the draft, so the precise text difference is unproven. A separate earlier session rejected changed old history content; that guard remains intact.
- Shared send readback now treats CRLF and LF as equivalent and preserves every other character, including whitespace and numbers. It waits up to six read-only samples for provider propagation, within the existing overall send deadline. No write/click retry, broad whitespace normalization or draft overwrite is added. Private logs record lengths, CR/LF counts and a keyed fingerprint, never draft text.
- Ready and processing notices use fixed allowlisted text and one-use consent through the same guarded sender. Busy notices consume a fresh command proof and reserve its token before a Slave query. A failed notice prevents the query/rearm; cancellation and pending drafts include notice attempts. Revalidation shows WAIT, not READY. Startup baseline precedes Ready so an immediate new command is retained; notices do not count as commands.
- App/installer version is 0.2.1; the unchanged wire contract remains 0.2.0. Existing offline Slave v0.2.0 works with the new Master. Updated Slave packages are optional for this Master-only behavior fix. `total status` labels the transmitted version as protocol version.
- Both local Release builds and actual EXE self-tests passed on Windows 10; ZIP packaging passed. New checks cover CRLF/LF equivalence, changed digits/whitespace rejection, delayed readback, cancellation, exact one-use notices, pending notice drafts, and a command immediately after Ready.
- Actual KI-Messenger send success for this update remains field-unverified. The original failed draft must be reviewed/cleared by the user before a new session; the app must not clear it automatically. Transport failure cannot safely send another error notice; the local Master shows the error and stops.

Published [v0.2.1-rc1](https://github.com/yunhyok/Messenger-Remote-Control/releases/tag/v0.2.1-rc1) from immutable source/tag `6ab6c6fa39ed97cd6b53eb7d5f9c7c7ed7adc04a` via [CI run 35200668399](https://github.com/yunhyok/Messenger-Remote-Control/actions/runs/35200668399). Both build and release jobs passed. The Windows Server 2025 runner passed both EXE self-tests and both installer install/same-version repair/uninstall/metadata/shortcut/local-data checks. This is not a real Win7/Win11 field send test.
All five public assets were downloaded without authentication and verified against their GitHub digests, byte lengths, SHA256SUMS, exact ZIP entry lists, installer/EXE versions and embedded source commit. The two ZIPs contain identical Slave binaries. Later changes to this verification record are documentation only.

- [Master installer](https://github.com/yunhyok/Messenger-Remote-Control/releases/download/v0.2.1-rc1/Messenger-Remote-Control-Master-Setup-0.2.1.exe), SHA256 `751BB8AE3EBA2DCC5883FEC2057EB83FD25B0C988058A94AB4BF09B00CC22462`.
- [Full ZIP](https://github.com/yunhyok/Messenger-Remote-Control/releases/download/v0.2.1-rc1/Messenger-Remote-Control-v0.2.1-win7-win11-net48.zip) and [SHA256SUMS.txt](https://github.com/yunhyok/Messenger-Remote-Control/releases/download/v0.2.1-rc1/SHA256SUMS.txt).
- Optional [Slave installer](https://github.com/yunhyok/Messenger-Remote-Control/releases/download/v0.2.1-rc1/Messenger-Remote-Control-Slave-Setup-0.2.1.exe) / [Slave ZIP](https://github.com/yunhyok/Messenger-Remote-Control/releases/download/v0.2.1-rc1/Messenger-Remote-Control-Slave-v0.2.1-win11-net48.zip), ZIP SHA256 `7CB9E1DAF44B55E5A23BBF409AEE6F507350E042510B9C4BAA042EFF211ABA57`.

## Previous release baseline — v0.2.0

## 1. Source and scope

Independent repository: `yunhyok/Messenger-Remote-Control`, default branch `main`.
Source snapshot: `yunhyok/Remote-Control-App@1365e2c249d00b2a73634c77cd7bc82247f82405` (PowerSI v0.1.58).
Only source, build scripts and reviewed documentation were copied. Original checkout and separate HFSS worktree remain untouched.

The user approved implementation, public source publication, offline Master/Slave installers and CI pre-release `v0.2.0-rc1`. Existing PowerSI field tests are historical baseline evidence, not proof of this new mobile report path. Do not repeat those long tests.

## 2. Implemented path

- Master: whole-body command observation -> authenticated status request -> report formatter -> fresh same-command proof -> immutable multipart messages -> exact one-use send per part. Uncertain sends stop the remainder.
- Shared Link: bounded process inventory and versioned report contract, strict UTF-8/Base64 validation, per-target identity, time/source/state/code/excerpt and report partial/omitted counts.
- PS3 transmits an identity-only process inventory (no window/CPU/RAM/age metrics); Slave observers retain the original local inventory. This also prevents Pending metrics from leaving Slave.
- Slave: `SlaveForm.ReadOutputBuffer` is shared by local collection and `CollectRemoteOutput`. Each target is fixed by PID, start time and session. `ResponsiveStep` checks responsiveness before and after collection stages; image inference checks again immediately before model calls.
- Direct buffer and automatic copy are preferred. Independent OCR failure does not discard a valid copy. Pending overrides any earlier evidence and suppresses further collection.
- Collection budget: 100 seconds, shared among remaining targets; response grace follows before the Master's 120-second request limit. Targets that were not reached remain explicit.
- Slave controls use a scrollable client area on small desktops; the final Pending/status line remains reachable after resizing. This does not alter any PowerSI window.

## 3. Product boundaries

Windows 7 SP1 Master / Windows 11 Slave; .NET Framework 4.8; visible product/version.
Existing user configuration and pairing locations remain stable.
Installers are per-user under LocalApplicationData/Programs. Only a missing Master .NET prerequisite requests elevation; neither installer downloads runtime files during installation.
Loopback LM Studio only; no cloud, automatic model loading or server launch.
No target window resizing, restoring, moving, automated monitoring, simulation launch/termination or arbitrary commands.
Pending is Windows nonresponse, not a general detector of PowerSI internal calculation waiting.

## 4. Local data and report rules

Images, full buffers, diagnostic ZIPs, pairing credentials and user configuration remain private and are excluded from Git/releases.
First name mention is full plus PID; repeated long names abbreviate safely within the entire report.
Pending target output contains only its name, PID and Pending.
Normal excerpts: latest five lines, at most 600 characters. Report parts: at most 1,400 characters including report ID and part count.
All target identities/statuses precede optional excerpts. CPU or elapsed time never implies completion.
Local LLM text is labeled OCR and is not trusted as a command or a verified numerical measurement.

## 5. Verification status

Verified locally on 2026-09-16, Windows 10 Pro 22H2 build 19045, installed .NET release 533325:

- Both Release builds: zero warnings and errors. Both actual EXE `--self-test` processes exited 0; stderr was empty.
- Self-tests include report codec/identity rejection, Pending short-circuit and evidence removal, empty/trailing-blank Output, LLM error handling, remaining-time allocation, full Unicode names, report reset/ordering, whole-row command rejection, fresh proof, immutable multipart consent and failure cancellation.
- Both Inno Setup 7.1.0 installers compiled. The Microsoft .NET Framework 4.8 offline redistributable was checked against SHA256 and a valid Microsoft signature before embedding. Exact release asset names/checksums and ZIP entry allowlists passed.
- Actual Master setup: per-user install, same-version reinstall/repair into the same location, installed EXE self-test, product/file-version metadata, shortcut and uninstall registration, uninstall, and configuration-preservation checks passed. No reboot was requested. Private local evidence: `work/installer-smoke-fe075bc9cb624d0c93f4df958ac95288/`.
- Setup and EXE file versions are 0.2.0.0. User-facing product version is 0.2.0.
- No existing real Master configuration files were present on this test account; preservation evidence is the isolated sentinels plus the unchanged empty set of known real settings paths.

CI verification passed on Windows Server 2025 build 26100: [run 35072841733](https://github.com/yunhyok/Messenger-Remote-Control/actions/runs/35072841733), source `abfc973d7493a5208f92564fdfd6dc4285763305`.
Both Release builds and actual EXE self-tests passed. Both generated installers passed installation, same-version repair, installed EXE self-tests, metadata/shortcut/uninstall registration, removal and local-data preservation checks. This runner had no existing real settings; isolated sentinels and the unchanged empty set of known real settings paths were verified.
CI also passed native owned-window foreground, bounded rejection, live copy/input and small-window scroll reachability checks. All four package checksums and ZIP entry allowlists passed. The release dispatch repeated these checks on the final release source and passed; delivery verification is recorded below.

Not field-verified: actual Windows 7 SP1 Master, actual Windows 11 Slave, a clean PC missing .NET 4.8 (including prerequisite UAC/reboot behavior), physically disconnected installation, and the real KI-Messenger/PowerSI mobile path. The installer has no network-download operation and the Master runtime payload is embedded, but these structural checks do not substitute for a clean offline Win7 test. Same-version repair is not evidence of upgrading from a prior released installer; this is the first installer version.
Local foreground-dependent input checks explicitly skipped when the test process lacked interactive foreground rights; the corresponding CI owned-window checks passed. These are not actual PowerSI/메신저 field passes. Do not repeat completed long PowerSI/HFSS trials.

## 6. Delivery

[v0.2.0-rc1 pre-release](https://github.com/yunhyok/Messenger-Remote-Control/releases/tag/v0.2.0-rc1) was published by [workflow_dispatch run 35073530837](https://github.com/yunhyok/Messenger-Remote-Control/actions/runs/35073530837). Both build and release jobs passed.
The immutable tag and embedded EXE source version point to `68f0368d0583ad6795e655b18eeb030c58e6be73`. Later changes to this handoff/test-guide record are documentation only; the release assets are not replaced.
The repository is Public with default branch main. All five expected assets were downloaded without authentication and verified: GitHub asset digests and sizes, four SHA256SUMS entries, exact ZIP entry allowlists, 0.2.0 setup/product versions, 0.2.0.0 EXE file versions and the embedded source commit. The Slave EXE is identical in both ZIPs.

- [Master installer](https://github.com/yunhyok/Messenger-Remote-Control/releases/download/v0.2.0-rc1/Messenger-Remote-Control-Master-Setup-0.2.0.exe)
- [Slave installer](https://github.com/yunhyok/Messenger-Remote-Control/releases/download/v0.2.0-rc1/Messenger-Remote-Control-Slave-Setup-0.2.0.exe)
- [Slave-only ZIP](https://github.com/yunhyok/Messenger-Remote-Control/releases/download/v0.2.0-rc1/Messenger-Remote-Control-Slave-v0.2.0-win11-net48.zip), SHA256 `428897904F1BADFA243A466685C07A56A29105F4E194CAB552A08C3E22EB9FC7`
- [Full ZIP](https://github.com/yunhyok/Messenger-Remote-Control/releases/download/v0.2.0-rc1/Messenger-Remote-Control-v0.2.0-win7-win11-net48.zip) and [SHA256SUMS.txt](https://github.com/yunhyok/Messenger-Remote-Control/releases/download/v0.2.0-rc1/SHA256SUMS.txt)

Publish future versions as stable `v<source-version>` Releases through the same CI workflow; never overwrite verified tags or assets. Keep private evidence, configuration and company data out of releases.
See [INSTALL.md](INSTALL.md), [SLAVE-TEST.md](SLAVE-TEST.md) and [WIN7-TEST.md](WIN7-TEST.md).

# Messenger Remote Control v0.2.0 handoff

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

Publish future candidates with a new rc tag through the same CI workflow; never overwrite this verified tag or its assets. Keep private evidence, configuration and company data out of releases.
See [INSTALL.md](INSTALL.md), [SLAVE-TEST.md](SLAVE-TEST.md) and [WIN7-TEST.md](WIN7-TEST.md).

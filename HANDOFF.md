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

CI Windows Server 2025 installation verification and public release/download verification are pending at this source checkpoint. Update this section after the CI verification run, before release dispatch.

Not field-verified: actual Windows 7 SP1 Master, actual Windows 11 Slave, a clean PC missing .NET 4.8 (including prerequisite UAC/reboot behavior), physically disconnected installation, and the real KI-Messenger/PowerSI mobile path. The installer has no network-download operation and the Master runtime payload is embedded, but these structural checks do not substitute for a clean offline Win7 test. Same-version repair is not evidence of upgrading from a prior released installer; this is the first installer version.
Foreground-dependent input checks explicitly skipped when the test process lacked interactive foreground rights. Native owned-window and pure/synthetic checks passed. Do not describe those skips as field passes or repeat completed long PowerSI/HFSS trials.

## 6. Delivery

Use CI `workflow_dispatch` with `release_tag=v0.2.0-rc1`.
Publish only the two role installers, full ZIP, Slave-only ZIP and `SHA256SUMS.txt`.
Check the public repository, main/tag source SHA, downloaded package contents, EXE versions and SHA256 before final delivery.
See [INSTALL.md](INSTALL.md), [SLAVE-TEST.md](SLAVE-TEST.md) and [WIN7-TEST.md](WIN7-TEST.md).

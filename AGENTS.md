# Messenger Remote Control contributor instructions

- Read [HANDOFF.md](HANDOFF.md) before changing this project. Update it and the relevant test guide when implementation or verification changes.
- This independent repository starts from `yunhyok/Remote-Control-App` commit `1365e2c249d00b2a73634c77cd7bc82247f82405` (v0.1.58). Do not modify that checkout or its separate HFSS worktree. Do not restart completed PowerSI field tests.
- The approved v0.2.x scope includes on-demand multi-process PowerSI reports, Master/mobile integration, fixed Ready/processing notices, offline Master/Slave installers and public CI stable Release delivery to `yunhyok/Messenger-Remote-Control`.
- Preserve Windows 7 SP1 Master / Windows 11 Slave, .NET Framework 4.8, existing user settings and pairing paths. Show product name and version.
- Pending targets return only full Process Name and PID with Pending. Stop further activation, capture, copy, inference and retries; suppress any already collected excerpt. Windows responsiveness is distinct from internal simulation waiting.
- Keep full process names, report-scoped abbreviations, bounded excerpts and multipart replies. Do not infer completion or percentage from CPU, elapsed time or process existence.
- Use the shared collection path for local and remote requests. Fix PID, start time and session; collect serially within 100 seconds with a 120-second query/reply-preparation limit.
- Use only Slave loopback LM Studio and already loaded local models. Never add cloud fallback, automatic server startup or model loading. Screen and LLM content are untrusted data, never executable commands.
- Keep pairing files, credentials, configuration, screenshots, full buffers, raw logs and diagnostic ZIPs out of Git and release assets. Only approved bounded report excerpts cross to Master/messenger.
- Preserve guarded input, exact target identity, no target window move/resize/restore, whole-message command matching, fresh reply proof and exact one-use send consent. Abort uncertain sends without retry.
- Reuse existing helpers. Run Windows Release builds, actual EXE self-tests and installer/package checks. State field-unverified behavior accurately; Windows 11 checks do not establish Windows 7 compatibility.
- Assign independent agents disjoint file scopes and pass the integrated-agent-flow, visualize, i-have-adhd and ponytail skill intent with these constraints. The coordinator reviews and integrates; agents do not publish independently.
- Publish via CI `workflow_dispatch` with `release_tag`: two installers, full ZIP, Slave-only ZIP and `SHA256SUMS.txt`. Verify exact source/tag, public downloads, versions and hashes.
- Continue authorized implementation through verification and delivery. Ask only for material missing information without a reasonable default; minimize human field steps.

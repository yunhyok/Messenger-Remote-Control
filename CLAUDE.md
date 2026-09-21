# Claude review entry point

Read [AGENTS.md](AGENTS.md) and [HANDOFF.md](HANDOFF.md) before reviewing this repository. HANDOFF.md is the current source map, behavior contract, verification record and review checklist; `docs/history/` contains superseded, dated history.

The current request is to prepare an independent review of Messenger Remote Control v0.3.3. Begin with a source review and report evidence-backed findings with severity, file/line or function, trigger, impact, minimal correction and regression check. Distinguish verified defects from hypotheses and field-unverified behavior. Follow any later explicit user instructions about implementation.

- Work in `Messenger-Remote-Control`; do not modify the original `Remote-Control-App` checkout or HFSS work.
- Use public tracked source/documentation for review. Do not inspect or upload ignored `work/`, user configuration, pairing files, raw logs, screenshots, diagnostic bundles or company Output without a separate explicit request covering those materials.
- Do not send real messenger messages, operate PowerSI/HFSS, install software or publish a release as part of source review. HANDOFF.md describes the existing checks and when each applies.
- Reuse the existing helpers, preserve the Win7 Master / Win11 Slave and .NET Framework 4.8 boundaries, and keep Pending, exact-command, fresh-send and output-history rules intact.

No Codex plugin or machine-specific skill path is required to understand this project. The workflow principles are summarized in HANDOFF.md.

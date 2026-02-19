# Changelog

## 2026-02-19

Summary:

- Unified mutable runtime state under `hidcontrol.state.json` (profiles, active profile, room-profile bindings).
- Added storage observability and authority diagnostics (`/status/storage`, storage events).
- Hardened WebRTC room lifecycle (rotation/binding cleanup regressions).
- Finalized watchdog skip policy (`watchdog_skip_reason`) and suppression tests.
- Stabilized audio health/probe UX edge-cases.
- Formalized legacy config/storage deprecations in API responses and docs.

Detailed notes:

- `Docs/RELEASE_NOTES_2026-02-19.md`

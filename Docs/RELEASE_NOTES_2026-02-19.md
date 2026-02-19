# Release Notes (2026-02-19)

Date: 2026-02-19

## Scope

- storage authority migration to state-store
- observability for storage and room lifecycle
- room lifecycle hardening
- watchdog restart policy finalization
- audio health/probe UX stabilization
- API/config deprecation pass

## What Changed

### 1) Storage authority (state-store first)

- Added unified mutable state store: `hidcontrol.state.json`
- Runtime authority moved to state-store for:
  - `videoProfiles`
  - `activeVideoProfile`
  - `roomProfileBindings`
- Added startup bootstrap logic:
  - first run seeds state-store from config
  - subsequent runs treat state-store as source of truth

Main commits:

- `0e0466f` Introduce unified JSON state store and storage status endpoint
- `4050572` Add state-store observability status and lifecycle logs

### 2) Storage observability

- `/status/storage` extended with:
  - `schemaVersion`
  - `fileSizeBytes`
  - `lastWriteUtc`
  - `authoritativeSource`
- Added storage events:
  - `state_store_load`
  - `state_store_save_profiles`
  - `state_store_save_bindings`
  - `state_store_recover_fallback`

Main commit:

- `4050572` Add state-store observability status and lifecycle logs

### 3) Room lifecycle hardening

- Added integration regressions for:
  - known video room visibility after helper rotation
  - room-profile binding removal on room delete

Main commit:

- `dee24a3` Add room lifecycle regression tests for rotation and binding cleanup

### 4) Watchdog policy finalization

- Finalized restart suppression policy:
  - suppress restart for capture `LastExitCode = -22`
  - skip restart when source is manual-stop
  - skip restart when no output modes are enabled (roomless/idle)
- Added `watchdog_skip_reason` diagnostics with debounce

Main commit:

- `8f6574a` Finalize watchdog skip policy and add restart suppression tests

### 5) Audio stability / UX closeout

- Fixed audio health classification edge case:
  - `audio-level ~0` with measured bitrate now classified as `noise-only` (not `silence`)
- Stabilized audio probe file link path:
  - strict `probeId` validation server-side to avoid stale/race links

Main commit:

- `bf7fc44` Stabilize audio health classification and probe file linking

### 6) API/config deprecation pass

- `/config` and `/stats` now expose formal deprecation metadata:
  - `MouseMappingDb`
  - `MigrateSqliteToPg`
  - config-bound `videoProfiles` / `activeVideoProfile`
- Added authoritative source map and replacement/removal hints
- Updated server README with storage authority/deprecation section

Main commit:

- `48564da` Document authoritative storage and formalize config deprecations

### 7) Related storage migration fixups

- SQLite import marker path handling fixed
- marker artifacts added to `.gitignore`

Main commits:

- `e45c9d6` Fix one-shot SQLite import marker path handling
- `82f3bf8` Ignore SQLite import marker artifacts

## Migration Notes

1. Keep `hidcontrol.state.json` alongside server runtime files.
2. On first run without state file, config values seed state-store.
3. After first successful bootstrap:
   - profile and room-binding runtime writes go to state-store
   - changing `videoProfiles`/`activeVideoProfile` in config does not override runtime mutable state
4. Use these endpoints to validate authority:
   - `GET /config`
   - `GET /status/storage`

## Rollback Plan

### Fast rollback (recommended)

1. Roll back to a commit before `2026-02-19` release changes.
2. Restore previous config/db artifacts from backup.
3. Remove or archive `hidcontrol.state.json` if returning to legacy runtime behavior.

### Partial rollback by scope

- Roll back watchdog policy only:
  - revert commit `8f6574a`
- Roll back audio health/probe link changes only:
  - revert commit `bf7fc44`
- Roll back API deprecation metadata/doc only:
  - revert commit `48564da`
- Roll back state-store authority/observability:
  - revert commits `0e0466f` and `4050572`

## Validation Summary

Automated checks completed in this cycle:

- `HidControlServer.Tests`: PASS (`84/84`)

Environment-limited checks:

- Go tests not executed in this shell (`go` tool unavailable)
- Web build in this sandbox intermittently fails without compiler diagnostics (environment issue)

Recommended release gate run on dev host:

- `.\run_all_tests.ps1`
- manual acceptance matrix:
  - Room Create/Connect/Hangup/Restart/Delete
  - Profile Select/Clone/Delete
  - Audio On/Off + Probe + health hint
  - Rooms list stability after helper rotation
  - `/status/storage` correctness

# EXP-022 Backlog (P0/P1/P2) with Estimates

Source: `Docs/EXP022_SRS_IMPLEMENTATION_PLAN_EN.md`, `Docs/EXP022_SRS_IMPLEMENTATION_PLAN_UA.md`  
Estimate unit: **engineering days** (effective implementation days, excluding waiting/approval delays).

## P0 — Critical (Must for usable release)

| ID | Task | Estimate (days) | Dependencies | Done Criteria |
|---|---|---:|---|---|
| P0-1 | Freeze baseline profiles/scripts and capture KPI baseline | 1 | None | Baseline runbook + metrics file committed |
| P0-2 | Split HID logic from `exp-022` into dedicated HID service process | 3 | P0-1 | HID service runs independently, `exp-022` consumes it |
| P0-3 | Stabilize WS control contract (`id/ok/error`, reconnect, timeout handling) | 2 | P0-2 | No hanging requests; deterministic ACK/NACK behavior |
| P0-4 | Complete UART error taxonomy mapping (0x01..0x04, timeout, HMAC, busy) | 1.5 | P0-2 | UI and logs show normalized error classes |
| P0-5 | Implement descriptor fallback (`0x04`) when layout (`0x05`) is missing/incomplete | 4 | P0-2 | Mouse/keyboard actions work on non-boot/report-id devices |
| P0-6 | Report build correctness: report-id, signed/unsigned fields, variable report length | 2.5 | P0-5 | Scripted HID semantic tests >= 99% pass |
| P0-7 | Unified diagnostics endpoints (`health/interfaces/layouts/last-reports`) | 2 | P0-2 | Endpoints return valid state for active session |
| P0-8 | AV profile baseline hardening (`balanced`, `low-latency`, `stability`) | 3 | P0-1 | Three profiles reproducible via config/switch |
| P0-9 | 30-minute stability gate (no freeze >1s, no recurring audio dropouts) | 2 | P0-8 | Gate report passed 3 consecutive runs |

**P0 subtotal:** ~21 days

## P1 — Important (Required for strong production readiness)

| ID | Task | Estimate (days) | Dependencies | Done Criteria |
|---|---|---:|---|---|
| P1-1 | Runtime profile switching without process restart (safe transition) | 3 | P0-8 | Switch profiles in-session without stream break |
| P1-2 | AV+HID unified timeline with correlation IDs | 2.5 | P0-7 | Single chronological diagnostic view available |
| P1-3 | Automated latency sampler (clock capture helper + summary) | 2 | P0-8 | Avg/P95 latency exported per run |
| P1-4 | Backpressure policy tuning (queue thresholds, drop strategy by command type) | 2 | P0-3 | Mouse move drops controlled; click/keys preserved |
| P1-5 | HID mapping packs (keyboard layout mapping, optional per-target profiles) | 2.5 | P0-6 | Mapping selection configurable, validated on target |
| P1-6 | Recovery tests (service restart, network blip, temporary COM fault) | 2 | P0-7 | Auto-recovery behavior documented and validated |
| P1-7 | Operator diagnostics export bundle (1-click JSON/log package) | 1.5 | P0-7 | Export used for root-cause in <5 minutes |

**P1 subtotal:** ~15.5 days

## P2 — Optimization (Post-RC improvements)

| ID | Task | Estimate (days) | Dependencies | Done Criteria |
|---|---|---:|---|---|
| P2-1 | Adaptive AV policy (dynamic profile recommendation from telemetry) | 4 | P1-2, P1-3 | Profile suggestions reduce freeze rate in tests |
| P2-2 | Advanced quality controls (content-aware bitrate/GOP presets) | 3 | P0-8 | Better quality score at same latency envelope |
| P2-3 | Extended HID macro layer (safe shortcut/macro templates) | 2.5 | P1-5 | Macro actions validated and rate-limited |
| P2-4 | Extended compatibility matrix (additional target OS/device variants) | 3 | P0-6 | Matrix report with pass/fail by variant |
| P2-5 | Performance optimizations (CPU/GPU usage alerts + guardrails) | 2 | P1-2 | Resource spikes detected and surfaced |

**P2 subtotal:** ~14.5 days

## Milestone Proposal

1. **M1 (P0 complete)**: ~4–5 calendar weeks  
2. **M2 (P1 complete)**: +3 calendar weeks  
3. **M3 (P2 complete)**: +2–3 calendar weeks

(Assuming 1 primary engineer + part-time QA. Parallelization can reduce wall time.)

## Immediate Next Sprint (Recommended)

1. P0-2 (service split)
2. P0-5 (descriptor fallback)
3. P0-6 (report correctness)
4. P0-7 (diagnostics endpoints)
5. P0-9 (stability gate setup)

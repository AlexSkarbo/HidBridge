# EXP-022 Next-Gen Remote Control Platform
## Software Requirements Specification + Step-by-Step Implementation Plan (EN)

Version: `0.1-draft`  
Date: `2026-02-25`

---

## 1. Introduction

### 1.1 Purpose
Define a production-oriented solution derived from EXP-022 to deliver:
- low-latency video
- stable audio
- real-time mouse/keyboard control
- measurable and repeatable quality/latency outcomes

This document follows a Wiegers-style structure: business requirements, user requirements, functional requirements, quality attributes, interfaces, constraints, verification, and rollout plan.

### 1.2 Scope
System topology:

`Browser Client <-> Control/Media Services <-> Capture + RP2040 HID Bridge <-> Target PC`

Out of scope:
- cloud multi-tenant SaaS
- account management/identity platform
- mobile native apps

### 1.3 Definitions
- E2E latency: visual latency measured by synced clock overlay.
- HID semantic success: target device performs intended action (not only UART ACK).
- Profile: predefined AV encoder/transport settings.

---

## 2. Business Requirements

### BR-1: Usability
Operator can control target PC remotely with acceptable responsiveness.

### BR-2: Stability
Session remains usable for long runs (>=30 minutes) without major freezes.

### BR-3: Quality
1080p baseline quality under low-latency constraints.

### BR-4: Diagnosability
Engineering can isolate failures quickly with telemetry and logs.

### Success Metrics
- Median visual latency on LAN <= 200 ms (target baseline)
- P95 latency <= 300 ms
- Freeze >1s: 0 per 30 min run
- HID semantic success >= 99% in scripted validation

---

## 3. Stakeholders and User Classes

### Stakeholders
- Product owner / system owner
- Streaming engineer
- Firmware/HID engineer
- QA/test operator

### User Classes
- Operator: starts sessions, uses keyboard/mouse control.
- QA engineer: runs matrix tests and verifies KPIs.
- Developer: debugs transport, report formats, and tuning profiles.

---

## 4. User Requirements (Use Cases)

### UC-1 Start Session
1. Operator starts HID service and AV publish.
2. Operator opens web monitor.
3. Operator presses Play and Connect HID.
4. Operator arms input and controls target.

### UC-2 Low-Latency Validation
1. Operator opens clock overlay on target and client.
2. System computes visual lag samples.
3. Operator compares profile A/B/C.

### UC-3 Fault Diagnosis
1. Operator notices freeze/no-input.
2. Opens diagnostics panel/endpoints.
3. Identifies category: AV pipeline / transport / HID semantic mismatch.

---

## 5. Functional Requirements

## 5.1 AV Service
- FR-AV-1: Accept capture input (video+audio), publish via WHIP.
- FR-AV-2: Provide selectable profiles (`balanced`, `low-latency`, `stability`).
- FR-AV-3: Expose runtime stats (fps, keyframe interval, rtt, nack/pli, drops).
- FR-AV-4: Support safe profile switch without process restart (preferred).

## 5.2 HID Service
- FR-HID-1: Accept control commands via WS (`/ws/control`).
- FR-HID-2: Map control events to UART HID inject commands.
- FR-HID-3: Resolve interface selectors (`0xFF mouse`, `0xFE keyboard`).
- FR-HID-4: Build reports using `GET_REPORT_LAYOUT (0x05)`.
- FR-HID-5: Fallback to `GET_REPORT_DESC (0x04)` parser when layout unavailable/incomplete.
- FR-HID-6: Return command-level ACK/NACK to client with error classification.

## 5.3 Web Client / Monitor
- FR-UI-1: Play WHEP stream.
- FR-UI-2: Connect/disconnect HID control channel.
- FR-UI-3: Pointer lock and keyboard capture with explicit arm/disarm.
- FR-UI-4: Show live status: connected, armed, errors, last ACK.
- FR-UI-5: Show AV/HID diagnostics summary.

## 5.4 Diagnostics API
- FR-DIAG-1: `/health` for AV and HID services.
- FR-DIAG-2: `/diag/interfaces` with mounted/active HID interfaces.
- FR-DIAG-3: `/diag/layouts` source and parsed details (layout/descriptor).
- FR-DIAG-4: `/diag/reports/last` with recent injected report hex + decoded fields.
- FR-DIAG-5: Counters: commands, acks, errors, timeouts, retries.

---

## 6. Quality Attributes (Nonfunctional Requirements)

### NFR-LAT-1 (Latency)
- Maintain target envelope under LAN baseline profile.

### NFR-STAB-1 (Stability)
- No freeze >1s in 30-minute tests.
- Auto-recovery strategy for transient transport faults.

### NFR-REL-1 (Reliability)
- Deterministic retries/timeouts/backpressure policy.

### NFR-OBS-1 (Observability)
- Structured logs with correlation IDs.
- Unified timeline for AV + HID events.

### NFR-SEC-1 (Security)
- HMAC-protected UART control protocol.
- Optional origin/token checks for WS control endpoint.

---

## 7. Constraints and Assumptions

### Constraints
- Existing firmware UART protocol (SLIP + CRC + HMAC) is authoritative.
- RP2040 hardware bridge remains unchanged initially.
- Current deployment is local LAN.

### Assumptions
- Capture device can deliver stable frame cadence.
- Target PC accepts standard HID usage tables.

---

## 8. External Interface Requirements

## 8.1 UART Protocol
- Command set includes inject/list/layout/descriptor/device-id.
- Error codes must be surfaced to UI/operator.

## 8.2 WebSocket Control Contract
- Mouse: move, wheel, button down/up.
- Keyboard: text, usage press, shortcut.
- Response: `{id, ok, error}`.

## 8.3 WHEP/WHIP
- AV transport through SRS endpoints with negotiated codecs.

---

## 9. Step-by-Step Implementation Plan

## Phase 0 — Baseline Freeze (1-2 days)
1. Freeze current EXP-022 commits/settings.
2. Record baseline KPIs from current profile.
3. Create issue tracker labels: `av`, `hid`, `latency`, `quality`, `diag`.

Deliverable: baseline report and reproducible startup scripts.

## Phase 1 — Service Split (3-5 days)
1. Extract HID logic from exp-022 into dedicated service project.
2. Keep exp-022 as integration harness (AV + UI only).
3. Define internal contracts between UI and HID service.

Deliverable: independent HID process with WS endpoint and UART pipeline.

## Phase 2 — HID Semantic Completion (4-7 days)
1. Complete layout parser compatibility.
2. Add descriptor fallback path for non-boot/report-id variants.
3. Add per-interface report cache and invalidation.
4. Implement strict error taxonomy in responses.

Deliverable: reliable mouse/keyboard semantics across known devices.

## Phase 3 — AV Tuning Framework (4-6 days)
1. Implement profile registry (A/B/C + runtime switch).
2. Add AV telemetry endpoints and periodic snapshots.
3. Add automated latency sample collector.

Deliverable: measurable tuning loop with profile diffs.

## Phase 4 — Unified Diagnostics (3-4 days)
1. Add merged timeline (AV + HID).
2. Add report decode logging (sampled).
3. Add one-click diagnostic export.

Deliverable: “5-minute root cause” support package.

## Phase 5 — Validation and Hardening (5-8 days)
1. Run compatibility matrix (30-min runs/profile).
2. Fix top regressions.
3. Lock production default profile.

Deliverable: release candidate with KPI evidence.

---

## 10. Verification and Acceptance

### Test Suites
- TS-1 HID semantic tests (scripted)
- TS-2 AV continuity tests
- TS-3 Latency benchmark tests
- TS-4 Endurance tests (30-60 min)

### Acceptance Gates
- AG-1 All P0/P1 defects closed.
- AG-2 KPI targets met in 3 consecutive runs.
- AG-3 Recovery behavior validated (service restart/network blips).

---

## 11. Risks and Mitigations

- R-1 Capture device jitter -> Mitigation: conservative profile fallback.
- R-2 Descriptor edge cases -> Mitigation: descriptor parser + raw inject debug.
- R-3 CPU overload -> Mitigation: profile caps + telemetry alarms.
- R-4 Hidden packet loss -> Mitigation: expose NACK/PLI/RTT and adaptive profile.

---

## 12. Release Strategy

1. Internal beta in LAN lab.
2. Controlled pilot on target workstation set.
3. Production profile lock + change-control for tuning.

---

## 13. Traceability Matrix (Compact)

| Business Req | Functional/NFR |
|---|---|
| BR-1 Usability | FR-HID-1..6, FR-UI-1..5 |
| BR-2 Stability | FR-AV-3..4, NFR-STAB-1 |
| BR-3 Quality | FR-AV-1..4, NFR-LAT-1 |
| BR-4 Diagnosability | FR-DIAG-1..5, NFR-OBS-1 |


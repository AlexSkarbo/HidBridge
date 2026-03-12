# HID Control Migration Plan (From `exp-022` to Production Service)

## 1) Current Status

The `exp-022-datachanneldotnet` experiment proved that the full control path works:

- Browser (WebRTC client) -> WebSocket control
- `exp-022` process -> UART (`COM6`, `3,000,000`)
- RP2040 B_host -> RP2040 A_device
- USB HID to Target PC

Video/audio streaming is operational.  
Mouse/keyboard input is now partially operational (not yet fully deterministic across all report formats/devices).

## 2) Why We Should Move Out of `exp`

`exp-022` currently mixes:

- AV streaming experiment logic
- Control-plane experiments
- HID transport + HID report building

This makes maintenance and validation harder.  
Production HID behavior needs a dedicated service with strict diagnostics, compatibility matrix, and stable API contracts.

## 3) Target Architecture

Split responsibilities:

- `exp-022`: AV-only test bench (WHEP/WHIP playback/capture, latency checks)
- New service (`HidBridgeControlService` or evolved `HidControlServer`): HID control only

Data path (target):

- Browser -> WebSocket (or DataChannel gateway) -> HID service -> UART protocol -> RP2040 bridge -> Target PC

## 4) Mandatory Functional Scope for the New HID Service

### Transport & Protocol

- Stable UART framing (SLIP + CRC + HMAC)
- Interface resolution (`0xFF` mouse selector, `0xFE` keyboard selector)
- Timeouts/retries/backpressure controls

### HID Report Generation

- Primary source: `GET_REPORT_LAYOUT (0x05)`
- Mandatory fallback: `GET_REPORT_DESC (0x04)` + descriptor parser
- Correct handling of:
  - Report IDs
  - Boot vs report protocol
  - Signed/unsigned axes and fields
  - Variable report sizes per interface

### Input Semantics

- Mouse: move, wheel, buttons (`left/right/middle/back/forward`)
- Keyboard: text, shortcuts, raw usage+modifiers, key down/up

## 5) API & Observability Requirements

Expose explicit diagnostics:

- `health` (transport, UART state)
- active interface snapshot (`LIST_INTERFACES`)
- layout source per interface (`layout` vs `descriptor`)
- last injected reports (hex + decoded fields)
- command/ack/error counters

This is required to separate:

- “transport works” vs
- “semantic HID injection is correct”

## 6) Test & Validation Matrix (Production Gate)

Minimum acceptance:

- 99%+ successful semantic injection in scripted scenarios.

Matrix:

- Mouse: move/wheel/buttons + press/release/click
- Keyboard: ASCII text, shortcuts (Ctrl/Alt/Shift/Meta), raw usages
- OS targets: at least Windows baseline
- Different HID descriptor/report-id variants
- Long-run stability (30+ min continuous operation)

## 7) Migration Steps

1. Freeze `exp-022` HID changes (feature freeze).
2. Create new HID service project (or branch from `Tools/Server/HidControlServer`).
3. Move UART + report-building logic into dedicated modules.
4. Add descriptor fallback path and layout cache.
5. Implement diagnostics endpoints and structured logs.
6. Run compatibility matrix and fix semantic mismatches.
7. Keep `exp-022` as AV test harness using the new HID service endpoint.

## 8) Definition of Done

Migration is complete when:

- `exp-022` no longer owns HID business logic.
- New HID service passes the compatibility matrix.
- Input behavior is reproducible across restarts and long runs.
- On-call diagnostics can identify failures in <5 minutes.

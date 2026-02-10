# Roadmap (HIDden Bridge / HidBridge)

This roadmap is a living plan for getting from the current state (control MVP + legacy video options) to a product-grade, clean-architecture codebase with WebRTC-based real-time KVM workflows.

## Current Status (Checkpoint)

**Control MVP (WebRTC DataChannel)**
- Works in modern browsers (Chrome/Edge/Firefox/Opera) for **control-plane** messaging: keyboard/mouse JSON over a WebRTC DataChannel.
- Target/controlled device remains **agentless**: no software is installed on the controlled PC.
- `HidControlServer` provides signaling and spawns/monitors a helper peer (`Tools/WebRtcControlPeer`) that bridges DataChannel JSON <-> `/ws/hid`.

**Video (legacy options)**
- FLV/HLS/MJPEG are still supported (FFmpeg driven). This is *not* the long-term plan; real-time target is WebRTC.

## MVP Must-Haves (Product Checklist)

1. **Keyboard shortcuts**
   - Support general shortcut parsing on the controller side.
   - Deliver as HID key sequences to the controlled device (via the bridge).
   - Document limits for i18n text input and why it is not universal (OS/layout dependent).
2. **Firmware deck**
   - Keep all firmware projects under `Firmware/`.
   - Keep all tooling under `Tools/`.
3. **Public repository**
   - Repo is public, with clear proprietary/commercial licensing.
4. **WebRTC**
   - Phase A: control-plane (DataChannel) = DONE (MVP).
   - Phase B: real-time video (WebRTC media) = next major milestone.
5. **RP2040-Zero A/B firmware**
   - A/B slots (dual image) for safe updates.
6. **NeoPixel support**
   - LED status for A and B.
7. **PICO-PIO-USB support**
   - Add PIO USB where needed (boards without native USB host, etc.).
8. **Waveshare-RP2350-USB-A firmware**
   - PIO-USB + NeoPixel.
9. **RP2040-Zero firmware with PIO-USB + NeoPixel**

## Clean Architecture Refactor Plan (Tools/HidControlServer)

### Step 0 — Inventory
- Map modules: `Program.cs`, FFmpeg/video pipeline, endpoints, Razor pages.
- Identify core scenarios: start/stop stream, switch profile, diagnostics, flv/ws/hls/mjpeg, HID injection.

### Step 1 — Shared Contracts + Client SDK
- `Tools/Shared/HidControl.Contracts`: DTOs/enums (Video/Diag/Profile, etc.)
- `Tools/Shared/HidControl.ClientSdk`: HttpClient wrapper + WS client + reconnect/retry.

### Step 2 — Break up “God classes”
- Extract services: ffmpeg process manager, profile service, capture lock, diagnostics, helpers.

### Step 3 — Application Layer
- Use-cases: Start/Stop stream, Switch profile, Get diag, Get profiles.
- Ports: `IStreamSupervisor`, `IProfileStore`, `IFfmpegHost`, `IClock`, etc.

### Step 4 — Infrastructure Layer
- Implement ports: ffmpeg host, capture lock, profile store, OS/IO/process implementations.
- Keep OS-specific concerns here.

### Step 5 — API/Endpoints become thin
- Endpoints should call Application use-cases only.
- Start with WebRTC-control endpoints first (they are new and easier to “do right”).

### Step 6 — UI Layer
- Keep legacy Razor page(s) as-is.
- Move new UI to `Tools/Clients/Web/HidControl.Web` (Blazor later; current skeleton works).
- Plan: Desktop (Avalonia) and Mobile (MAUI/other) should consume `HidControl.ClientSdk`.

### Step 7 — Cleanup
- Normalize server events/logging.
- Normalize error codes and diagnostics formats.
- Add tests where behavior is tricky (room lifecycle, helper supervision).

## WebRTC Roadmap

### Milestone 1 — “Control MVP” (DONE)
- In-memory signaling rooms.
- One controller per room (helper + one browser) via a **max 2 peers** policy.
- Room listing + room creation + helper autostart.
- TURN support (optional) for restrictive networks.

### Milestone 2 — “Control Productization” (NEXT)
- Persistent rooms (optional): store + restore, with TTL cleanup.
- Better UI room browser: connect/hangup, show “busy/idle”, show last activity.
- Config-driven timeouts (connect timeout, idle stop timeout, cleanup intervals).
- Hardware-bound room IDs: deterministic mapping to device identity where possible.

### Milestone 3 — “WebRTC Video”
- Add media tracks (video first).
- Capture pipeline to WebRTC (camera/FFmpeg -> RTP) with minimal latency.
- NAT traversal hardened: TURN required for “works anywhere”.

## Room Name Generation (Algorithm Notes)

Room IDs should not be empty and should be traceable to a specific piece of hardware:
- Base prefix: `hb-`
- Video-plane prefix: `hb-v-`
- Device identity component: derived from the bridge identity when available (e.g., a stable device ID from the UART-connected bridge MCU).
- Random suffix: short url-safe token to avoid collisions.

Example:
- `hb-<deviceIdShort>-<rand>`
- `hb-v-<deviceIdShort>-<rand>`
- Fallback: `hb-unknown-<rand>`

This is intentionally documented so we can later change implementation without forgetting the intent.

## TURN (coturn) Policy

TURN is **optional** for local LAN where direct P2P works, but **recommended** for reliability:
- Some browsers/networks produce 0 usable ICE candidates without TURN.
- Some corporate/firewalled networks block UDP; TURN over TCP/TLS can save the session.

Setup instructions live in `Docs/turn_setup.md`.

## Documentation To Keep Updated

- `Docs/webrtc_control_peer.md`: Control MVP usage + troubleshooting (`room_full`, connect timeouts).
- `Docs/turn_setup.md`: coturn Docker command + when TURN is needed.
- `Docs/text_input_i18n.md`: why “type any language in any OS” is not generally solvable with pure HID.
- Root test runner: `run_all_tests.ps1` (single command for .NET and Go tests).

## Next Steps (Do These In Order)

1. Stabilize “control rooms” behavior:
   - Enforce 1 controller per room.
   - Ensure Hangup reliably frees the room / helper session.
2. Make timeouts configurable (minimal defaults):
   - WebRTC connect timeout, signaling reconnect, idle stop.
3. Ship a small room browser UX:
   - List rooms, create room, connect, hangup, delete room.
4. Start Step 5 refactor *for WebRTC only* (thin endpoints):
   - Move room/ICE logic into Application + Infrastructure services.
5. Add minimal integration smoke tests:
   - Room lifecycle + helper supervision policy.

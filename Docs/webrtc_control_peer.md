# WebRTC Control Peer (Step 3)

Goal: use WebRTC **DataChannel** for keyboard/mouse control, while keeping video as-is.

We run a small helper next to `HidControlServer`:
- it joins `/ws/webrtc` (signaling)
- it accepts browser offers
- it opens a DataChannel and forwards messages to `/ws/hid`

This lets us test WebRTC control-plane with **Chrome/Edge/Firefox/Opera** (including mobile) without installing anything on the remote/controlled device.

## Components

- `HidControlServer` (existing): `/ws/webrtc` signaling relay + `/ws/hid` control endpoint
- `WebRtcControlPeer` (new): `Tools/WebRtcControlPeer` (Go + Pion)
- `HidControl.Web` (existing): demo UI that can create a DataChannel

## Run

Start server first, then the peer:

```powershell
cd Tools/WebRtcControlPeer
.\run.ps1 -ServerUrl "http://127.0.0.1:8080" -Room "control"
```

Then in the browser demo use room `control` and click:
- `Connect` (recommended)

When `datachannel: open` appears, messages are forwarded to `/ws/hid`.

## Notes

- **Rooms / concurrency (Control MVP):** every room allows **only 1 controller** at a time (plus the helper) = **max 2 peers**.
  - If another tab/browser tries to join the same room, it will get `room_full`.
  - To run multiple independent sessions, use different room ids (one room per session).
    - `HidControl.Web` can create a room on the server (and start a helper for it) via `Generate`.
    - `Start Helper` ensures the helper is running for the selected room (useful when you typed/pasted a room id).
    - You can also create rooms via REST: `POST /status/webrtc/rooms` (returns `room`, `pid`).
- **Room name generation (server):**
  - When you call `POST /status/webrtc/rooms` without a `room` value, the server generates a room id:
    - `hb-<deviceId>-<rand>`
    - If the UART device id is not known yet, it uses `hb-unknown-<rand>`.
  - `<deviceId>` is derived from `HidUartClient.GetDeviceIdHex()` and is currently tied to the UART-connected HID bridge MCU (typically `B_host`).
  - `<rand>` is a short random base36 suffix to avoid collisions.
  - Current implementation keeps `<deviceId>` short (first 8 hex chars) so room ids remain user-friendly.
- **Room lifecycle signal:** when a peer leaves/disconnects, signaling broadcasts `webrtc.peer_left`.
  - UI uses this to drop stale paired-peer state and make reconnect/handover behavior deterministic.
- **Room kind in signaling events:** server includes `kind` (`control` or `video`) in `webrtc.joined`, `webrtc.peer_joined`, and `webrtc.peer_left`.
  - Kind is inferred by room id convention (`video`, `video-*`, `hb-v-*` => `video`; otherwise `control`).
- STUN is required for many environments. Default: `stun:stun.l.google.com:19302`.
- Some Chromium-based browsers (Edge/Opera) may produce **0 ICE candidates** in hardened environments.
  - In that case, STUN-only cannot work and you need a TURN relay.
  - See `Docs/turn_setup.md` (coturn + TURN REST ephemeral credentials).
- `WebRtcControlPeer` requires Go 1.21+ (some Pion transport dependencies require `sync/atomic.Bool`).
- Video-plane skeleton notes: see `Docs/webrtc_video_peer.md`.

## Web UI Room Browser

`HidControl.Web` includes a small room browser UI:
- `Generate`: creates a new room on the server and starts a helper for it.
- `Start Helper`: ensures the helper is started for the currently selected room.
- Rooms table:
  - `Start`: starts the helper for that room (disabled if already started).
  - `Use`: select a room (fills the Room field).
  - `Connect`: selects the room and attempts to connect.
  - `Delete`: stops the helper for that room (not allowed for `control`).

## Web UI Control Actions

Once the status becomes `datachannel: open`, you can use the built-in controls:
- Preset keyboard shortcuts (Ctrl+C, Alt+Tab, Win+R, etc.)
- Text input (layout-dependent, limited Unicode support for now)
- Mouse move + basic clicks

All of these send JSON messages over the WebRTC DataChannel and are forwarded by the helper to `/ws/hid`.

Troubleshooting:
- `room_full`: the room already has a controller. Close the other tab/browser or generate a new room.
- `connect_timeout`: typically means NAT traversal failed. Configure TURN (`Docs/turn_setup.md`) and retry.

## Optional Auto-Start (Server)

`HidControlServer` can auto-start the helper on boot when enabled in config:

```json
{
  "webRtcControlPeerAutoStart": true,
  "webRtcControlPeerRoom": "control",
  "webRtcControlPeerStun": "stun:stun.l.google.com:19302"
}
```

## Timeouts (Config)

The WebRTC control UI uses server-provided timeouts (so they are tweakable without rebuilding the web client):

```json
{
  "webRtcClientJoinTimeoutMs": 250,
  "webRtcClientConnectTimeoutMs": 5000
}
```

Defaults are intentionally small for fast fail on LAN. Increase them if you expect slow TURN/TCP connections.

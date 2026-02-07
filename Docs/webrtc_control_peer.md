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

- **Rooms / concurrency (MVP):** the default room `control` allows **only 1 controller** at a time (plus the helper).
  - If another tab/browser tries to join, it will get `room_full`.
  - To run multiple independent sessions, use different room ids.
    - `HidControl.Web` can create a room on the server (and start a helper for it) via `Generate`.
    - You can also create rooms via REST: `POST /status/webrtc/rooms` (returns `room`, `pid`).
- **Room name generation (server):**
  - When you call `POST /status/webrtc/rooms` without a `room` value, the server generates a room id:
    - `hb-<deviceId>-<rand>`
    - If the UART device id is not known yet, it uses `hb-unknown-<rand>`.
  - `<deviceId>` is derived from `HidUartClient.GetDeviceIdHex()` and is currently tied to the UART-connected HID bridge MCU (typically `B_host`).
  - `<rand>` is a short random base36 suffix to avoid collisions.
  - Current implementation keeps `<deviceId>` short (first 8 hex chars) so room ids remain user-friendly.
- STUN is required for many environments. Default: `stun:stun.l.google.com:19302`.
- Some Chromium-based browsers (Edge/Opera) may produce **0 ICE candidates** in hardened environments.
  - In that case, STUN-only cannot work and you need a TURN relay.
  - See `Docs/turn_setup.md` (coturn + TURN REST ephemeral credentials).
- `WebRtcControlPeer` requires Go 1.21+ (some Pion transport dependencies require `sync/atomic.Bool`).

## Optional Auto-Start (Server)

`HidControlServer` can auto-start the helper on boot when enabled in config:

```json
{
  "webRtcControlPeerAutoStart": true,
  "webRtcControlPeerRoom": "control",
  "webRtcControlPeerStun": "stun:stun.l.google.com:19302"
}
```

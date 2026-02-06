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
- `Join`
- `Call`

When `datachannel: open` appears, messages are forwarded to `/ws/hid`.

## Notes

- STUN is required for many environments. Default: `stun:stun.l.google.com:19302`.
- TURN support is not included in this MVP step.
- `WebRtcControlPeer` requires Go 1.21+ (some Pion transport dependencies require `sync/atomic.Bool`).

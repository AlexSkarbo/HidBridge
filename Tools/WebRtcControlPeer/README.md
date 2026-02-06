# WebRtcControlPeer

This tool runs next to `Tools/HidControlServer` and acts as a **WebRTC control peer**:
- joins the signaling room via `HidControlServer /ws/webrtc`
- accepts browser offers and answers them
- opens a WebRTC **DataChannel** and forwards JSON messages to `HidControlServer /ws/hid`

It allows us to use WebRTC for control-plane (keyboard/mouse) **without adding a WebRTC stack to the .NET server**.

## Prereqs

- Go 1.21+ (required due to dependencies using `sync/atomic.Bool`)
- HidControlServer running and reachable
- If `HidControlServer` is configured with `Token`, provide it via `HIDBRIDGE_TOKEN`

## Run (Windows)

```powershell
cd Tools/WebRtcControlPeer
$env:HIDBRIDGE_SERVER_URL="http://127.0.0.1:8080"
$env:HIDBRIDGE_TOKEN=""
$env:HIDBRIDGE_WEBRTC_ROOM="control"
go run .
```

## Run (Linux/RPi)

```bash
cd Tools/WebRtcControlPeer
export HIDBRIDGE_SERVER_URL="http://127.0.0.1:8080"
export HIDBRIDGE_TOKEN=""
export HIDBRIDGE_WEBRTC_ROOM="control"
export HIDBRIDGE_STUN="stun:stun.l.google.com:19302"
go run .
```

Or use:

```bash
./run.sh "http://127.0.0.1:8080" "" "control"
```

## Test With Web UI

1. Start `HidControlServer`
2. Start `WebRtcControlPeer` with the same room (`HIDBRIDGE_WEBRTC_ROOM`)
3. Open `Tools/Clients/Web/HidControl.Web` and use the WebRTC demo with that room:
   - click `Join`
   - click `Call`
   - wait for `datachannel: open`
   - click `Send` (it should forward to `/ws/hid` and return the server response)

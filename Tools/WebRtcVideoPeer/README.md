# WebRtcVideoPeer (Skeleton)

This helper runs next to `HidControlServer` and acts as a **WebRTC peer for the video plane**.

Current status: **skeleton**.
- It joins a signaling room via `HidControlServer` (`/ws/webrtc`)
- It accepts offers from browser clients
- It opens/accepts a `DataChannel` and **echoes** text messages back (for debugging)

Later this tool will publish real media tracks (screen/capture -> WebRTC video).

## Run (Windows)

```powershell
.\run.ps1 -ServerUrl "http://127.0.0.1:8080" -Room "video"
```

## Run (Linux/RPi)

```bash
./run.sh "http://127.0.0.1:8080" "" "video" "stun:stun.l.google.com:19302"
```

## Environment Variables

- `HIDBRIDGE_SERVER_URL` (default: `http://127.0.0.1:8080`)
- `HIDBRIDGE_TOKEN` (optional)
- `HIDBRIDGE_WEBRTC_ROOM` (default: `video`)
- `HIDBRIDGE_STUN` (default: `stun:stun.l.google.com:19302`)


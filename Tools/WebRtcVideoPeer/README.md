# WebRtcVideoPeer

This helper runs next to `HidControlServer` and acts as a **WebRTC peer for the video plane**.

Current status:
- It joins a signaling room via `HidControlServer` (`/ws/webrtc`)
- It accepts offers from browser clients
- It publishes a synthetic VP8 video track (`ffmpeg` test source -> RTP -> WebRTC)
- It opens/accepts a `DataChannel` and echoes text messages back (debug/control)

Next step is replacing synthetic source with real capture pipeline.

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
- `HIDBRIDGE_FFMPEG` (optional, default: `ffmpeg`)

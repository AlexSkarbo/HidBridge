# WebRtcVideoPeer

This helper runs next to `HidControlServer` and acts as a **WebRTC peer for the video plane**.

Current status:
- It joins a signaling room via `HidControlServer` (`/ws/webrtc`)
- It accepts offers from browser clients
- It publishes a VP8 video track from `ffmpeg` (`testsrc` by default, `capture` optional)
- It opens/accepts a `DataChannel` and echoes text messages back (debug/control)
- If capture startup fails, it auto-falls back to `testsrc` and sends:
  - `{"type":"video.status","event":"fallback","mode":"testsrc","detail":"capture_failed"}`

Next step is replacing synthetic source with real capture pipeline.

## Run (Windows)

```powershell
.\run.ps1 -ServerUrl "http://127.0.0.1:8080" -Room "video"
```

Capture mode (default Windows device name):

```powershell
.\run.ps1 -ServerUrl "http://127.0.0.1:8080" -Room "video" -SourceMode "capture"
```

## Run (Linux/RPi)

```bash
./run.sh "http://127.0.0.1:8080" "" "video" "stun:stun.l.google.com:19302"
```

Capture mode (Linux `/dev/video0` default):

```bash
./run.sh "http://127.0.0.1:8080" "" "video" "stun:stun.l.google.com:19302" "capture"
```

## Environment Variables

- `HIDBRIDGE_SERVER_URL` (default: `http://127.0.0.1:8080`)
- `HIDBRIDGE_TOKEN` (optional)
- `HIDBRIDGE_WEBRTC_ROOM` (default: `video`)
- `HIDBRIDGE_STUN` (default: `stun:stun.l.google.com:19302`)
- `HIDBRIDGE_FFMPEG` (optional, default: `ffmpeg`)
- `HIDBRIDGE_VIDEO_SOURCE_MODE` (optional, default: `testsrc`; values: `testsrc`, `capture`)
- `HIDBRIDGE_VIDEO_QUALITY_PRESET` (optional, default: `balanced`; values: `low`, `balanced`, `high`)
- `HIDBRIDGE_VIDEO_CAPTURE_INPUT` (optional; full FFmpeg input args for capture mode, overrides OS defaults)
- `HIDBRIDGE_VIDEO_FFMPEG_ARGS` (optional; full FFmpeg pipeline args, overrides built-in mode pipeline)

Notes:
- In `capture` mode, defaults are OS-specific:
  - Windows: `-f dshow -i video="USB3.0 Video"`
  - Linux: `-f v4l2 -framerate 30 -video_size 1280x720 -i /dev/video0`
  - macOS: `-f avfoundation -framerate 30 -i 0:none`
- `HIDBRIDGE_VIDEO_FFMPEG_ARGS` should include input + codec settings; output transport (`-f rtp ...`) is appended by the helper.

# WebRTC Video Peer

Goal: real-time video via WebRTC.
Current implementation publishes a video track via FFmpeg, with configurable source mode, quality and encoder mode.

## Components

- `HidControlServer`: signaling relay (`/ws/webrtc`) + room/ICE/config endpoints under `/status/*`
- `WebRtcVideoPeer` (helper): `Tools/WebRtcVideoPeer` (Go + Pion + ffmpeg test source)
- `HidControl.Web`: demo UI (rooms browser + DataChannel tester)
- Signaling path split details: `Docs/webrtc_signaling_paths.md`

## Rooms

Video rooms are a separate namespace (recommended):

- Default video room: `video`
- Generated video rooms: `hb-v-<deviceIdShort>-<rand>`

Where:
- `<deviceIdShort>` is derived from `HidUartClient.GetDeviceIdHex()` (first 8 hex chars) and is currently tied to the UART-connected HID bridge MCU (typically `B_host`).
- If the device id is not known yet, the generator uses `unknown`.
- `<rand>` is a short random base36 suffix to avoid collisions.

## Endpoints

- List video rooms: `GET /status/webrtc/video/rooms`
- Create a video room (and start helper): `POST /status/webrtc/video/rooms`
  Optional body fields:
  - `room`: string
  - `qualityPreset`: `low|low-latency|balanced|high|optimal`
  - `bitrateKbps`: integer (`200..12000`)
  - `fps`: integer (`5..60`)
  - `captureInput`: custom ffmpeg capture input args
  - `encoder`: `auto|cpu|hw|nvenc|amf|qsv|v4l2m2m|vaapi`
- List available WebRTC encoders for current host: `GET /video/webrtc/encoders`
- Delete a video room helper: `DELETE /status/webrtc/video/rooms/{room}`
  - Note: deleting the default room `video` is blocked (`cannot_delete_video`).

Web UI note:
- In `HidControl.Web`, video quality defaults to `high` on LAN/localhost hosts.
- `high` uses more CPU and bandwidth; switch to `balanced` or `low` on weak hosts.

## Helper Auto-Start

`HidControlServer` can auto-start the video helper on boot:

```json
{
  "webRtcVideoPeerAutoStart": true,
  "webRtcVideoPeerRoom": "video",
  "webRtcVideoPeerStun": "stun:stun.l.google.com:19302",
  "webRtcVideoPeerSourceMode": "capture",
  "webRtcVideoPeerQualityPreset": "balanced",
  "webRtcVideoPeerCaptureInput": "-f dshow -i video=\"USB3.0 Video\"",
  "webRtcVideoPeerFfmpegArgs": ""
}
```

Notes:
- `webRtcVideoPeerSourceMode`: `testsrc` or `capture` (default: `testsrc`)
- `webRtcVideoPeerQualityPreset`: `low`, `low-latency`, `balanced`, `high`, `optimal` (default: `balanced`)
- `webRtcVideoPeerCaptureInput`: optional FFmpeg input args for capture mode
- `webRtcVideoPeerFfmpegArgs`: optional full FFmpeg pipeline args (overrides mode defaults)
- On helper startup, orphan `*.pid` entries are cleaned and stale helper processes are terminated.

## Current Behavior

- Helper joins room and accepts offer.
- Helper publishes video track from FFmpeg:
  - default mode: `testsrc` (synthetic generator)
  - optional mode: `capture` (real capture input)
- Encoder mode:
  - `cpu`: software encode path (codec-dependent, e.g. `libvpx` for VP8, `libx264` for H264)
  - explicit hardware modes: `nvenc`, `amf`, `qsv`, `v4l2m2m`, `vaapi`
  - `hw`: legacy alias kept for compatibility
  - `auto`: stable CPU default
- If `capture` fails to start (device busy/not found), helper falls back to `testsrc` automatically
  and sends a DataChannel status event:
  - `{"type":"video.status","event":"fallback","mode":"testsrc","detail":"capture_failed"}`
- Helper keeps DataChannel echo for control/debug payloads.
- Helper process is long-running and reconnects to signaling after transient transport failures.

## Quality Presets

Preset tuning is intentionally different for throughput-oriented low quality versus
latency-oriented mode:

| Preset | Intent | GOP | Rate control | Expected tradeoff |
| --- | --- | --- | --- | --- |
| `low-latency` | minimum interaction delay | short (`~30`) | tight (`maxrate ~1.05x`, `bufsize ~1x`) | fastest reaction, lower visual stability/quality |
| `low` | reduce CPU/bandwidth | medium (`~90`) | relaxed (`maxrate ~1.15x`, `bufsize ~3x`) | lower load, not optimized for latency |
| `balanced` | default | medium (`~60`) | standard (`maxrate ~1.2x`, `bufsize ~2x`) | balanced latency/quality/load |
| `high` | better quality at same target bitrate | medium (`~60`) | standard (`maxrate ~1.2x`, `bufsize ~2x`) | better detail, higher encode cost |
| `optimal` | max visual quality | long (`~120`) | quality-favoring (`maxrate ~1.15x`, `bufsize ~4x`) | best quality, highest latency/CPU risk |

## Video Source Configuration

`Tools/WebRtcVideoPeer` supports these environment variables:

- `HIDBRIDGE_VIDEO_SOURCE_MODE`
  - `testsrc` (default)
  - `capture`
- `HIDBRIDGE_VIDEO_CAPTURE_INPUT`
  - optional full FFmpeg input args for capture mode (overrides OS defaults)
- `HIDBRIDGE_VIDEO_QUALITY_PRESET`
  - `low`, `low-latency`, `balanced`, `high`, `optimal` for built-in encoder args
- `HIDBRIDGE_VIDEO_ENCODER`
  - `auto` (default), `cpu`, `hw`, `nvenc`, `amf`, `qsv`, `v4l2m2m`, `vaapi`
- `HIDBRIDGE_VIDEO_FFMPEG_ARGS`
  - optional full FFmpeg pipeline args (overrides mode-specific built-in pipeline)

Built-in capture defaults:

- Windows: `-f dshow -i video=USB3.0 Video`
- Linux: `-f v4l2 -framerate 30 -video_size 1280x720 -i /dev/video0`
- macOS: `-f avfoundation -framerate 30 -i 0:none`

## Optional Room Registry Persistence

Room registry persistence is optional and disabled by default in the sample config.
Control rooms can be persisted and restored. Video rooms are treated as ephemeral and are not restored from disk.

Enable persistence explicitly with:

```json
{
  "webRtcRoomsPersistEnabled": true,
  "webRtcRoomsStorePath": "webrtc_rooms.json",
  "webRtcRoomsPersistTtlSeconds": 86400
}
```

## Current Limitations

- Hardware encoder availability depends on OS/driver/FFmpeg build.
- Capture-device capability limits (resolution/fps/format) still dominate final output quality.

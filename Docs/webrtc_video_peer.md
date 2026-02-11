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
- Apply video settings to an existing room (best-effort restart helper):
  - `POST /api/webrtc/video/rooms/{room}/apply`
  - Used by `HidControl.Web` button `Apply now`.

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

## Stabilization Status

Implemented:

- Server-side fallback path `capture -> testsrc` with runtime status surfaced in API/UI:
  - `fallbackUsed`
  - `lastVideoError`
  - `sourceModeRequested`
  - `sourceModeActive`
- Video room runtime status endpoint:
  - `GET /status/webrtc/video/peers/{room}`
  - includes `running`, `pid`, `startedAtUtc`, `updatedAtUtc`, `measuredFps`, `measuredKbps`, `frames`, `packets`.
- Helper lifecycle hardening:
  - early-exit probe
  - restart policy with backoff and limits
  - room cleanup reconciliation on helper stop/delete races.
- Integration test coverage for key failure/recovery paths (see `Tools/HidControlServer.Tests/WebRtcIntegrationTests.cs`):
  - helper start timeout propagation
  - room list lag versus peer/runtime snapshots
  - fallback status persistence
  - delete reconcile when backend stop reports transient failure.

Run all tests:

```powershell
.\run_all_tests.ps1
```

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

## Recommended Defaults (Latency/Quality)

For stable interactive remote control on LAN:

- Preset: `low-latency`
- Codec: `VP8`
- Encoder: `CPU (software)` unless hardware path is validated on this host
- Capture mode: real device mode with `1920x1080` and `30 fps` (or closest stable mode)
- Bitrate: `900..1500 kbps` for low-latency, `2000..3500 kbps` for visual quality focus

For visual-quality profile `optimal (1080p)`:

- Preset: `optimal`
- Codec: `H264` (better visual quality/bitrate on many paths)
- Encoder: validated hardware encoder (`nvenc`/`amf`) or `CPU` fallback
- Use `Apply now` after changes, then reconnect to verify actual result in runtime stats.

Note:
- If capture mode only provides `60 fps` but latency is unstable, explicitly choose `30 fps` capture mode.
- If software encoder path cannot keep up, reduce fps or bitrate first, then lower preset.

## Runbook

Quick API flow (copy/paste):

```bash
# 1) Create room
curl -s -X POST http://127.0.0.1:8080/status/webrtc/video/rooms \
  -H "Content-Type: application/json" \
  -d '{"room":"hb-v-local-test","qualityPreset":"low-latency","codec":"vp8","encoder":"cpu"}'
# expected: {"ok":true,"room":"hb-v-local-test","started":true|false,...}

# 2) Runtime health
curl -s http://127.0.0.1:8080/status/webrtc/video/peers/hb-v-local-test
# expected: {"ok":true,"room":"hb-v-local-test","running":true,...}

# 3) Apply settings to same room
curl -s -X POST http://127.0.0.1:8080/api/webrtc/video/rooms/hb-v-local-test/apply \
  -H "Content-Type: application/json" \
  -d '{"qualityPreset":"optimal","codec":"h264","encoder":"cpu","bitrateKbps":3000}'
# expected: {"ok":true,"room":"hb-v-local-test","started":true|false,...}

# 4) Delete room
curl -s -X DELETE http://127.0.0.1:8080/status/webrtc/video/rooms/hb-v-local-test
# expected: {"ok":true,"room":"hb-v-local-test","stopped":true|false}
```

### No video in player

1. Query runtime:
   - `GET /status/webrtc/video/peers/{room}`
2. Expected:
   - `ok=true`, `running=true`
   - `sourceModeActive` is `capture` or `testsrc`
3. If `fallbackUsed=true`, inspect `lastVideoError`.
4. Verify browser logs include:
   - `pc.track` (video)
   - `pc.connectionState=connected`
5. If `running=true` but no frame:
   - check helper log for `startup_timeout` / `no video RTP packets within ...`
   - this means FFmpeg started but RTP packets did not arrive in time.

### Device busy

Symptoms:
- FFmpeg logs contain `Could not run graph` / `device already in use`.

Actions:
1. Stop legacy capture/ffmpeg pipelines for the same source.
2. Delete stale video rooms in UI or via `DELETE /status/webrtc/video/rooms/{room}`.
3. Retry with `capture` mode.
4. On Windows, verify who holds the device and kill stale workers first.

### Socket refused / bind error

Symptoms:
- `connectex: No connection could be made...`
- Kestrel bind error on `https://localhost:55484`.

Actions:
1. Ensure only one server instance is running.
2. Free/replace occupied ports (`8080`, `55484`) and restart.
3. Re-check server startup logs before reconnecting clients.
4. On Windows, check HTTP URL ACL and excluded port ranges:
   - `netsh http show urlacl | findstr 55484`
   - `netsh int ipv4 show excludedportrange protocol=tcp`
   - `netsh int ipv6 show excludedportrange protocol=tcp`
5. If an invalid ACL exists for your app user:
   - `netsh http delete urlacl url=https://localhost:55484/`
   - then restart server (or change dev port in `launchSettings.json`).

### Helper timeout (`helper_not_ready` / `started:false,error:timeout`)

Symptoms:
- UI: `helper_not_ready` or `ensure_helper ... timeout`
- Runtime endpoint intermittently returns `timeout`

Actions:
1. Verify process was really started:
   - `GET /status/webrtc/video/rooms` (room exists, peers/hasHelper)
   - helper log file exists in `Tools/WebRtcVideoPeer/logs/`
2. Verify room runtime directly:
   - `GET /status/webrtc/video/peers/{room}`
   - if `ok=true` and `running=true`, retry connect (room-list may lag briefly).
3. If helper exits quickly:
   - inspect helper log for ffmpeg/capture errors (`device busy`, `input not found`, codec init fail).
4. If timeout repeats:
   - delete room, ensure stale processes are gone, recreate room and reconnect.

### High latency / poor image quality

1. Check runtime health:
   - `GET /status/webrtc/video/peers/{room}`
2. Compare:
   - `measuredFps` vs target capture mode
   - `measuredKbps` vs configured bitrate
3. If latency is high:
   - switch to `low-latency` preset
   - lower bitrate and/or fps
   - prefer `VP8 + CPU` as baseline
4. If quality is poor:
   - switch to `high` or `optimal`
   - move to `H264`
   - increase bitrate in steps (`+300..500 kbps`)

### Useful diagnostics

- Room list: `GET /status/webrtc/video/rooms`
- Room runtime: `GET /status/webrtc/video/peers/{room}`
- ICE config: `GET /status/webrtc/ice`
- Local tests: `.\run_all_tests.ps1`
- Runtime must-have fields when room is healthy:
  - `ok=true`
  - `running=true`
  - `sourceModeActive` set (`capture` or `testsrc`)
  - `updatedAtUtc` changes over time

Windows quick commands:

- List listeners:
  - `netstat -ano | findstr :8080`
  - `netstat -ano | findstr :55484`
- URL ACL and excluded ranges:
  - `netsh http show urlacl | findstr 55484`
  - `netsh int ipv4 show excludedportrange protocol=tcp`
  - `netsh int ipv6 show excludedportrange protocol=tcp`
- Resolve PID -> process:
  - `tasklist /FI "PID eq <pid>"`
- Find stale helper/ffmpeg processes:
  - `Get-Process webrtcvideopeer,webrtccontrolpeer,ffmpeg,powershell -ErrorAction SilentlyContinue`
- Kill stale helper quickly:
  - `taskkill /F /IM webrtcvideopeer.exe`
  - `taskkill /F /IM webrtccontrolpeer.exe`
- Probe capture modes:
  - `ffmpeg -hide_banner -f dshow -list_options true -i video="USB3.0 Video"`
- Check helper room runtime quickly:
  - `curl http://127.0.0.1:8080/status/webrtc/video/peers/<room>`

Linux quick commands:

- List listeners:
  - `ss -ltnp | rg "8080|55484"`
- Find/kill stale helpers:
  - `ps -ef | rg "webrtc(video|control)peer|ffmpeg"`
  - `pkill -f webrtcvideopeer`
  - `pkill -f webrtccontrolpeer`
- Probe capture modes:
  - `ffmpeg -hide_banner -f v4l2 -list_formats all -i /dev/video0`
- Check helper room runtime quickly:
  - `curl http://127.0.0.1:8080/status/webrtc/video/peers/<room>`

## Pre-Release Smoke Checklist

Run before each release candidate:

1. Control channel:
   - connect to `control`
   - send keyboard shortcut and mouse click
   - verify remote input works
2. Video channel:
   - create `hb-v-*` room
   - connect and confirm first frame appears
   - verify `pc.track` and `datachannel: open`
3. Settings apply:
   - change codec/encoder/preset
   - click `Apply now`
   - reconnect and confirm runtime reflects new settings
4. Failure handling:
   - force capture failure (busy/missing device)
   - verify fallback status in runtime (`fallbackUsed=true`)
5. Cleanup:
   - delete created video room
   - confirm room disappears from list
   - confirm no stale helper process for that room
6. Automated checks:
   - run `.\run_all_tests.ps1`
   - all suites green

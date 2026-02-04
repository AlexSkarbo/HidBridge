# HidControlServer (C#)

HTTP server that injects mouse/keyboard input into `B_host` via the control UART (SLIP framed) and then forwards to `A_device` as normal `PF_INPUT`.

## Requirements

- .NET SDK 8.x
- USB-UART adapter connected to `B_host` control UART pins:
  - TX (adapter) → `B_host` RX (`PROXY_CTRL_UART_RX_PIN`, default GPIO1)
  - RX (adapter) → `B_host` TX (`PROXY_CTRL_UART_TX_PIN`, default GPIO0)
  - GND ↔ GND

## Швидкий старт (простими словами)

1) Підключи USB-UART адаптер до `B_host` (TX↔RX, RX↔TX, GND↔GND).
2) Відкрий `Tools/HidControlServer/hidcontrol.config.json` (або створи з прикладу) і постав:
   - `serialPort` = твій COM порт
   - `baud` = `3000000`
   - `masterSecret` і `uartHmacKey` = те саме, що `PROXY_CTRL_HMAC_KEY` у прошивці
3) Запусти сервер:
   ```bash
   dotnet run -- --config hidcontrol.config.json
   ```
4) Перевір, що сервер живий:
   ```bash
   curl http://127.0.0.1:8080/health
   curl http://127.0.0.1:8080/uart/ping
   ```
5) Отримай список HID і розкладки:
   ```bash
   curl "http://127.0.0.1:8080/devices?includeReportDesc=true"
   curl "http://127.0.0.1:8080/keyboard/layout?itf=2"
   ```
   Якщо `/devices` повертає `keyMode=bootstrap` або `warning` — це нормально, сервер сам підхопив bootstrap.
6) Швидка перевірка миші/клави (REST):
   ```bash
   curl -X POST http://127.0.0.1:8080/mouse/move -H "Content-Type: application/json" -d "{\"dx\":50,\"dy\":0,\"itfSel\":0}"
   curl -X POST http://127.0.0.1:8080/keyboard/text -H "Content-Type: application/json" -d "{\"text\":\"REST_OK \",\"itfSel\":2}"
   ```
7) Швидка перевірка WebSocket:
   - Сторінка: `http://127.0.0.1:8080/hid/ws-test`
   - Скрипт: `.\Scripts\ws_hid_test.ps1`

Якщо миша/клава не рухаються:
- перевір `/uart/ping` (має бути `ok:true`)
- перевір `/devices?includeReportDesc=true`
- переконайся, що `masterSecret`/`uartHmacKey` збігаються з прошивкою

## Run

From `Tools/HidControlServer`:

```bash
dotnet run -- --serial COM5 --baud 3000000 --url http://127.0.0.1:8080
```

Config file (optional):

- Copy `hidcontrol.config.json.example` → `hidcontrol.config.json` and edit values
- Or pass `--config path\\to\\hidcontrol.config.json`
- `mouseTypeName` / `keyboardTypeName` can be used to bind to a specific interface type without hardcoding `itfSel`.
- `masterSecret` must match `PROXY_CTRL_HMAC_KEY` in firmware for control UART commands to work (treated as master secret for per-device derivation).

Optional flags:

- `--config hidcontrol.config.json`
- `--serialAuto true|false` (auto-detect COM port, default `false`)
- `--serialMatch "VID_XXXX&PID_YYYY"` (match substring against PNP IDs)
- `--serialMatch "device:HEX"` (probe ports by B_host device id, e.g. `device:E46350B0E32D322E`)
- `--mouseReportLen 3|4` (default `4`)
- `--injectQueueCapacity N` (default `256`)
- `--injectDropThreshold N` (default `8`, drop if queue depth >= N when `dropIfBusy=true`)
- `--injectTimeoutMs N` (default `200`)
- `--injectRetries N` (default `2`)
- `--mouseMoveTimeoutMs N` (default `50`)
- `--mouseMoveDropIfBusy true|false` (default `true`)
- `--mouseMoveAllowZero true|false` (default `false`)
- `--mouseWheelTimeoutMs N` (default `50`)
- `--mouseWheelDropIfBusy true|false` (default `true`)
- `--mouseWheelAllowZero true|false` (default `false`)
- `--keyboardInjectTimeoutMs N` (default `200`, falls back to `injectTimeoutMs`)
- `--keyboardInjectRetries N` (default `2`, falls back to `injectRetries`)
- `--masterSecret SECRET` (default `changeme-master-secret`, used for per-device HMAC derivation)
- `--mouseItfSel 0xFF` (auto mouse, default) or explicit interface index `0..255`
- `--keyboardItfSel 0xFE` (auto keyboard, default) or explicit interface index `0..255`
- `--bindAll true|false` (default `false`, when true binds to `0.0.0.0` on the same port as `url`)
- `--mouseMappingDb PATH` (default `hidcontrol.db`, used only for SQLite migration)
- `--pgConnectionString` (default `Host=localhost;Port=5432;Database=hidcontrol;Username=hidcontrol;Password=hidcontrol`)
- `--migrateSqliteToPg true|false` (default `false`, import mappings from SQLite into Postgres)
- `--mouseTypeName mouse|keyboard|keyboard+mouse|unknown`
- `--keyboardTypeName mouse|keyboard|keyboard+mouse|unknown`
- `--devicesAutoMuteLogs true|false` (default `true`)
- `--devicesAutoRefreshMs 0|N` (default `0` = off)
- `--devicesIncludeReportDesc true|false` (default `true`)
- `--videoRtspPort N` (default `8554`)
- `--videoSrtBasePort N` (default `9000`, per-source port is `base + index`)
- `--videoSrtLatencyMs N` (default `50`)
- `--videoRtmpPort N` (default `1935`)
- `--videoHlsDir PATH` (default `video_hls`)
- `--videoLlHls true|false` (default `true`)
- `--videoHlsSegmentSeconds N` (default `1`)
- `--videoHlsListSize N` (default `6`)
- `--videoHlsDeleteSegments true|false` (default `true`)
- `--videoLogDir PATH` (default `video_logs`)
- `--videoCaptureAutoStopFfmpeg true|false` (default `true`)
- `--videoCaptureRetryCount N` (default `3`)
- `--videoCaptureRetryDelayMs N` (default `500`)
- `--videoCaptureLockTimeoutMs N` (default `2000`)
- `--videoSecondaryCaptureEnabled true|false` (default `false`, дозволяє другий ffmpeg для `/video/mjpeg` та `/video/snapshot` поверх RTSP)
- `--videoKillOrphanFfmpeg true|false` (default `true`, прибиває "зайві" ffmpeg що тримають той самий source)
- `--videoFlvBufferBytes N` (default `262144`, FLV tag buffer size)
- `--videoFlvReadBufferBytes N` (default `65536`, FLV stream read buffer size)
- `--videoMjpegReadBufferBytes N` (default `16384`, MJPEG stream read buffer size)
- `--videoMjpegMaxFrameBytes N` (default `4194304`, MJPEG max frame size)
- `--videoModesCacheTtlSeconds N` (default `300`, 0 disables)
- `--devicesCacheMaxAgeSeconds N` (default `600`, 0 disables)
- `--uartHmacKey SECRET` (default `changeme`, must match `PROXY_CTRL_HMAC_KEY`)
- `--mouseLeftMask 0x01` (default `1`)
- `--mouseRightMask 0x02` (default `2`)
- `--mouseMiddleMask 0x04` (default `4`)
- `--mouseBackMask 0x08` (default `8`)
- `--mouseForwardMask 0x10` (default `16`)
- `--token SECRET` (requires header `X-HID-Token: SECRET`)

If `--token` is set, add `-H "X-HID-Token: SECRET"` to every request (including `/health`, `/config`, `/stats`).

When `masterSecret` is set and `B_host` supports `GET_DEVICE_ID (0x06)`, the server derives a per-device UART HMAC key:

```
derived_key = HMAC-SHA256(masterSecret, device_id)
```

The initial `GET_DEVICE_ID` call uses the master secret; subsequent commands use the derived key.

## SBC memory profile (Raspberry Pi Zero 2W)

Suggested config overrides for low-memory devices:

```json
{
  "videoFlvBufferBytes": 131072,
  "videoFlvReadBufferBytes": 32768,
  "videoMjpegReadBufferBytes": 8192,
  "videoMjpegMaxFrameBytes": 2097152,
  "videoModesCacheTtlSeconds": 120,
  "devicesCacheMaxAgeSeconds": 300
}
```

CLI equivalents:

```bash
dotnet run -- \
  --videoFlvBufferBytes 131072 \
  --videoFlvReadBufferBytes 32768 \
  --videoMjpegReadBufferBytes 8192 \
  --videoMjpegMaxFrameBytes 2097152 \
  --videoModesCacheTtlSeconds 120 \
  --devicesCacheMaxAgeSeconds 300
```

## PostGIS (targets storage)

This repo includes a minimal PostGIS Docker setup at the repository root.

Start:

```bash
docker compose -f ../../docker-compose.postgis.yml up -d
```

Default connection string:

```
Host=localhost;Port=5433;Database=hidcontrol;Username=hidcontrol;Password=hidcontrol
```

You can override it later via config/CLI when the target storage is wired.
If you need to import existing `hidcontrol.db`, set `migrateSqliteToPg=true` once at startup.

## Examples

Show config / stats:

```bash
curl http://127.0.0.1:8080/config
curl http://127.0.0.1:8080/stats
curl http://127.0.0.1:8080/device-id
curl http://127.0.0.1:8080/version
```

Quick start (video + HID): `Docs/quick_start_remote.md`

UART quick checks:

```bash
curl http://127.0.0.1:8080/uart/ping
curl http://127.0.0.1:8080/uart/trace
```

Full UART + WS test:

```powershell
.\Scripts\uart_ws_full_test.ps1
```

Video + WS quick start (auto upsert + ffmpeg start + tests):

```powershell
.\Scripts\video_ws_start.ps1
```

Якщо ffmpeg вже запущено і не хочеш додатковий `test-capture`:

```powershell
.\Scripts\video_ws_start.ps1 -SkipTestCapture
```

Найпростіший старт відео (крок за кроком):

1) Переконайся, що сервер запущений і відкривається `http://127.0.0.1:8080/health`.
2) (Windows) Встанови залежності:
   ```powershell
   .\Scripts\install_video_deps.ps1
   ```
3) Запусти авто-скрипт відео:
   ```powershell
   .\Scripts\video_ws_start.ps1
   ```
4) Відкрий у браузері:
   - `http://127.0.0.1:8080/video`
   - `http://127.0.0.1:8080/video/mjpeg/cap-1`
5) Якщо бачиш `exit_-5` або `device busy`, закрий OBS/Teams/браузери з камерою і повтори крок 3.
   Якщо треба звільнити захоплення з нашого боку, виконай:
   ```powershell
   curl -X POST http://127.0.0.1:8080/video/capture/stop?id=cap-1
   ```
6) Діагностика захоплення:
   ```powershell
   curl http://127.0.0.1:8080/video/diag
   ```
   Якщо `orphanFfmpeg` не пустий — увімкни `videoKillOrphanFfmpeg: true` і перезапусти сервер.

Reset capture device (Windows, admin required):

```powershell
.\Scripts\video_reset_capture.ps1
```

## WebSocket control (draft)

The control channel (mouse/keyboard/gamepad) will use a separate WebSocket endpoint. Draft protocol:

`Docs/ws_control_protocol.md`

Video pipeline draft:

`Docs/video_pipeline_plan.md`

Video streams mapping:

`Docs/video_streams.md`

WebSocket status endpoint:

`ws://127.0.0.1:8080/status`

Video page:

`http://127.0.0.1:8080/video`

## Video profiles (cross-platform)

Default config ships with OS-specific low-latency profiles and `activeVideoProfile=auto`:

- Windows: `win-1080-low`, `win-2k-low`, `win-4k-low`
- Linux (RPI): `pi-1080-low`, `pi-2k-low`
- macOS: `mac-1080-low`

Set active profile via API:

```bash
curl -X POST http://127.0.0.1:8080/video/profiles/active \
  -H "Content-Type: application/json" \
  -d "{\"name\":\"auto\"}"
```

Дефолтні порти:

- RTSP: `8554`
- SRT: `9000 + index`
- RTMP: `1935`

Змінюються через `videoRtspPort`, `videoSrtBasePort`, `videoRtmpPort`.

## Швидкий старт: відео (Windows)

1) Встанови ffmpeg:
   ```powershell
   .\Scripts\install_video_deps.ps1
   ```
2) Знайди відео-пристрої:
   ```bash
   curl http://127.0.0.1:8080/video/dshow/devices
   curl http://127.0.0.1:8080/video/sources/windows
   ```
3) Додай джерело (приклад для `USB3.0 Video`):
   ```bash
   curl -X POST http://127.0.0.1:8080/video/sources/upsert \
     -H "Content-Type: application/json" \
     -d "{\"id\":\"cap-1\",\"kind\":\"uvc\",\"url\":\"win:USB\\\\VID_345F&PID_2131&MI_00\\\\7&2637BD52&0&0000\",\"name\":\"USB3.0 Video\",\"enabled\":true,\"ffmpegInputOverride\":\"-f dshow -i video=\\\"USB3.0 Video\\\"\"}"
   ```
   Якщо треба, використай `alternativeName` з `/video/dshow/devices`.
4) Запусти потоки:
   ```bash
   curl -X POST http://127.0.0.1:8080/video/ffmpeg/start
   ```
5) Перевір:
   ```bash
   curl -X POST "http://127.0.0.1:8080/video/test-capture?id=cap-1"
   curl "http://127.0.0.1:8080/video/snapshot/cap-1" --output snap.jpg
   ```
6) Відкрий у браузері:
   - MJPEG: `http://127.0.0.1:8080/video/mjpeg/cap-1`
   - HLS: `http://127.0.0.1:8080/video/hls/cap-1/index.m3u8`

## Швидкий старт: відео (Linux / RPI)

1) Додай джерело:
   ```bash
   curl -X POST http://127.0.0.1:8080/video/sources/upsert \
     -H "Content-Type: application/json" \
     -d "{\"id\":\"cap-1\",\"kind\":\"uvc\",\"url\":\"/dev/video0\",\"name\":\"USB Capture\",\"enabled\":true}"
   ```
2) Запусти потоки:
   ```bash
   curl -X POST http://127.0.0.1:8080/video/ffmpeg/start
   ```
3) Перевір:
   ```bash
   curl -X POST "http://127.0.0.1:8080/video/test-capture?id=cap-1"
   ```

## MJPEG / HLS (без MediaMTX)

Browser-friendly MJPEG stream:

```bash
http://127.0.0.1:8080/video/mjpeg/{id}
```

HLS (live):

```bash
http://127.0.0.1:8080/video/hls/{id}/index.m3u8
```

Snapshot:

```bash
http://127.0.0.1:8080/video/snapshot/{id}
```

MJPEG/HLS не потребують MediaMTX і працюють на Windows/Linux/macOS, якщо ffmpeg відкриває джерело.
The server keeps a shared capture process per source, so multiple MJPEG clients do not reopen the device.
If you still get `device busy`, close other apps that might hold the capture device.

## Screen capture (без MediaMTX)

Source kind `screen` uses OS-specific capture:

- Windows: `gdigrab` (`desktop`)
- Linux: `x11grab` (`:0.0` by default)
- macOS: `avfoundation` (device `1` by default)

Add screen source:

```bash
curl -X POST http://127.0.0.1:8080/video/sources/upsert -H "Content-Type: application/json" -d "{\"id\":\"screen-1\",\"kind\":\"screen\",\"url\":\"\",\"name\":\"Screen Capture\",\"enabled\":true}"
```

## Video dependencies (Windows)

Auto-install FFmpeg and update config path:

```powershell
.\Scripts\install_video_deps.ps1
```

Re-run with custom install directory or config path:

```powershell
.\Scripts\install_video_deps.ps1 -InstallDir "C:\Tools\HidVideo" -ConfigPath ".\hidcontrol.config.json"
```

After install, restart the server and run:

```bash
curl -X POST http://127.0.0.1:8080/video/ffmpeg/start
```

## WebSocket control client (C#)

Minimal console client:

```bash
dotnet run --project ../HidControlClient/HidControlClient.csproj -- --url=ws://127.0.0.1:8080/ws/hid --dx=10 --dy=0
```

Optional args:

- `--token=SECRET` (if token enabled)
- `--target=bhost-1`
- `--wheel=1`
- `--no-click`
- `--batch=50` (send batch of mouse moves)
- `--status` (connect to ws://HOST/status and print updates)

## Video sources

List sources:

```bash
curl http://127.0.0.1:8080/video/sources
```

Changes from `POST /video/sources` and `POST /video/sources/upsert` persist into the active `hidcontrol.config.json` (resolved from `--config`, then cwd, then app base dir).

Replace list:

```bash
curl -X POST http://127.0.0.1:8080/video/sources -H "Content-Type: application/json" -d "{\"sources\":[{\"id\":\"cap-1\",\"kind\":\"uvc\",\"url\":\"/dev/video0\",\"name\":\"USB Capture\",\"enabled\":true}]}"
```

Upsert one source:

```bash
curl -X POST http://127.0.0.1:8080/video/sources/upsert -H "Content-Type: application/json" -d "{\"id\":\"rtsp-1\",\"kind\":\"rtsp\",\"url\":\"rtsp://cam/stream\",\"name\":\"Cam\",\"enabled\":true}"
```

Optional FFmpeg override input:

```bash
curl -X POST http://127.0.0.1:8080/video/sources/upsert -H "Content-Type: application/json" -d "{\"id\":\"cap-1\",\"kind\":\"uvc\",\"url\":\"win:...\",\"name\":\"USB3.0 Video\",\"enabled\":true,\"ffmpegInputOverride\":\"-f dshow -i video=\\\"@device_pnp_...\\\"\"}"
```

Scan local sources:

```bash
curl -X POST "http://127.0.0.1:8080/video/sources/scan?apply=true"
```

Windows device list (WMI):

```bash
curl http://127.0.0.1:8080/video/sources/windows
```

DShow device list (FFmpeg):

```bash
curl http://127.0.0.1:8080/video/dshow/devices
```

Stream endpoints:

```bash
curl http://127.0.0.1:8080/video/streams
```

Поля в відповіді: `rtspUrl`, `srtUrl`, `rtmpUrl`, `hlsUrl`, `mjpegUrl`, `snapshotUrl`.

Write stream plan (ffmpeg args snapshot):

```bash
curl -X POST http://127.0.0.1:8080/video/streams/config/write
```

Файл зберігається у `videoLogDir` (за замовчуванням `video_logs/video_streams.json`).

Video profiles:

```bash
curl http://127.0.0.1:8080/video/profiles
curl -X POST "http://127.0.0.1:8080/video/profiles/active?name=low-latency"
```

FFmpeg control:

```bash
curl -X POST http://127.0.0.1:8080/video/ffmpeg/start
curl http://127.0.0.1:8080/video/ffmpeg/status
curl -X POST "http://127.0.0.1:8080/video/ffmpeg/stop?id=cap-1"
```

FFmpeg watchdog:

- config options: `ffmpegAutoStart`, `ffmpegWatchdogEnabled`, `ffmpegWatchdogIntervalMs`, `ffmpegRestartDelayMs`, `ffmpegMaxRestarts`, `ffmpegRestartWindowMs`
List connected HID interfaces (from B_host):

```bash
curl http://127.0.0.1:8080/devices
```

List serial ports (Windows):

```bash
curl http://127.0.0.1:8080/serial/ports
```

If the derived key fails for `/devices`, you can force a bootstrap attempt:

```bash
curl "http://127.0.0.1:8080/devices?useBootstrapKey=true"
```

If UART is down, you can use cached devices:

```bash
curl "http://127.0.0.1:8080/devices?allowStale=true"
curl "http://127.0.0.1:8080/devices/last"
```

If UART is down and cache is empty, `/devices?allowStale=true` falls back to mappings stored in DB.

Retry derived HMAC (after updating firmware/masterSecret):

```bash
curl -X POST http://127.0.0.1:8080/hmac/retry
```

`/stats` and `/device-id` now include `hmacMode` (`bootstrap` or `derived`).

`/devices` returns `itfProtocol` (boot-proto), `inferredType` (bitmask from report descriptor: bit0=keyboard, bit1=mouse), and `typeName`.
When `devicesIncludeReportDesc=true`, it also includes `reportDescHex` (possibly truncated) and `reportDescTotalLen`.
If `mouseTypeName` / `keyboardTypeName` are set in config (or CLI), they are used as defaults to pick `itfSel` from the latest `/devices` snapshot.
Call `/devices` at least once after boot to populate that snapshot.
When `mouseItfSel=0xFF` or `keyboardItfSel=0xFE`, the server will also try to resolve the real interface index from the snapshot.

Note: `/devices` relies on UART responses from `B_host`. For reliable results, the server auto-mutes `B_host` logs during the request (config `devicesAutoMuteLogs`).
If you want a specific steady log level, call `/loglevel` first so the server restores it after `/devices`.
When `devicesIncludeReportDesc=true`, the auto-refresh loop also fetches report descriptors.

Toggle B_host log level (0..4):

```bash
curl -X POST http://127.0.0.1:8080/loglevel -H "Content-Type: application/json" -d "{\"level\":0}"
```

Mouse move:

```bash
curl -X POST http://127.0.0.1:8080/mouse/move -H "Content-Type: application/json" -d "{\"dx\":20,\"dy\":0}"
```

Left click:

```bash
curl -X POST http://127.0.0.1:8080/mouse/button -H "Content-Type: application/json" -d "{\"button\":\"left\",\"down\":true}"
curl -X POST http://127.0.0.1:8080/mouse/button -H "Content-Type: application/json" -d "{\"button\":\"left\",\"down\":false}"
```

Additional button names: `back`, `forward`, `x1`, `x2`, `button4`, `button5`, or `buttonN` (1..8).
Button names use the mask mapping from config/CLI for `left/right/middle/back/forward` (default mapping is left=1, right=2, middle=4, back=8, forward=16).
`buttonN` always maps to bit `1 << (N-1)`, regardless of the mask mapping.

Direct buttons mask:

```bash
curl -X POST http://127.0.0.1:8080/mouse/buttons -H "Content-Type: application/json" -d "{\"buttonsMask\":3}"
```

Convenience endpoints:

```bash
curl -X POST http://127.0.0.1:8080/mouse/leftClick
curl -X POST http://127.0.0.1:8080/mouse/rightClick
curl -X POST http://127.0.0.1:8080/mouse/middleClick
curl -X POST http://127.0.0.1:8080/mouse/leftPress
curl -X POST http://127.0.0.1:8080/mouse/leftRelease
curl -X POST http://127.0.0.1:8080/mouse/back
curl -X POST http://127.0.0.1:8080/mouse/forward
```

Type text (US layout mapping):

```bash
curl -X POST http://127.0.0.1:8080/keyboard/type -H "Content-Type: application/json" -d "{\"text\":\"hello\"}"
```

Note: if you pass an explicit `itfSel`, make sure it matches the keyboard interface (`typeName=keyboard` from `/devices`).

Keyboard layout diagnostics:

```bash
curl "http://127.0.0.1:8080/keyboard/layout?itf=2"
curl "http://127.0.0.1:8080/keyboard/layout?itf=2&reportId=1"
curl "http://127.0.0.1:8080/keyboard/layouts?itf=2&maxReportId=16"
```

Keyboard report test (custom modifiers + keys, max 6 keys):

```bash
curl -X POST http://127.0.0.1:8080/keyboard/report -H "Content-Type: application/json" -d "{\"modifiers\":0,\"keys\":[4,5],\"itfSel\":2,\"applyMapping\":true}"
```

Keyboard reset/state:

```bash
curl -X POST http://127.0.0.1:8080/keyboard/reset -H "Content-Type: application/json" -d "{\"itfSel\":2}"
curl http://127.0.0.1:8080/keyboard/state
```

Windows PowerShell test scripts:

```powershell
.\Scripts\keyboard_test.ps1
.\Scripts\mouse_test.ps1
.\Scripts\hid_test.ps1
```

Full integration tests:

Windows (PowerShell):
```powershell
.\Scripts\video_full_test.ps1 -ScanApply -StartFfmpeg
.\Scripts\hid_full_test.ps1 -Token SECRET
```

Linux/macOS (bash):
```bash
BASE_URL=http://127.0.0.1:8080 TOKEN=SECRET bash Scripts/video_full_test.sh
USE_BOOTSTRAP=1 SKIP_INJECT=1 bash Scripts/hid_full_test.sh
```

WebSocket HID (low-latency):

Endpoint:
- `ws://HOST:PORT/ws/hid?access_token=SECRET` (token optional if disabled)

Message format (JSON):

- `{"id":"1","type":"mouse.move","dx":5,"dy":-2,"wheel":0,"itfSel":0}`
- `{"id":"2","type":"mouse.wheel","delta":1,"itfSel":0}`
- `{"id":"3","type":"mouse.button","button":"left","down":true,"itfSel":0}`
- `{"id":"4","type":"mouse.buttons","mask":1,"itfSel":0}`
- `{"id":"5","type":"keyboard.press","usage":4,"mods":0,"itfSel":2}`
- `{"id":"6","type":"keyboard.down","usage":4,"mods":0,"itfSel":2}`
- `{"id":"7","type":"keyboard.up","usage":4,"mods":0,"itfSel":2}`
- `{"id":"8","type":"keyboard.text","text":"hello","itfSel":2}`
- `{"id":"9","type":"keyboard.report","mods":0,"keys":[4,5],"applyMapping":true,"itfSel":2}`

Server responses:
- `{"ok":true,"type":"mouse.move","id":"1"}`
- `{"ok":false,"type":"keyboard.text","id":"8","error":"unsupported_char_XXXX"}`

WS test page:
- `http://127.0.0.1:8080/hid/ws-test`

WS test script (PowerShell):
```powershell
.\Scripts\ws_hid_test.ps1
```

Combined video + HID + WS smoke test (PowerShell):
```powershell
.\Scripts\video_hid_ws_test.ps1 -BaseUrl http://127.0.0.1:8080 -SourceId cap-1
```

Serial probe (find B_host by device id):
```bash
curl "http://127.0.0.1:8080/serial/probe"
```

One-shot test runner (PowerShell):
```powershell
.\Scripts\full_test.ps1
```

Options:
- `-Strict` (fail on source/OS mismatch)

Exit codes:

- `2` = health failed (server not reachable)
- `3` = `/devices` failed
- `4` = no interfaces
- `5` = no keyboard interface (keyboard/hid test)
- `6` = no mouse interface (mouse/hid test)

Per-request interface override:

```bash
curl -X POST http://127.0.0.1:8080/mouse/move -H "Content-Type: application/json" -d "{\"dx\":10,\"dy\":0,\"itfSel\":2}"
curl -X POST http://127.0.0.1:8080/keyboard/press -H "Content-Type: application/json" -d "{\"usage\":4,\"itfSel\":1}"
```

Raw inject (hex report bytes):

```bash
curl -X POST http://127.0.0.1:8080/raw/inject -H "Content-Type: application/json" -d "{\"itfSel\":255,\"reportHex\":\"00 05 00 00\"}"
```

Note: inject commands are queued and retried to improve UART reliability. Use `injectQueueCapacity`, `injectDropThreshold`, `injectTimeoutMs`, `injectRetries` to tune it.

Mouse mapping UI:

```bash
curl http://127.0.0.1:8080/mouse/testButtons
```

Keyboard mapping UI:

```bash
curl http://127.0.0.1:8080/keyboard/map
```

Saved mappings:

- `POST /mouse/mapping` with `{deviceId,itf,reportDescHash,buttonsCount,mapping}`
- `GET /mouse/mapping?deviceId=...`
- `POST /keyboard/mapping` with `{deviceId,itf,reportDescHash,mapping}`
- `GET /keyboard/mapping?deviceId=...`
- `GET /keyboard/mapping/byItf?itf=...`

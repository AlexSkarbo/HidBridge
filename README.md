# HIDden Bridge (HidBridge)

Hidden Bridge for HID (remote KVM-style control).

HidBridge is a **dual‑MCU USB HID bridge** built on RP2040 microcontrollers. It acts as a **transparent proxy** for USB HID devices, enabling remote keyboard/mouse injection while streaming low‑latency video over **WebRTC**, **HLS**, **FLV**, or **MJPEG passthrough**.

The controlled device remains **agentless** - no software is installed on the host - because all input is delivered via USB HID and video is served from HidControlServer using standard streaming protocols.



Repository: `https://github.com/AlexSkarbo/HidBridge.git`

At a high level:
- `B_host` enumerates a USB HID peripheral as a **USB host** and forwards input to `A_device`.
- `A_device` presents itself to the target PC as a **USB HID device**.
- `HidControlServer` can inject extra mouse/keyboard input into `B_host` over a dedicated **control UART** (SLIP framed, authenticated) and also provides video streaming endpoints driven by FFmpeg.

## Micro Meet

`Micro Meet` is the operator-facing collaboration layer built on top of the new `Platform/` stack.

What it demonstrates:
- fleet view of available endpoints
- one-click `Start session` / `Launch room`
- room-based invite and join flow
- explicit control handoff between operators
- room timeline for what happened and when

This is positioned as:
- shared control rooms for real endpoints
- not just another screen-sharing demo

Primary docs:
- runtime and local stack: `Platform/README.md`
- demo walkthrough (UA): `Docs/GoToMarket/MicroMeet_Demo_Runbook_UA.md`
- demo walkthrough (EN): `Docs/GoToMarket/MicroMeet_Demo_Runbook_EN.md`
- GitHub packaging notes: `Docs/GoToMarket/MicroMeet_GitHub_Package_UA.md`
- Reddit draft: `Docs/GoToMarket/MicroMeet_Reddit_Post_UA.md`
- LinkedIn draft: `Docs/GoToMarket/MicroMeet_LinkedIn_Post_UA.md`
- publish checklist: `Docs/GoToMarket/MicroMeet_Publish_Checklist_UA.md`

## Repository Layout

- `Firmware/`
  - `Firmware/src/A_device` — USB device endpoint firmware (TinyUSB device)
  - `Firmware/src/B_host` — USB host endpoint firmware (TinyUSB host)
  - `Firmware/src/common` — shared transport/protocol/config
- `Tools/`
  - `Tools/HidControlServer` — ASP.NET Core server (REST + WebSocket) for HID injection + video
  - `Tools/Shared/*` — contracts + client SDK
  - `Tools/Clients/*` — client skeletons (Web/Desktop/Mobile)
- `Docs/` — protocol docs + quick starts

## Quick Start

### 1) Firmware

Prereqs:
- Pico SDK configured via `PICO_SDK_PATH`
- CMake + toolchain

Build (example):

```bash
cmake -S . -B build -DPICO_BOARD=waveshare_rp2040_zero
cmake --build build -j
```

Outputs typically include `build/A_device.uf2` and `build/B_host.uf2`.

Config:
- Edit `Firmware/src/common/proxy_config.h` (UART pins/baud and `PROXY_CTRL_HMAC_KEY`).

### 2) Tools (HidControlServer)

Prereqs:
- .NET SDK 10.0+
- FFmpeg (either in `PATH` or configured via `ffmpegPath` in server config)

Run:

```bash
cd Tools/HidControlServer
dotnet run -- --config hidcontrol.config.json
```

Notes:
- `Tools/HidControlServer/hidcontrol.config.json` is **intentionally gitignored** (local machine config).
  - Start from `Tools/HidControlServer/hidcontrol.config.json.example`.
- For remote video + HID quick start, see `Docs/quick_start_remote.md`.

## Docs

- UART control protocol: `Docs/uart_control_protocol.md`
- WebSocket control protocol: `Docs/ws_control_protocol.md`
- Video endpoints mapping: `Docs/video_streams.md`
- Micro Meet demo/sales runbook (UA): `Docs/GoToMarket/MicroMeet_Demo_Runbook_UA.md`
- Micro Meet demo/sales runbook (EN): `Docs/GoToMarket/MicroMeet_Demo_Runbook_EN.md`

## Test Runner

Use the root script to run all main test suites in one command:

```powershell
.\run_all_tests.ps1
```

Useful options:
- `-Configuration Debug|Release`
- `-DotnetVerbosity quiet|minimal|normal|detailed|diagnostic`
- `-SkipDotnet`
- `-SkipGo`
- `-StopOnFailure`

## License

Copyright (c) 2026 Skarbo Oleksandr / Alexander Skarbo.

HidBridge is licensed for non‑commercial use under the PolyForm Noncommercial License 1.0.0.  
You can read the full license text at https://polyformproject.org/licenses/noncommercial/1.0.0/ or in the `LICENSE` file.

Any commercial use (including embedding in products, distribution for profit, or offering as a service) requires a separate commercial license. See `COMMERCIAL_LICENSE.md`.

## Commercial Licensing

If you want to use HidBridge in a commercial product or service, you need a separate commercial license.
See `COMMERCIAL_LICENSE.md` for available commercial license packages (Indie, Team, Enterprise) and contact details.

Contact: `alexandr.skarbo@gmail.com`

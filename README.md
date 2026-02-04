# HIDden Bridge (HidBridge)

Hidden Bridge for HID (remote KVM-style control).

HidBridge is a **dual-MCU HID proxy/bridge** (Pico/RP2040-class) with tooling for **remote HID injection** and **low-latency video**.

Key idea: the controlled device can remain "agentless" (no software installed) because input is injected over USB HID.

Repository: `https://github.com/AlexSkarbo/HidBridge.git`

At a high level:
- `B_host` enumerates a USB HID peripheral as a **USB host** and forwards input to `A_device`.
- `A_device` presents itself to the target PC as a **USB HID device**.
- `HidControlServer` can inject extra mouse/keyboard input into `B_host` over a dedicated **control UART** (SLIP framed, authenticated) and also provides video streaming endpoints driven by FFmpeg.

## Repository Layout

- `Firmware/`
  - `Firmware/A_device` — USB device endpoint firmware (TinyUSB device)
  - `Firmware/B_host` — USB host endpoint firmware (TinyUSB host)
  - `Firmware/common` — shared transport/protocol/config
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
- Edit `Firmware/common/proxy_config.h` (UART pins/baud and `PROXY_CTRL_HMAC_KEY`).

### 2) Tools (HidControlServer)

Prereqs:
- .NET SDK 9.0+
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

## Commercial Licensing

If you want to legally use HidBridge in a product/service, you need a commercial license. See `COMMERCIAL_LICENSE.md`.
Contact: `alexandr.skarbo@gmail.com`

## License

Copyright (c) 2026 Skarbo Oleksandr / Alexander Skarbo.

This project is **proprietary**. No license is granted for use/distribution without a separate written agreement. See `LICENSE`.

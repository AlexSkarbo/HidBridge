# UART control (B_host) — HID input injection (v2)

This project accepts **external control commands over UART** on `B_host` and injects them into the `B_host → A_device` link as normal `PF_INPUT` frames.

## UART ports

- **Bridge link (B_host ↔ A_device)**: configured by `PROXY_UART_*` in `Firmware/common/proxy_config.h`.
  - Default pins: `uart1` TX=4 RX=5 (CTS=6 RTS=7 if enabled)
  - Default requested baud: `PROXY_UART_BAUD` (set to `PROXY_UART_BAUD_FAST` by default)
- **Control port (external → B_host)**: configured by `PROXY_CTRL_UART_*` in `Firmware/common/proxy_config.h`.
  - Default pins: `uart0` TX=0 RX=1
  - Default requested baud: `PROXY_CTRL_UART_BAUD` (3,000,000)

`Firmware/common/uart_transport.c` and `Firmware/B_host/control_uart.c` log the **actual** baud set by the SDK (it can be clamped by the UART clock).

## Framing

Control commands are **SLIP framed**:

- `END = 0xC0`
- `ESC = 0xDB`
- `ESC_END = 0xDC` (escaped `END`)
- `ESC_ESC = 0xDD` (escaped `ESC`)

Each SLIP frame contains one **v2 control frame**.

## v2 frame format

All integers are little-endian unless noted.

```
[0]  magic   = 0xF1
[1]  version = 0x01
[2]  flags   = bit0=response, bit1=error
[3]  seq     = 0..255
[4]  cmd
[5]  len     = payload length (0..240)
[6..] payload
[6+len]   crc16 LSB
[7+len]   crc16 MSB
[8+len..] hmac16 (first 16 bytes of HMAC-SHA256)
```

- `crc16` is CRC-CCITT with seed `0xFFFF` over `header+payload` (`[0..5+len]`).
- `hmac16` is computed over `header+payload+crc` (`[0..7+len]`) using the shared key.
- The HMAC key must match on both sides:
  - Firmware: `PROXY_CTRL_HMAC_KEY` in `Firmware/common/proxy_config.h`
  - Server: `masterSecret` in `hidcontrol.config.json` / `--masterSecret`

## Error codes

When `flags` includes `error`, payload is one byte:

- `1` = bad length
- `2` = inject failed (not ready or invalid interface)
- `3` = report descriptor missing
- `4` = report layout missing

## Commands

### `0x01` — INJECT_REPORT

Request payload:

- `[0] = itf_sel`
  - `0..CFG_TUH_HID-1` = explicit interface
  - `0xFF` = first mounted **mouse** interface
  - `0xFE` = first mounted **keyboard** interface
- `[1] = report_len` (`N`)
- `[2..] = report bytes` (`N`)

Response: ACK or error.

### `0x02` — LIST_INTERFACES

Request payload: none.

Response payload:

- `[0] = count`
- Then `count` entries, each **7 bytes**:
  - `[0] = dev_addr`
  - `[1] = itf` (interface index)
  - `[2] = itf_protocol` (0=none, 1=keyboard, 2=mouse)
  - `[3] = protocol` (0=boot, 1=report)
  - `[4] = inferred_type` (bit0=keyboard, bit1=mouse)
  - `[5] = active` (0/1)
  - `[6] = mounted` (0/1)

### `0x03` — SET_LOG_LEVEL

Request payload:

- `[0] = level` (`0`..`4`)

Response: ACK or error.

### `0x04` — GET_REPORT_DESC

Request payload:

- `[0] = itf` (interface index)

Response payload:

- `[0] = itf`
- `[1] = total_len LSB`
- `[2] = total_len MSB`
- `[3] = flags` (bit0 = truncated)
- `[4..] = report descriptor bytes (may be truncated)`

### `0x05` — GET_REPORT_LAYOUT

Request payload:

- `[0] = itf` (interface index)
- `[1] = reportId` (`0` = auto)

Response payload:

- `[0] = itf`
- `[1] = reportId`
- `[2] = layoutKind` (1=mouse, 2=keyboard, 3=mouse+keyboard)
- `[3] = flags` (bit0=hasButtons, bit1=hasWheel, bit2=hasX, bit3=hasY)
- `[4] = buttonsOffsetBits`
- `[5] = buttonsCount`
- `[6] = buttonsSizeBits`
- `[7] = xOffsetBits`
- `[8] = xSizeBits`
- `[9] = xSigned` (0/1)
- `[10] = yOffsetBits`
- `[11] = ySizeBits`
- `[12] = ySigned` (0/1)
- `[13] = wheelOffsetBits`
- `[14] = wheelSizeBits`
- `[15] = wheelSigned` (0/1)
- `[16] = kbReportLen` (bytes, excluding Report ID)
- `[17] = kbHasReportId` (0/1)

### `0x06` — GET_DEVICE_ID

Request payload: none.

Response payload:

- `[0] = id_len` (currently 8)
- `[1..] = device id bytes` (Pico unique board ID)

Key derivation flow:

- Server sends `GET_DEVICE_ID` using the **master secret** key.
- Server derives per-device key: `HMAC-SHA256(master_secret, device_id)`.
- All subsequent commands use the **derived key**.

## Mouse report (Boot protocol)

Most “boot mouse” devices use a 3-byte or 4-byte input report (no Report ID).

Report bytes:

- Byte `0`: buttons bitmask
  - bit0 = Left
  - bit1 = Right
  - bit2 = Middle
- Byte `1`: `dx` (signed int8, -127..127)
- Byte `2`: `dy` (signed int8, -127..127)
- Byte `3` (optional): wheel (signed int8, -127..127)

## Keyboard report (Boot protocol)

Boot keyboard input report is always 8 bytes (no Report ID):

- Byte `0`: modifiers bitmask
- Byte `1`: reserved (`0x00`)
- Bytes `2..7`: up to 6 simultaneous key usages (HID Usage Page 0x07).

To “press” a key, send a **key-down** report and then a **key-up** (all zeros) report.

## Report IDs / non-boot devices

If the enumerated interface uses **Report IDs**, the first byte of the report is the Report ID. In that case include it as byte 0 of `report bytes` and set `report_len` accordingly (or use `/raw/inject` to experiment).

## HidControlServer configuration and testing

The C# server wraps this UART protocol and exposes HTTP endpoints. Config lives in `Tools/HidControlServer/hidcontrol.config.json` (see `hidcontrol.config.json.example`).

Key reliability options:

- `injectQueueCapacity`, `injectDropThreshold`
- `injectTimeoutMs`, `injectRetries`
- `keyboardInjectTimeoutMs`, `keyboardInjectRetries`
- `mouseMoveTimeoutMs`, `mouseMoveDropIfBusy`, `mouseMoveAllowZero`
- `mouseWheelTimeoutMs`, `mouseWheelDropIfBusy`, `mouseWheelAllowZero`

HID layout diagnostics:

```bash
curl "http://127.0.0.1:8080/keyboard/layout?itf=2"
curl "http://127.0.0.1:8080/keyboard/layout?itf=2&reportId=1"
curl "http://127.0.0.1:8080/keyboard/layouts?itf=2&maxReportId=16"
```

Keyboard report test:

```bash
curl -X POST http://127.0.0.1:8080/keyboard/report \
  -H "Content-Type: application/json" \
  -d "{\"modifiers\":0,\"keys\":[4,5],\"itfSel\":2,\"applyMapping\":true}"
```

Keyboard reset/state:

```bash
curl -X POST http://127.0.0.1:8080/keyboard/reset -H "Content-Type: application/json" -d "{\"itfSel\":2}"
curl http://127.0.0.1:8080/keyboard/state
curl http://127.0.0.1:8080/device-id
curl http://127.0.0.1:8080/version
```

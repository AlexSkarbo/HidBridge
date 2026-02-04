# Quick Start: Remote Video + HID Control

This guide assumes:
- HidControlServer is already built and runs on the host PC.
- B_host firmware is flashed with matching `PROXY_CTRL_HMAC_KEY`.
- USB-UART is connected (TX->RX, RX->TX, GND->GND).

Solution file: `Tools/HidBridge.sln`.

## 1) Start the server

From `Tools/HidControlServer`:

```powershell
dotnet run -- --config hidcontrol.config.json
```

## 2) Start video + basic checks

```powershell
.\Scripts\video_ws_start.ps1 -UseDeviceName
```

Open:
- `http://127.0.0.1:8080/video`

## 3) Verify UART + devices

```powershell
curl http://127.0.0.1:8080/uart/ping
curl http://127.0.0.1:8080/devices?includeReportDesc=true
```

Expected:
- `ok: true`
- `keyMode: derived` (no bootstrap warning)

If you see bootstrap warning:
1) Make sure `masterSecret` and `uartHmacKey` in `hidcontrol.config.json`
   match `PROXY_CTRL_HMAC_KEY` in firmware.
2) Reflash B_host, then:
   ```powershell
   curl -X POST http://127.0.0.1:8080/hmac/retry
   ```

## 4) Verify HID over WS (low latency)

```powershell
.\Scripts\ws_hid_test.ps1
```

Expected:
- `ok:true` for mouse and keyboard
- `uart reports delta: +...`

## 5) Manual HID test (REST)

Mouse:
```powershell
irm -Method Post -Uri http://127.0.0.1:8080/mouse/move -ContentType "application/json" -Body '{"dx":30,"dy":0,"itfSel":0}'
```

Keyboard:
```powershell
irm -Method Post -Uri http://127.0.0.1:8080/keyboard/text -ContentType "application/json" -Body '{"text":"TEST ","itfSel":2}'
```

Note:
- `/keyboard/text` is **ASCII-only** by default.
- For non-ASCII text (Cyrillic/CJK/etc.) without installing anything on the controlled device, see: `Docs/text_input_i18n.md`.

## Troubleshooting

- `/uart/ping` timeout: wrong COM port, bad wiring, or wrong baud.
- `derived key failed`: key mismatch between server config and firmware.
- Video OK, HID not: check `devices` and use explicit `itfSel`.

---

# Швидкий старт: віддалене відео + керування HID

Цей гайд передбачає:
- HidControlServer вже зібраний і запущений на хості.
- Прошивка B_host прошита з правильним `PROXY_CTRL_HMAC_KEY`.
- USB-UART підключений (TX->RX, RX->TX, GND->GND).

Файл рішення: `Tools/HidBridge.sln`.

## 1) Запуск сервера

З `Tools/HidControlServer`:

```powershell
dotnet run -- --config hidcontrol.config.json
```

## 2) Старт відео + базова перевірка

```powershell
.\Scripts\video_ws_start.ps1 -UseDeviceName
```

Відкрий:
- `http://127.0.0.1:8080/video`

## 3) Перевірка UART + пристроїв

```powershell
curl http://127.0.0.1:8080/uart/ping
curl http://127.0.0.1:8080/devices?includeReportDesc=true
```

Очікувано:
- `ok: true`
- `keyMode: derived` (без попередження про bootstrap)

Якщо бачиш попередження про bootstrap:
1) Переконайся, що `masterSecret` і `uartHmacKey` в `hidcontrol.config.json`
   збігаються з `PROXY_CTRL_HMAC_KEY` у прошивці.
2) Перепроший B_host, потім:
   ```powershell
   curl -X POST http://127.0.0.1:8080/hmac/retry
   ```

## 4) Перевірка HID через WS (низька затримка)

```powershell
.\Scripts\ws_hid_test.ps1
```

Очікувано:
- `ok:true` для миші та клавіатури
- `uart reports delta: +...`

## 5) Ручна перевірка HID (REST)

Миша:
```powershell
irm -Method Post -Uri http://127.0.0.1:8080/mouse/move -ContentType "application/json" -Body '{"dx":30,"dy":0,"itfSel":0}'
```

Клавіатура:
```powershell
irm -Method Post -Uri http://127.0.0.1:8080/keyboard/text -ContentType "application/json" -Body '{"text":"TEST ","itfSel":2}'
```

## Діагностика

- `/uart/ping` timeout: неправильний COM-порт, проводка або baud.
- `derived key failed`: ключі не збігаються між сервером та прошивкою.
- Відео є, HID нема: перевір `devices` і вкажи явний `itfSel`.

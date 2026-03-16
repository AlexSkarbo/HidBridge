# HidBridge Agent Product Model (UA)

Оновлено: `2026-03-16`

## 1. Призначення документа

Цей документ фіксує практичну продуктову модель для device-oriented агентів HidBridge і дає 3 конкретні референс-приклади:

1. WiFi Camera Agent (Tapo C220).
2. PC Resident Agent (встановлюється на цільовий ПК).
3. Edge Proxy Agent (експериментальна модель `exp-022`: HDMI capture + USB HID bridge firmware).

## 2. Спільна модель для всіх агентів

Кожен агент:

1. Реєструється в Core як `agentId` із `tenant/org scope`.
2. Публікує endpoint-и, capabilities і health.
3. Приймає командні envelope-и через adapter/transport.
4. Повертає `ACK` з нормалізованим статусом:
   - `Applied`
   - `Rejected`
   - `Timeout`
5. Публікує telemetry/event/audit дані.

Базові обов'язкові поля endpoint-паспорта:

1. `endpointId`
2. `agentId`
3. `deviceType`
4. `capabilities[]`
5. `transportProviders[]`
6. `health.status`
7. `health.lastSeenUtc`

## 3. Приклад 1 — WiFi Camera Agent (Tapo C220)

## 3.1 Модель розгортання

1. Агент встановлений на edge host у тій самій мережі, що камера.
2. Камера підключається через RTSP/ONVIF/HTTP API (залежить від конфігурації).
3. Агент шле відео (і за наявності аудіо) у media plane платформи.

## 3.2 Основні можливості

1. `video.stream.h264` або `video.stream.h265`
2. `audio.stream.aac` (якщо доступно)
3. `camera.ptz` (pan/tilt)
4. `camera.preset` (збережені позиції)
5. `camera.snapshot`

## 3.3 Команди

1. `camera.ptz.move`
2. `camera.ptz.stop`
3. `camera.preset.goto`
4. `camera.snapshot.capture`
5. `camera.stream.restart`

## 3.4 Типові метрики health

1. `streamFps`
2. `streamBitrateKbps`
3. `streamReconnectCount`
4. `lastFrameAtUtc`
5. `rtspLatencyMs`

## 4. Приклад 2 — PC Resident Agent (інсталюється на цільовий ПК)

## 4.1 Модель розгортання

1. Агент інсталюється як service на цільовий ПК.
2. Має локальні права на:
   - screen capture
   - audio capture/render path
   - локальне виконання input-control.
3. Підключається до Core через adapter (WebRTC/WebSocket).

## 4.2 Основні можливості

1. `desktop.video.capture`
2. `desktop.audio.capture`
3. `desktop.input.keyboard`
4. `desktop.input.mouse`
5. `desktop.telemetry.system`

## 4.3 Команди

1. `keyboard.text`
2. `keyboard.shortcut`
3. `mouse.move`
4. `mouse.button`
5. `desktop.session.lock` (опційно, policy-driven)

## 4.4 Типові метрики health

1. `captureFps`
2. `encodeLatencyMs`
3. `inputAckP95Ms`
4. `cpuUsagePct`
5. `memoryUsageMb`

## 5. Приклад 3 — Edge Proxy Agent (`exp-022` модель)

## 5.1 Модель розгортання

1. Агент встановлений на ПК-посереднику поруч із цільовим ПК.
2. Відео береться через HDMI-USB карту захоплення.
3. Керування йде через USB HID bridge (Firmware RP2040 pair).
4. Цільовий ПК лишається agentless.

## 5.2 Основні можливості

1. `video.capture.hdmi-usb`
2. `input.inject.uart-hid-bridge`
3. `bridge.device.descriptor.sync`
4. `bridge.health.uart`
5. `control.relay.webrtc-dc` (для transport експериментів)

## 5.3 Команди

1. `keyboard.text`
2. `keyboard.shortcut`
3. `mouse.move`
4. `keyboard.reset`
5. `bridge.interface.probe`

## 5.4 Типові метрики health

1. `uartAckTimeoutCount`
2. `uartRoundtripMsP95`
3. `captureFrameAgeMs`
4. `bridgeConnected`
5. `activeInterfaceSelector`

## 6. Порівняння трьох моделей

1. Tapo C220 Agent:
   - оптимальний для pure camera use-case.
   - control domain: camera PTZ/preset.
2. PC Resident Agent:
   - найповніший контроль ПК (екран/звук/input/system telemetry).
   - не потребує зовнішнього HID bridge.
3. Edge Proxy Agent (`exp-022` pattern):
   - потрібен там, де target ПК не можна/небажано інсталювати агент.
   - дає agentless-control через зовнішній hardware proxy.

## 7. Рекомендований продуктово-технічний порядок

1. Першим production напрямом робити `PC Resident Agent` (найбільша цінність і найпростіша операційна модель).
2. Паралельно розвивати `WiFi Camera Agent` як окремий vertical.
3. `exp-022` pattern залишити як edge-proxy профіль для спеціальних кейсів (air-gapped/locked-down target hosts).

## 8. Нормативне правило для room-centric моделі

Усі три типи агентів є рівноправними endpoint-постачальниками в межах room:

1. Room може одночасно містити:
   - багато камерних endpoint-ів;
   - багато PC endpoint-ів;
   - багато edge-proxy endpoint-ів.
2. Учасники взаємодіють із ними в одному room-context через спільну policy/lease/audit модель.

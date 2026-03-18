# HidBridge ADR-002 (UA)

Оновлено: `2026-03-18`

## Тема

Edge Proxy Agent як production runtime для патерна `HDMI/USB capture + USB-HID-bridge firmware`.

## Контекст

`WebRtcTests/exp-022-datachanneldotnet` успішно підтвердив експериментальний PoC:

- WebRTC control path;
- relay ACK path;
- HID керування через UART bridge.

Але `exp-022` залишається дослідним проєктом з monolithic кодом, який змішує:

- protocol I/O;
- websocket control endpoint;
- runtime orchestration;
- debug/bench behavior.

Для production потрібен окремий edge runtime з чітким контрактом і керованим lifecycle.

## Рішення

1. `Platform/Edge/HidBridge.EdgeProxy.Agent` є canonical runtime для edge-proxy mode.
2. `exp-022` переводиться в лабораторний/verification контур і не є обов'язковим production dependency.
3. Функціонал переноситься шарами:
- `HidBridge.Edge.Abstractions`: стабільні інтерфейси edge runtime.
- `HidBridge.Edge.HidBridgeProtocol`: device/protocol адаптери (частковий перенос коду з `exp-022`).
- `HidBridge.EdgeProxy.Agent`: orchestration loop, API relay, heartbeat, peer lifecycle.

## Scope переносу з exp-022

Переносимо:

- HID command semantics (`keyboard.text`, `keyboard.press`, `keyboard.shortcut`, `mouse.move`, `mouse.scroll`, `mouse.button`);
- ACK parsing semantics для control websocket;
- UART/HID protocol helpers (поетапно в `HidBridge.Edge.HidBridgeProtocol`).

Не переносимо як runtime dependency:

- standalone exp-022 orchestration loop;
- експериментальні debug/poc modes;
- тимчасові benchmark paths.

## Contract boundaries

### Agent ↔ Platform API

- peer online/offline;
- heartbeat signaling;
- command polling;
- ACK publish;
- transport health projection.

### Agent ↔ Device/proxy protocol

- command execute;
- timeout/error classification;
- device health snapshot.

### Web/API policy

- readiness і lease policy виконуються в platform layer;
- agent не приймає policy-рішень доступу.

## Runtime state model (edge peer)

`Starting -> Connecting -> Connected -> Degraded -> Reconnecting -> Offline`

Обов'язкові diagnostics поля:

- `lastPeerState`
- `lastPeerFailureReason`
- `lastPeerSeenAtUtc`
- `lastRelayAckAtUtc`
- `onlinePeerCount`

## Error model

- `E_TRANSPORT_TIMEOUT`
- `E_WEBRTC_PEER_EXECUTION_FAILED`
- `E_WEBRTC_PEER_ADAPTER_FAILURE`
- `E_COMMAND_EXECUTION_FAILED`

Статуси ACK:

- `Applied`
- `Rejected`
- `Timeout`

## Наслідки

Позитивні:

- від'єднання production runtime від експериментального проєкту;
- простіша підтримка multi-agent масштабування;
- стабільний API/UI health/readiness контур.

Компроміси:

- тимчасово підтримуються два контури (`exp-022` + `EdgeProxy.Agent`) до завершення повного переносу протоколу;
- потрібні додаткові integration/e2e тести для contract parity.

## Implementation checkpoint (Iteration A)

- [x] затверджено canonical роль `EdgeProxy.Agent`;
- [x] виділено план розділення на `Abstractions` + `Protocol` + `Agent`;
- [x] додано прямий UART protocol path в `HidBridge.Edge.HidBridgeProtocol` (`UartHidCommandExecutor`);
- [x] уніфіковано HID action mapping через спільний `HidBridgeUartCommandDispatcher` (API connector + edge agent);
- [x] замкнути CI acceptance на `EdgeProxy.Agent` як primary path (`ci-local/full` через `-IncludeWebRtcEdgeAgentAcceptance`).

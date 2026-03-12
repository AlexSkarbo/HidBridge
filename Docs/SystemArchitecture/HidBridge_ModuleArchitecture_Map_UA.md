# HidBridge Module Architecture Map (UA)

Версія: `1.0-draft`  
Дата: `2026-02-27`  
Статус: `Draft for implementation`  
Пов'язані документи:  
- `Docs/SystemArchitecture/HidBridge_AgentContract_v1_UA.md`  
- `Docs/SystemArchitecture/HidBridge_SystemArchitecture_UA.md`  
- `Tools/HidBridge.sln`  
- `WebRtcTests/exp-022-datachanneldotnet/Program.cs`

## 1. Мета документа

Документ фіксує:
- карту поточних компонентів (`Tools`, `Firmware`, `exp-022`);
- цільову модульну архітектуру для multi-agent + agentless платформи;
- покроковий migration map, де кожен існуючий компонент отримує чітке місце в новій системі.

## 2. AS-IS: що вже є в репозиторії

## 2.1 Firmware

- `Firmware/src/A_device`
  - USB Device на стороні target (емуляція HID).
- `Firmware/src/B_host`
  - USB Host на стороні керуючого контуру.
  - UART control ingress (v2 frame, SLIP+CRC+HMAC).
- `Firmware/src/common`
  - transport/proxy конфіг і спільні утиліти.

## 2.2 Tools solution (legacy + reusable)

- `Tools/Server/HidControlServer`
  - монолітний серверний runtime.
  - HID UART client, HTTP endpoints, WS, media adapters.
- `Tools/Shared/*`
  - contracts/sdk/core/use-cases з частково повторно-використовними моделями.
- `Tools/Core/*`, `Tools/Infrastructure/*`
  - ранні шари clean architecture.
- `Tools/Clients/*`
  - Web/Desktop/Mobile клієнти.

## 2.3 WebRtcTests/exp-022-datachanneldotnet

- `Program.cs`
  - R&D runtime для DataChannelDotnet + WHIP/WHEP + HID over WS/DC.
  - команди `dc-whip-capture-poc`, `dc-hid-poc`, `clock-server`.
- `stream_monitor.html`
  - операторський тестовий UI (media + HID control field + clock).
- `clock_sync.html`
  - об'єктивний вимір E2E latency.

Висновок AS-IS:
- Експериментально доведені media + control контури.
- Відсутній жорсткий модульний поділ control plane/data plane/event plane.

## 3. TO-BE: цільові модулі платформи

Цільова система має бути компонентною і plugin-oriented.

Базові модулі:

1. `HidBridge.ControlPlane`
- AuthN/AuthZ, RBAC, tenant isolation.
- Inventory, policy, sharing rules.

2. `HidBridge.SessionOrchestrator`
- lifecycle сесій (open/arm/active/recover/close).
- arbitration доступу (owner/controller/observer).

3. `HidBridge.ConnectorHost`
- життєвий цикл агентів/конекторів.
- plugin loader для installed/agentless/hid/media connector.

4. `HidBridge.Connectors.HidBridgeUart`
- реалізація HID bridge transport (COM/UART v2 + layout/descriptor).
- мапінг `command.request` -> HID report injection.

5. `HidBridge.MediaGateway`
- capture, encode, publish/subscribe (WHIP/WHEP/WebRTC).
- profile management (`ultra-low-latency`, `balanced`, `quality-first`).

6. `HidBridge.Eventing`
- unified event bus + audit log + telemetry timeline.

7. `HidBridge.Observability`
- metrics, traces, alerts, diagnostics API.

8. `HidBridge.OperatorUI`
- єдина панель: список ПК, статуси, активні сесії, події, sharing.

## 4. Agent model у TO-BE

`Connector` реалізує Agent Contract v1 і реєструється в `ConnectorHost`.

Типи конекторів:
- `installed`: сервіс на endpoint.
- `agentless`: remote execution без сервісу на endpoint.
- `hid_bridge`: керування через RP2040 chain + UART.
- `media_gateway`: окремий виконавець media-функцій.

Ключова перевага:
- додавання нового типу керування = додавання нового connector plugin, без переписування всього backend.

## 5. Карта відповідності AS-IS -> TO-BE

| AS-IS компонент | TO-BE модуль | Рішення | Коментар |
|---|---|---|---|
| `Firmware/src/B_host` control UART | `Connectors.HidBridgeUart` (device side contract) | Keep + harden | Це канонічний апаратний контур |
| `Firmware/src/A_device` HID device | `Connectors.HidBridgeUart` (target injection backend) | Keep | Основа agentless HID |
| `Tools/Server/HidControlServer/HidUartClient.cs` | `Connectors.HidBridgeUart` | Extract + refactor | Винести з моноліту в окремий пакет |
| `Tools/Server/HidControlServer/Endpoints/*` | `ControlPlane API` + `SessionOrchestrator API` | Split | Рознести endpoint-и по bounded context |
| `WebRtcTests/exp-022-datachanneldotnet/Program.cs` (dc-hid-poc) | `ConnectorHost` + `Connectors.HidBridgeUart` | Re-implement as service | `exp-022` лишити R&D only |
| `WebRtcTests/exp-022-datachanneldotnet/Program.cs` (dc-whip) | `MediaGateway` | Reuse algorithms, rewrite runtime | Окремий production runtime |
| `stream_monitor.html` | `OperatorUI` module | Port UI logic | Додати RBAC, inventory, audit panels |
| `clock_sync.html` | `OperatorUI diagnostics` | Keep as diagnostic widget | Вбудувати в latency panel |
| `Tools/Shared/HidControl.Contracts` | `HidBridge.Contracts` | Reuse + expand | Основа для Agent Contract DTO |
| `Tools/Shared/HidControl.ClientSdk` | `HidBridge.SDK` | Reuse patterns | Стандартизувати transport adapters |

## 6. Пропонована структура рішень (орієнтир)

```text
Tools/
  Platform/
    HidBridge.ControlPlane/
    HidBridge.SessionOrchestrator/
    HidBridge.ConnectorHost/
    HidBridge.MediaGateway/
    HidBridge.Eventing/
    HidBridge.Observability/
    HidBridge.OperatorUI/
  Connectors/
    HidBridge.Connectors.HidBridgeUart/
    HidBridge.Connectors.Agentless.WinRM/
    HidBridge.Connectors.Agentless.SSH/
    HidBridge.Connectors.Installed.Windows/
  Shared/
    HidBridge.Contracts/
    HidBridge.SDK/
    HidBridge.Abstractions/
Firmware/
  (залишається окремо, з versioned protocol compatibility matrix)
WebRtcTests/
  exp-022-datachanneldotnet/
  (лише інтеграційна лабораторія)
```

## 7. Системні контракти між модулями

Мінімум контрактів:

- `IConnector`
  - `RegisterAsync`, `HeartbeatAsync`, `ExecuteAsync`, `GetCapabilitiesAsync`

- `ISessionOrchestrator`
  - `OpenSession`, `ArmSession`, `CloseSession`, `RecoverSession`

- `IMediaGateway`
  - `StartPublish`, `StopPublish`, `Subscribe`, `GetMediaMetrics`

- `IEventWriter`
  - `WriteAudit`, `WriteTelemetry`, `WriteCommandLifecycle`

- `IInventoryService`
  - `UpsertEndpoint`, `AttachAgent`, `SetEndpointState`, `QueryEndpoints`

## 8. End-to-end потік (цільовий)

Сценарій: оператор керує target PC мишкою/клавіатурою і бачить AV-стрім.

1. `OperatorUI` відкриває сесію через `SessionOrchestrator`.
2. `SessionOrchestrator` резервує:
- control connector (`HidBridgeUart`)
- media connector (`MediaGateway`)
3. `MediaGateway` підіймає publish/subscribe маршрут.
4. `OperatorUI` відправляє `command.request` (`mouse.move`, `keyboard.press`).
5. `ConnectorHost` маршрутизує команду у `HidBridgeUart`.
6. `HidBridgeUart` формує HID report і відправляє через UART v2.
7. ACK/NACK повертається як `command.ack`.
8. Всі події фіксуються в `Eventing` і доступні в timeline/audit.

## 9. Системні ризики та контроль

1. Ризик: ACK не гарантує реальне застосування на target.
- Контроль: ввести `command.applied` (v1.1), device-side/observer feedback.

2. Ризик: regression latency при додаткових шарах.
- Контроль: performance budget per module, zero-copy де можливо.

3. Ризик: нестабільність mixed transport (WS + DataChannel + UART).
- Контроль: transport abstraction + fault-injection тести.

4. Ризик: schema drift між компонентами.
- Контроль: central contracts package + CI schema validation.

## 10. Міграція по хвилях

## Хвиля M0 (Foundation)
- Виділити `HidBridge.Contracts` з Agent Contract v1 DTO.
- Підключити schema validation у CI.

## Хвиля M1 (Connector extraction)
- Винести HID UART runtime із `HidControlServer`/`exp-022` у `Connectors.HidBridgeUart`.
- Закріпити підтримку autodetect interface + report layout fallback.

## Хвиля M2 (Session orchestration)
- Реалізувати `SessionOrchestrator` з state machine.
- Ввести session-level recovery policy.

## Хвиля M3 (Media separation)
- Винести media runtime в `MediaGateway`.
- Уніфікувати профілі як policy object.

## Хвиля M4 (Eventing + Observability)
- Єдина audit/telemetry timeline.
- SLO dashboards: latency, freeze, audio-loss, hid-failure.

## Хвиля M5 (Operator UI)
- Inventory view (всі ПК + стани).
- Session/share controls.
- Event explorer (фільтри по endpoint/session/agent).

## 11. DoD для нової модульної архітектури

1. Можна одночасно підключити mixed-парк з різними connector type.
2. Для кожного endpoint видно:
- capabilities
- health
- активні/історичні сесії
- детальний event trail

3. Керування мишею/клавіатурою і AV потоки працюють як одна сесія з контрольованою затримкою.
4. Новий connector додається без змін у core orchestration logic.

## 12. Практичний next step (конкретно для цього репозиторію)

1. Створити `Tools/Connectors/HidBridge.Connectors.HidBridgeUart` і перенести туди UART/HID logic.
2. Створити `Tools/Platform/HidBridge.SessionOrchestrator` з мінімальними API `open/close/recover`.
3. У `exp-022` лишити тільки інтеграційні сценарії та load/latency тести.
4. Підключити Agent Contract schema validation у runtime прийому команд.


## Додаток A. Оновлення стану `2026-03-02`

У greenfield-частині архітектури затверджено окремий thin web shell:
- `Platform/Clients/HidBridge.ControlPlane.Web`
- `Blazor Web App`
- thin identity shell with cookie session, optional OIDC, baseline authorization policies, and tenant/org seams
- `Tailwind CSS` browser baseline
- operator actions у `Session Details`:
  - `request control`
  - `release control`
  - `force takeover`
  - `approve invitation`
  - `decline invitation`
- localization baseline:
  - `en` default
  - `uk` secondary
  - browser locale auto-detection
  - manual locale override in settings
  - manual theme override in settings is tracked as an open web-shell stabilization issue
  - authorization-aware UX baseline:
    - current operator / roles / tenant / organization
    - moderation/control allow/deny reasoning
  - responsive operator layout aligned with `landing/index.html`
  - core layout styling no longer depends on a delayed Tailwind runtime refresh

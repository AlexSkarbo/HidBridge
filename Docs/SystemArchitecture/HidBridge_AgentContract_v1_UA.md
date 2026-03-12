# HidBridge Agent Contract v1 (UA)

Версія: `1.0-draft`  
Дата: `2026-02-27`  
Статус: `Draft for implementation`  
Пов'язані документи:  
- `Docs/SystemArchitecture/HidBridge_SystemArchitecture_UA.md`  
- `Docs/SystemArchitecture/HidBridge_SystemArchitecture_UA_Executive.md`  
- `Docs/ws_control_protocol.md`  
- `Docs/uart_control_protocol.md`  
- `Docs/SystemArchitecture/schemas/HidBridge_AgentContract_v1_Message.schema.json`

## 1. Мета документа

Цей документ формалізує контракт взаємодії для системи з великою кількістю різних виконавців керування (агентів), включно з agentless-сценаріями.

Цілі:
- уніфікувати поняття `agent` для всієї платформи;
- зафіксувати єдиний формат повідомлень (JSON schema);
- визначити state machine для життєвого циклу агентів, сесій і команд;
- зафіксувати єдину таксономію помилок для UI/логів/автоматичних ретраїв.

## 2. Узгоджені терміни

- `Endpoint`: керований або спостережуваний ресурс (PC/сервер/девайс).
- `Agent`: виконавець стандартизованого контракту керування endpoint.
- `Installed Agent`: локальний сервіс на endpoint.
- `Agentless Connector`: віддалений виконавець без локального сервісу на endpoint (WinRM/SSH/WMI/HID bridge gateway/API тощо).
- `Connector`: конкретна реалізація Agent Contract (installed або agentless).
- `Control Plane`: автентифікація, інвентаризація, RBAC, оркестрація сесій, audit.
- `Data Plane`: фактичні потоки даних (HID, media, файли, shell).
- `Session`: контекст активного доступу/керування endpoint.
- `Capability`: оголошена можливість (наприклад, `hid.mouse`, `media.webrtc.recv`).

Ключове узгодження:
- `agent` у цій системі — це не обов'язково локальний процес. Це контрактний виконавець можливостей endpoint.

## 3. Принципи Agent Contract v1

1. Єдиний envelope для всіх повідомлень.
2. Явна версія контракту (`v`).
3. Кореляція через `traceId`, `messageId`, `correlationId`.
4. Idempotency для команд через `idempotencyKey`.
5. Детальна error-модель (домен + код + retry policy).
6. Транспорт-агностичність:
- один payload може йти через WebSocket, DataChannel, gRPC або message bus.

## 4. Нормативні сутності (мінімум)

- `tenantId`: простір ізоляції.
- `endpointId`: стабільний ID цільового ПК/девайса.
- `agentId`: ID конкретного виконавця контракту.
- `connectorType`: `installed` | `agentless` | `hid_bridge` | `media_gateway`.
- `sessionId`: контекст керування.
- `commandId`: команда в рамках сесії.

## 5. Capability модель

Кожен агент у `agent.register` оголошує список capability з версіями.

Приклади capability:
- `hid.mouse.v1`
- `hid.keyboard.v1`
- `media.webrtc.publish.v1`
- `media.whep.play.v1`
- `telemetry.stream.v1`
- `discovery.interfaces.v1`
- `diag.report_layout.v1`

Правило:
- Оркестратор не відправляє команду, якщо capability відсутня.

## 6. Формат повідомлень (Envelope)

Базові поля envelope:
- `v`: версія контракту, string (`"1.0"`)
- `kind`: тип повідомлення (`agent.register`, `command.request`, `command.ack`, ...)
- `messageId`: унікальний ID повідомлення
- `traceId`: наскрізна кореляція
- `correlationId`: посилання на попереднє повідомлення (для ACK/NACK/response)
- `ts`: UTC timestamp (`date-time`)
- `tenantId`: tenant
- `endpointId`: endpoint
- `agentId`: агент/конектор
- `body`: payload конкретного `kind`

## 7. JSON Schema

Нормативна схема:
- `Docs/SystemArchitecture/schemas/HidBridge_AgentContract_v1_Message.schema.json`

Схема покриває:
- `agent.register`
- `agent.heartbeat`
- `session.open`
- `session.close`
- `command.request`
- `command.ack`
- `event.telemetry`
- `event.audit`

Приклад `command.request`:

```json
{
  "v": "1.0",
  "kind": "command.request",
  "messageId": "msg_01JXYZ",
  "traceId": "trc_7b9f5e",
  "ts": "2026-02-27T20:10:00Z",
  "tenantId": "tenant_main",
  "endpointId": "pc_192_168_0_140",
  "agentId": "agent_hidbridge_com6",
  "body": {
    "commandId": "cmd_10442",
    "sessionId": "ses_8f4b",
    "channel": "hid",
    "action": "mouse.move",
    "args": { "dx": 14, "dy": -3 },
    "timeoutMs": 3000,
    "idempotencyKey": "mouse-move-10442"
  }
}
```

Приклад `command.ack`:

```json
{
  "v": "1.0",
  "kind": "command.ack",
  "messageId": "msg_01JXYZ_ACK",
  "correlationId": "msg_01JXYZ",
  "traceId": "trc_7b9f5e",
  "ts": "2026-02-27T20:10:00.091Z",
  "tenantId": "tenant_main",
  "endpointId": "pc_192_168_0_140",
  "agentId": "agent_hidbridge_com6",
  "body": {
    "commandId": "cmd_10442",
    "status": "accepted",
    "error": null,
    "metrics": {
      "agentQueueMs": 3,
      "deviceAckMs": 79
    }
  }
}
```

## 8. State machine

## 8.1 Agent lifecycle

```text
DISCOVERED
  -> REGISTERING
    -> ONLINE
      -> DEGRADED
        -> ONLINE
      -> OFFLINE
        -> REGISTERING
      -> RETIRED
```

Переходи:
- `DISCOVERED -> REGISTERING`: знайдено endpoint/connector.
- `REGISTERING -> ONLINE`: валідний `agent.register` + policy allow.
- `ONLINE -> DEGRADED`: heartbeat є, але capability/health degraded.
- `ONLINE -> OFFLINE`: heartbeat timeout.
- `OFFLINE -> REGISTERING`: reconnect.
- `ONLINE|DEGRADED|OFFLINE -> RETIRED`: деактивація адміністратором.

## 8.2 Session lifecycle

```text
REQUESTED
  -> PREPARING
    -> ARMING
      -> ACTIVE
        -> RECOVERING
          -> ACTIVE
        -> TERMINATING
          -> ENDED
      -> FAILED
```

Пояснення:
- `PREPARING`: резерв ресурсів, перевірка capability.
- `ARMING`: транспортна ініціалізація (WS/DC/WHIP/WHEP/UART).
- `ACTIVE`: нормальна робота.
- `RECOVERING`: reconnect/restart підконтурів без втрати сесії.
- `FAILED`: сесію не вдалося підняти до `ACTIVE`.

## 8.3 Command lifecycle

```text
CREATED
  -> ENQUEUED
    -> SENT
      -> ACCEPTED
        -> APPLIED
      -> REJECTED
      -> TIMEOUT
```

Примітка для HID:
- Поточний UART ACK найчастіше означає `ACCEPTED`.
- `APPLIED` потребує окремого критерію (наприклад, фідбек device-side або ефект-детектор у higher layer).

## 9. Таксономія помилок (Error Model)

Формат error:
- `domain`: `AUTH|AGENT|SESSION|COMMAND|MEDIA|HID|UART|TRANSPORT|SYSTEM`
- `code`: стабільний символьний код
- `message`: діагностичний текст
- `retryable`: bool
- `details`: object

Обов'язкові коди v1:

- AUTH
  - `E_AUTH_INVALID_TOKEN`
  - `E_AUTH_FORBIDDEN`

- AGENT
  - `E_AGENT_NOT_REGISTERED`
  - `E_AGENT_CAPABILITY_MISSING`
  - `E_AGENT_HEARTBEAT_TIMEOUT`

- SESSION
  - `E_SESSION_NOT_FOUND`
  - `E_SESSION_NOT_ACTIVE`
  - `E_SESSION_CONFLICT`
  - `E_SESSION_ARM_TIMEOUT`

- COMMAND
  - `E_COMMAND_INVALID_PAYLOAD`
  - `E_COMMAND_UNSUPPORTED`
  - `E_COMMAND_TIMEOUT`
  - `E_COMMAND_IDEMPOTENCY_CONFLICT`

- HID/UART
  - `E_HID_INTERFACE_NOT_FOUND`
  - `E_HID_LAYOUT_MISSING`
  - `E_HID_REPORT_INVALID`
  - `E_UART_LINK_DOWN`
  - `E_UART_ACK_TIMEOUT`
  - `E_UART_DEVICE_ERROR_0x01`
  - `E_UART_DEVICE_ERROR_0x02`
  - `E_UART_DEVICE_ERROR_0x03`
  - `E_UART_DEVICE_ERROR_0x04`

- MEDIA
  - `E_MEDIA_PUBLISH_FAILED`
  - `E_MEDIA_SUBSCRIBE_FAILED`
  - `E_MEDIA_FREEZE_DETECTED`
  - `E_MEDIA_AUDIO_LOST`

- TRANSPORT
  - `E_WS_CONNECT_FAILED`
  - `E_DC_OPEN_TIMEOUT`
  - `E_HTTP_LISTENER_BIND_FAILED`

Карта firmware error byte -> contract code:
- `0x01` -> `E_UART_DEVICE_ERROR_0x01` (`bad length`)
- `0x02` -> `E_UART_DEVICE_ERROR_0x02` (`inject failed`)
- `0x03` -> `E_UART_DEVICE_ERROR_0x03` (`report descriptor missing`)
- `0x04` -> `E_UART_DEVICE_ERROR_0x04` (`report layout missing`)

## 10. Правила retry / recovery

- `retryable=true`:
  - екпоненційний backoff `200ms -> 500ms -> 1s -> 2s`, максимум 5 спроб.
- `retryable=false`:
  - негайний fail + `event.audit`.
- Для `E_UART_DEVICE_ERROR_0x04`:
  - 1) `GET_REPORT_LAYOUT` (з reportId scan)
  - 2) оновити cached layout
  - 3) повторити команду 1 раз

## 11. Безпека контракту

- mTLS або signed JWT між компонентами control plane.
- Role-based check перед `command.request`.
- Псевдонімізація чутливих полів в event stream.
- `idempotencyKey` + `messageId` для anti-replay на рівні сесії.

## 12. Observability вимоги

Кожне повідомлення повинно мати:
- `traceId`
- `messageId`
- `sessionId` (якщо є сесія)
- `endpointId`
- `agentId`

Мінімальні метрики:
- `command_accept_latency_ms`
- `command_apply_latency_ms` (коли доступно)
- `session_recoveries_total`
- `agent_heartbeat_miss_total`
- `media_freeze_events_total`
- `uart_ack_timeout_total`

## 13. Versioning і сумісність

- SemVer для контракту:
  - `1.x` backward-compatible.
  - `2.0` лише при breaking changes.
- Нові поля додаються опційно.
- `kind` розширюється тільки додаванням нових значень.

## 14. Мінімальні acceptance criteria для запуску v1

1. Реєстрація mixed-парку:
- мінімум 1 installed agent
- мінімум 1 agentless connector
- мінімум 1 HID bridge connector

2. Оркестрація:
- `session.open -> active` стабільно
- контрольні команди `mouse.move`, `keyboard.press`, `keyboard.shortcut` проходять через `command.request/ack`

3. Telemetry:
- у timeline видно повний ланцюг: `request -> ack -> audit event`

4. Помилки:
- усі runtime-помилки мапляться в стандартизований `domain+code`

## 15. Що робимо в наступній ітерації (v1.1)

- Додати `command.applied` для device-verified execution.
- Додати quality-of-service policy на рівні session profile (`ultra-low-latency`, `balanced`, `quality-first`).
- Додати стандартизовані `share.invite`/`share.grant`/`share.revoke` події.

## Оновлення стану `2026-03-02`

1. Agent contract уже обслуговує control-plane projections, diagnostics і collaboration flows.
2. Наступний рівень розвитку контракту:
- identity claims propagation
- tenant/org claims propagation
- policy evaluation context для web/operator сценаріїв

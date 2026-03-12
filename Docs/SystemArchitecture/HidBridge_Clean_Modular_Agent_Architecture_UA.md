# HidBridge — Чиста Модульна Агентна Архітектура (UA)

Версія: `1.0-draft`  
Дата: `2026-02-27`  
Статус: `Target architecture baseline`

Пов'язані документи:
- `Docs/SystemArchitecture/HidBridge_SystemArchitecture_UA.md`
- `Docs/SystemArchitecture/HidBridge_AgentContract_v1_UA.md`
- `Docs/SystemArchitecture/HidBridge_ModuleArchitecture_Map_UA.md`
- `Docs/SystemArchitecture/schemas/HidBridge_AgentContract_v1_Message.schema.json`

## 1. Ціль

Побудувати розширювану платформу, де:
- є багато типів агентів (installed, agentless, hardware bridge, media)
- оператор бачить всі комп'ютери та їх статуси в єдиному інвентарі
- можна керувати, спільно працювати, ділитися доступом
- є повна подієва прозорість (audit + telemetry)
- система еволюціонує без архітектурного боргу

Цільова UX-модель: **Google Meet-подібна сесія**, але з додатковими керуючими можливостями через багато агентів.

## 2. Архітектурні принципи

1. Contract-first: жодної інтеграції без версіонованого контракту.
2. Clean boundaries: домен, use cases, порти, адаптери, інфраструктура.
3. Multi-tenant by design: tenant isolation на всіх шарах.
4. Event-sourced observability: все важливе стає подією.
5. Connector plugin model: нові агенти додаються без змін у ядро.
6. Control plane і data plane розділені.
7. Failure isolation: деградація модуля не валить всю сесію.

## 3. Логічна макроархітектура

```text
[Web / Desktop / Mobile Clients]
  -> [API Gateway + Auth]
  -> [Control Plane]
      -> Identity & RBAC
      -> Inventory & Agent Registry
      -> Session Orchestrator
      -> Share/Collaboration Service
      -> Policy Engine
  -> [Connector Host]
      -> Installed Agent Connector
      -> Agentless Connector (WinRM/SSH/WMI/...)
      -> HID Bridge Connector (RP2040 chain)
      -> Automation Connector (scripts/jobs)
  -> [Realtime Plane]
      -> Signaling Service
      -> Media SFU Gateway Cluster
      -> DataChannel Relay / WS Relay
  -> [Eventing + Observability]
      -> Event Bus
      -> Audit Log
      -> Metrics/Tracing/Alerts
```

## 4. Bounded Contexts

## 4.1 Identity & Access

Відповідальність:
- SSO/OIDC
- users, teams, service principals
- RBAC/ABAC
- short-lived session tokens

Ключові сутності:
- User
- Role
- Permission
- Token

## 4.2 Tenant & Organization

Відповідальність:
- tenant isolation
- org structure
- quotas/licensing

Ключові сутності:
- Tenant
- OrganizationUnit
- SubscriptionPlan

## 4.3 Inventory & Agent Registry

Відповідальність:
- каталог endpoint-ів
- реєстрація агентів і capability
- heartbeat/health

Ключові сутності:
- Endpoint
- AgentInstance
- Capability
- HealthSnapshot

## 4.4 Session Orchestrator

Відповідальність:
- lifecycle сесій
- participant roles
- recovery policy
- arbitration доступу

Ключові сутності:
- Session
- Participant
- SessionPolicy
- SessionState

## 4.5 Collaboration Service (Meet-подібна модель)

Відповідальність:
- кімнати/дзвінки
- presence
- invite/share/grant/revoke
- handoff control між операторами

Ключові сутності:
- Room
- Invite
- ShareGrant
- PresenceState

## 4.6 Media Realtime (SFU)

Відповідальність:
- WebRTC signaling
- SFU маршрутизація медіа
- adaptive bitrate, simulcast/SVC
- recording hooks (опційно)

Ключові сутності:
- MediaSession
- Track
- Publisher
- Subscriber

## 4.7 Control Realtime (HID/Data)

Відповідальність:
- low-latency input events
- command ACK/NACK lifecycle
- idempotency

Ключові сутності:
- Command
- CommandAck
- ControlChannel

## 4.8 Connector Host

Відповідальність:
- lifecycle plugin-конекторів
- sandboxing/quotas
- connector health

Ключові сутності:
- ConnectorPackage
- ConnectorRuntime
- ConnectorLease

## 4.9 Eventing & Observability

Відповідальність:
- єдиний event stream
- audit immutable trail
- traces/metrics/logs correlation

Ключові сутності:
- DomainEvent
- AuditEvent
- MetricSeries
- TraceSpan

## 5. Clean Architecture шари

Кожен bounded context реалізується однаково:

1. Domain
- сутності, value objects, domain rules

2. Application
- use cases, orchestration, transaction boundaries

3. Ports
- інтерфейси до зовнішнього світу

4. Adapters
- HTTP/WS/gRPC handlers
- DB adapters
- external connectors

5. Infrastructure
- runtime hosting, message bus, storage, cache

Правило залежностей:
- outer layers залежать від inner layers, але не навпаки.

## 6. Агентна модель платформи

## 6.1 Типи агентів

1. Installed Agent
- сервіс на endpoint
- максимальні capability

2. Agentless Connector
- доступ через існуючі протоколи
- мінімальна інвазивність

3. HID Bridge Connector
- hardware path через RP2040 B_host/A_device
- для agentless фізичного HID контролю

4. Media Connector
- capture/encode/publish runtime

5. Automation Connector
- запуск workflow/job/remote script

## 6.2 Життєвий цикл агента

```text
DISCOVERED -> REGISTERING -> ONLINE -> DEGRADED -> OFFLINE -> RETIRED
```

## 6.3 Capability negotiation

Кожен агент під час register декларує capability:
- `hid.mouse.v1`
- `hid.keyboard.v1`
- `media.publish.v1`
- `media.subscribe.v1`
- `shell.exec.v1`
- `file.transfer.v1`
- `diag.telemetry.v1`

Session Orchestrator не відкриває режим, який не покритий capability.

## 7. Meet-подібний режим + керування endpoint

## 7.1 Режими учасників

- Owner
- Controller
- Observer
- Presenter

## 7.2 Сценарії

1. One-to-one remote control
- оператор керує endpoint і отримує AV

2. Shared session
- один controller, кілька observers
- controlled handoff controller role

3. Multi-endpoint operations room
- один оператор бачить кілька endpoint у unified wallboard
- окремі control leases per endpoint

## 7.3 Логічні канали

- Media channel (WebRTC SFU)
- Control channel (DataChannel/WS)
- Event channel (server-sent telemetry/audit)

## 8. Data model (мінімально необхідний)

- Tenants
- Users
- Endpoints
- Agents
- Capabilities
- Sessions
- SessionParticipants
- Commands
- CommandAcks
- Rooms
- ShareInvites
- EventsAudit
- EventsTelemetry

Індекси must-have:
- `Events*` за `tenantId + endpointId + ts`
- `Commands` за `sessionId + commandId`
- `Agents` за `endpointId + status`

## 9. API-контракти рівня платформи

## 9.1 Control Plane API

- `POST /api/v1/sessions`
- `POST /api/v1/sessions/{id}/close`
- `POST /api/v1/sessions/{id}/share`
- `GET /api/v1/endpoints`
- `GET /api/v1/endpoints/{id}/agents`
- `GET /api/v1/events`

## 9.2 Realtime Signaling API

- `WS /rt/v1/signaling`
- `WS /rt/v1/control`

## 9.3 Agent Contract API

- `agent.register`
- `agent.heartbeat`
- `command.request`
- `command.ack`
- `event.telemetry`
- `event.audit`

(Визначено в `HidBridge_AgentContract_v1_UA.md`)

## 10. Розгортання і масштабування

## 10.1 Базовий production layout

1. Control cluster
- API Gateway
- Identity
- Orchestrator
- Registry

2. Realtime cluster
- Signaling nodes
- SFU nodes
- Control relay nodes

3. Connector workers
- Horizontal pool
- sticky lease per endpoint/session

4. Data layer
- Postgres (OLTP)
- Redis (cache + ephemeral state)
- Event store (Kafka/NATS/append log)
- Object storage (recordings/artifacts)

## 10.2 Масштабування

- горизонтальне по tenant/region
- auto-scaling SFU по active tracks
- connector auto-scaling по active sessions

## 11. Відмовостійкість

1. Session recovery policy
- reconnect медіа
- reconnect control
- re-arm HID layout

2. Circuit breakers
- per connector type
- per endpoint

3. Retry policy
- command-level retry за error taxonomy
- bounded retries + jitter

4. Degraded modes
- media-only
- control-only
- observer-only

## 12. Безпека

1. Zero-trust між сервісами (mTLS + JWT claims).
2. Tenant-isolated storage і ключі.
3. Fine-grained permissions на share/control.
4. Immutable audit trail для всіх контрольних дій.
5. Secret rotation для connector credentials.

## 13. Що робимо з поточним репозиторієм

## 13.1 Залишаємо

- Firmware RP2040 контур як hardware foundation.
- UART v2 протокол як контрольний transport для HID bridge.
- напрацювання `Tools/Shared` як seed для нових contracts/abstractions.

## 13.2 Переносимо в нові модулі

- `HidUartClient`/layout logic -> `Connectors.HidBridgeUart`
- session runtime -> `Platform.SessionOrchestrator`
- media runtime -> `Platform.MediaGateway`
- ws/dc control ingress -> `Platform.ControlGateway`

## 13.3 Лишаємо в R&D

- `WebRtcTests/exp-022-datachanneldotnet` як лабораторію сумісності/latency/soak.

## 14. План впровадження (модульно)

## Фаза P0 (Foundation)

1. Підняти `HidBridge.Contracts` з Agent Contract DTO + schema validation.
2. Впровадити Event Envelope у всіх нових API.
3. Визначити єдиний error catalog.

## Фаза P1 (Control path)

1. Запустити `ConnectorHost` + `HidBridgeUart Connector`.
2. Під'єднати `SessionOrchestrator` до control path.
3. Зібрати end-to-end command lifecycle telemetry.

## Фаза P2 (Meet-like realtime)

1. Підняти окремий signaling service.
2. Інтегрувати SFU media cluster.
3. Реалізувати shared sessions (owner/controller/observer).

## Фаза P3 (Scale + Marketplace)

1. Plugin SDK для нових connector type.
2. Capability marketplace/registry.
3. Policy templates для enterprise rollout.

## 15. Definition of Done для архітектури

1. Новий агент додається як plugin без змін у core orchestration.
2. В одній панелі видно всі endpoint-і, агенти, сесії, події.
3. Meet-подібна сесія + endpoint control працюють одночасно.
4. Повний trace для кожної дії: UI -> command -> ack -> effect/telemetry.
5. Підтримується mixed fleet: installed + agentless + hid_bridge.

## 16. Ключовий архітектурний висновок

Правильна цільова модель для HidBridge:
- **не один великий сервер** і не один тип агента,
- а **платформа з чистими модулями та агентним контрактом**,
- де realtime collaboration (як у Meet) і remote control є частинами єдиної системи.

## 17. Dashboard / UI принципи

На поточному етапі затверджено такий порядок реалізації операторського dashboard:

1. Спочатку реалізуються backend projections / read models.
2. Потім реалізується dashboard API.
3. Лише після цього створюється thin operator UI.

Поточний статус реалізації:

1. Collaboration read models уже реалізовані.
2. Dashboard API baseline уже реалізований для:
   - fleet inventory
   - audit diagnostics
   - telemetry diagnostics
3. Optimized query projection API baseline уже реалізований для:
   - sessions
   - endpoints
   - audit
   - telemetry
4. Replay/archive diagnostics baseline уже реалізований для:
   - session replay bundle
   - archive summary
   - archive audit slices
   - archive telemetry slices
   - archive command slices
5. Наступний крок:
   - thin operator UI shell
   - operator actions baseline у session details view

Причини:

1. браузерний клієнт не повинен збирати складний state з великої кількості сирих endpoint-ів;
2. UI повинен працювати з готовими query-моделями, оптимізованими під операторські сценарії;
3. це зменшує coupling між UI і доменною логікою;
4. це спрощує масштабування і подальшу оптимізацію query layer.

Цільова модель UI/UX:

1. `operator console`, а не marketing-style front-end;
2. висока щільність корисної інформації;
3. мінімум декоративних елементів і анімацій;
4. явне відображення:
   - owner
   - active controller
   - pending approvals
   - endpoint/session health
   - degraded / failed states

Перші цільові екрани:

1. `Fleet Overview`
2. `Session Details`
3. `Audit Dashboard`
4. `Telemetry Dashboard`
5. `Control Operations`

## 18. Оновлення стану `2026-03-02` — Web / Identity / Tenant

### 18.1 Web shell

Затверджено такий web stack для thin operator UI shell:
- `Blazor Web App`
- `Tailwind CSS` browser baseline

Поточний baseline thin operator UI shell уже включає:
- `Fleet Overview`
- `Session Details`
- `Audit Dashboard`
- `Telemetry Dashboard`
- `Settings`
- operator actions:
  - `request control`
  - `release control`
  - `force takeover`
  - `approve invitation`
  - `decline invitation`
- localization baseline:
  - `en` default
  - `uk` secondary
  - browser locale auto-detection
  - manual locale override через settings
- візуальна мова UI синхронізується з `landing/index.html`:
  - light/dark themes
  - responsive layout
  - glass/grid styling

Обрано server-side модель, щоб не зберігати довгоживучі bearer-token у браузері і не дублювати доменну orchestration-логіку на клієнті.

### 18.2 Identity & Access

Поточне архітектурне рішення:
- `HidBridge.ControlPlane.Web` не виконує роль identity server;
- централізований SSO/Identity повинен існувати окремо від web shell;
- baseline implementation already exists in `Platform/Clients/HidBridge.ControlPlane.Web`:
  - cookie-backed web session
  - optional `OIDC` challenge flow
  - development operator fallback identity
  - baseline policies: `OperatorViewer`, `OperatorModerator`, `OperatorAdmin`
  - tenant/org seams projected from claims
- dev baseline централізованого IdP:
  - `Keycloak`
  - локальний `username/password`
  - зовнішні identity providers (`Google`, `Microsoft`, `GitHub`, інші) через federation
- для human users рекомендовано `OIDC Authorization Code + server cookie session`;
- для machine-to-machine взаємодії потрібні окремі `service principals`;
- для operator/control authorization потрібна комбінація:
  - `RBAC` для базових ролей;
  - `ABAC` для tenant/org/session/control умов.

Рекомендовані базові ролі:
- `PlatformAdmin`
- `OrgAdmin`
- `FleetOperator`
- `SessionModerator`
- `Observer`
- `ServicePrincipal`

### 18.3 Tenant & Organization

Поточне архітектурне рішення:
- baseline implementation already exists in `Platform/Clients/HidBridge.ControlPlane.Web`:
  - cookie-backed web session
  - optional `OIDC` challenge flow
  - development operator fallback identity
  - baseline policies: `OperatorViewer`, `OperatorModerator`, `OperatorAdmin`
  - tenant/org seams projected from claims
- повний multi-tenant enforcement не варто вводити раніше, ніж thin operator UI shell стабілізується;
- але tenant-aware claims, scopes і policy seams повинні бути враховані вже зараз;
- tenant isolation має бути введений до production rollout.

Рекомендована послідовність:
1. thin operator UI shell;
2. `Identity & Access baseline`;
3. tenant/org-aware authorization policies;
4. quotas/licensing/billing.


Оновлення web shell / Identity baseline:
- коли `Identity:Enabled=false`, development fallback identity тепер автентифікується автоматично без ручного login flow;
- ручний theme switch ще не можна вважати стабільним; проблему винесено в `Docs/SystemArchitecture/HidBridge_ControlPlane_Web_Design_Questions_UA.md`;
- `html, body` background зроблено напівпрозорим, щоб grid background із `landing/index.html` читався стабільно.
- поточний practical focus у thin web shell:
  - authorization-aware UX;
  - явне відображення current operator / roles / tenant / organization;
  - пояснення причин, чому moderation/control дії дозволені або недоступні.


Оновлення стану `2026-03-03`:
- login/logout між `HidBridge.ControlPlane.Web` і `Keycloak` перевірено локально;
- централізований SSO baseline підтверджено практично;
- наступний practical step: `Google` як перший external IdP у `Keycloak`, далі той самий onboarding pattern для інших провайдерів.

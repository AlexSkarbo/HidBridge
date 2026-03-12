# HidBridge — Поетапний план впровадження persistence layer (UA)

Оновлено: `2026-03-03`
Статус: `draft / working baseline`

## 1. Мета документа

Цей документ фіксує покроковий план переходу від поточного `in-memory` та тимчасового `file-based` persistence до цільового `SQL-backed persistence` у новій платформі `Platform/`.

Документ також явно відмічає, що вже реалізовано, а що ще потрібно зробити.

## 2. Контекст

Для цільової системи HidBridge недостатньо лише файлового зберігання. Платформа повинна підтримувати:

1. багато агентів;
2. agentless-конектори;
3. inventory усіх комп'ютерів;
4. сесії керування та спільного доступу;
5. аудит і telemetry;
6. розширення новими типами агентів без переробки ядра;
7. узгоджений control plane для майбутньої Meet-подібної collaboration-моделі.

Тому persistence layer має бути двошаровим:

1. `File persistence` — як локальний dev/test fallback.
2. `SQL persistence` — як основний production/staging backend.

## 3. Що вже зроблено

### 3.1 Архітектурний baseline

- [x] Виділено новий greenfield-root: `Platform/`
- [x] Зафіксовано стратегічне рішення: legacy залишається в `Tools/`, нова система розвивається окремо
- [x] Описано clean modular agent architecture
- [x] Описано Agent Contract v1
- [x] Описано module map та migration map

### 3.2 P0 skeleton

- [x] Створено solution: `Platform/HidBridge.Platform.sln`
- [x] Створено базові модулі:
  - `Platform/Shared/HidBridge.Contracts`
  - `Platform/Shared/HidBridge.Abstractions`
  - `Platform/Shared/HidBridge.Hid`
  - `Platform/Shared/HidBridge.Transport.Uart`
  - `Platform/Core/HidBridge.Domain`
  - `Platform/Core/HidBridge.Application`
  - `Platform/Platform/HidBridge.ConnectorHost`
  - `Platform/Platform/HidBridge.SessionOrchestrator`
  - `Platform/Platform/HidBridge.ControlPlane.Api`
  - `Platform/Connectors/HidBridge.Connectors.HidBridgeUart`

### 3.3 API / runtime baseline

- [x] Підключено `OpenAPI`
- [x] Підключено `Scalar`
- [x] Додано описи endpoint'ів
- [x] Реалізовано bootstrap локального UART/HID connector
- [x] Сесії прив'язуються до `targetAgentId`

### 3.4 Тимчасовий persistence baseline

- [x] Створено `Platform/Platform/HidBridge.Persistence`
- [x] Реалізовано file-based JSON persistence для:
  - `connectors`
  - `endpoints`
  - `sessions`
  - `audit`
  - `telemetry`
- [x] Додано `HIDBRIDGE_DATA_ROOT`
- [x] API переведено на читання persisted snapshots для session/audit/telemetry/endpoints

### 3.5 Якість коду

- [x] Додано XML-коментарі для public classes/public methods у Platform baseline
- [x] Додано unit tests
- [x] Додано `Platform/run_all_tests.ps1`
- [x] Командний `restore/build` для `Platform/HidBridge.Platform.sln` проходить

## 4. Що ще не зроблено

- [x] Production-like SQL backend baseline
- [x] Міграції БД
- [x] Startup reconciliation після restart процесу
- [x] Retention / archival policy для telemetry та audit
- [ ] Query/read-model optimization
- [ ] Multi-node / distributed coordination
- [ ] RBAC persistence
- [x] Sharing / invitations / approvals persistence baseline
- [x] Command journal persistence baseline
- [x] Command journal idempotency baseline
- [x] Dashboard read-model baseline для inventory/audit/telemetry
- [x] Tenant/org scope persisted in session snapshots
- [x] Tenant/org-aware enforcement baseline в endpoint/query/read-model шарах
- [x] Structured authorization denial payloads у `403` responses
- [x] Smoke/integration caller-scope propagation через `X-HidBridge-*` headers
- [x] Deep policy/orchestration enforcement baseline для session/control/collaboration flows
- [x] Policy/config persistence runtime activation baseline
- [x] Policy revision lifecycle/retention baseline

## 5. Цільовий стан persistence architecture

## 5.1 Storage-модель

Потрібно підтримати два провайдери persistence:

1. `File provider`
2. `SQL provider`

Обидва провайдери мають реалізовувати однакові абстракції:

1. `IConnectorCatalogStore`
2. `IEndpointSnapshotStore`
3. `ISessionStore`
4. `IEventStore`

Надалі, з ростом системи, ці абстракції потрібно деталізувати:

1. `IAgentStore`
2. `IEndpointStore`
3. `ISessionStore`
4. `ISharingStore`
5. `IAuditStore`
6. `ITelemetryStore`
7. `ICommandJournalStore`
8. `IPolicyStore`

## 5.2 Розподіл відповідальності

### File provider

Використовується для:

1. локальної розробки;
2. smoke/integration сценаріїв;
3. офлайн-режиму;
4. резервного fallback у dev-середовищі.

### SQL provider

Використовується для:

1. inventory;
2. agent catalog;
3. session lifecycle;
4. sharing model;
5. audit index;
6. command journal;
7. policy/config;
8. системних read-model запитів.

## 5.3 Рекомендована БД

Рекомендований primary backend:

1. `PostgreSQL`

Причини:

1. стабільний concurrency model;
2. транзакції;
3. індекси;
4. JSONB для гібридних payload-ів;
5. нормальна підтримка migrations;
6. хороший fit для control-plane системи.

## 5.4 Рекомендований доступ до даних

1. `EF Core` — для агрегатів і стандартних CRUD/transaction сценаріїв.
2. `Dapper` — опціонально для важких read-model/query endpoint’ів.

## 6. Поетапний план впровадження

## Phase P0 — Stabilization baseline

### P0-1. Platform skeleton

- [x] Створити новий modular solution у `Platform/`
- [x] Виділити shared/core/platform/connectors/tests
- [x] Підняти API baseline

### P0-2. Runtime baseline

- [x] Підключити UART/HID connector
- [x] Реалізувати session binding
- [x] Додати OpenAPI/Scalar
- [x] Додати базові тести

### P0-3. File persistence baseline

- [x] Створити persistence module
- [x] Винести persistence seams у abstractions
- [x] Реалізувати JSON stores
- [x] Перевести API на persisted snapshots
- [x] Додати unit tests для stores

## Phase P1 — SQL foundation

### P1-1. Створити SQL persistence module

- [x] Створити:
  - `Platform/Platform/HidBridge.Persistence.Sql`
  - `Platform/Platform/HidBridge.Persistence.Sql.Migrations`
- [x] Додати залежності:
  - `Npgsql`
  - `Microsoft.EntityFrameworkCore`
  - `Microsoft.EntityFrameworkCore.Design`
  - `Npgsql.EntityFrameworkCore.PostgreSQL`

### P1-2. Описати перший relational schema baseline

- [x] Спроєктувати таблиці:
  - `agents`
  - `agent_capabilities`
  - `endpoints`
  - `endpoint_capabilities`
  - `sessions`
  - `session_participants` або `session_shares`
  - `audit_events`
  - `telemetry_events`
  - `command_journal`
- [~] Для кожної таблиці визначити:
  - primary key
  - foreign keys
  - required indexes

## Phase P4-scope — Tenant / Organization enforcement baseline

- [x] `sessions` розширені полями `tenant_id` / `org_id`
- [x] File/SQL session snapshots зберігають tenant/org scope
- [x] caller scope із web shell прокидається в `ControlPlane.Api`
- [x] session-scoped write paths перевіряють tenant/org:
  - participant mutations
  - share / invitation flow
  - control arbitration
  - command dispatch
- [x] session-scoped read paths перевіряють tenant/org:
  - collaboration summaries
  - lobby
  - command journal
  - replay bundle
  - session-scoped projections
- [x] caller roles із `Keycloak/OIDC` прокидаються в `ControlPlane.Api` і дають backend policy baseline:
  - `operator.viewer` для читання, dispatch і базових control paths
  - `operator.moderator` / `operator.admin` для moderation paths
- [x] projections, dashboards і replay/archive diagnostics tenant/org-scoped у service/query layer
- [x] smoke/integration path перевіряє:
  - viewer moderation denial
  - foreign tenant denial
  - tenant/org stamping у session open
- [ ] policy/orchestration layer ще треба довести до повного enforcement baseline
- [ ] fleet-wide orchestration rules і policy consistency ще треба формалізувати

Примітка:
- `session_participants/session_shares` уже додані в SQL baseline.
- retention policy уже виконується background maintenance loop, але ще не винесена в окремий scheduler / distributed job subsystem.

## 7. Оновлений поетапний фокус

### Phase P4-next — Deep policy/orchestration enforcement

- [x] Винести critical authorization decisions глибше в orchestrator/policy layer
- [x] Формалізувати moderator/admin override rules для multi-session і fleet paths
- [x] Уніфікувати denial reason taxonomy між API/orchestrator/query services
- [x] Прокинути backend denial reasons у web UX
- [x] Довести fleet-wide orchestration consistency до окремого policy service

### Phase P5 — Policy/config persistence

- [~] persistence abstraction для operator policies
- [~] persistence abstraction для tenant/org policy bindings
- [x] SQL migration і runtime wiring для policy stores
- [~] versioned policy snapshots / audit trail

### P1-3. Реалізувати EF Core DbContext

- [x] Створити `PlatformDbContext`
- [x] Описати entity configuration через `IEntityTypeConfiguration<T>`
- [x] Відокремити persistence entities від domain objects
- [x] Додати mapping layer `entity <-> contract snapshot`

### P1-4. Реалізувати SQL stores

- [x] Реалізувати:
  - `SqlConnectorCatalogStore`
  - `SqlEndpointSnapshotStore`
  - `SqlSessionStore`
  - `SqlEventStore`
- [x] Додати DI registration:
  - `AddSqlPersistence(...)`

### P1-5. Додати конфігурацію backend selection

- [x] Додати режим:
  - `Persistence:Provider = File | Sql`
- [x] Реалізувати switch у composition root
- [x] Залишити file-provider як dev fallback

### Exit criteria для P1

- [x] API та orchestrator працюють поверх SQL
- [x] Після restart persisted sessions/events/agents відновлюються з SQL
- [x] Міграції накочуються автоматично або контрольовано
- [x] File provider лишається доступним для локального запуску

Поточний статус:
- SQL baseline реалізований.
- Реалізовано `InitialCreate` migration і автоматичне застосування міграцій на старті API через `HIDBRIDGE_SQL_APPLY_MIGRATIONS=true`.
- File provider лишається доступним для локального запуску як `dev/test fallback`.
- Додано P2 baseline:
  - startup reconciliation
  - session lease model
  - retention cleanup loop
- Реалізовано SQL-модель для `session_participants` і `session_shares`.
- Реалізовано baseline invitation/approval flow поверх `session_shares` (`Requested -> Pending -> Accepted/Rejected/Revoked`).
- Додано скрипт `Platform/run_sql_smoke.ps1` для локального smoke-run проти PostgreSQL.
- Додано окремий контейнерний baseline для нової Platform SQL-інфраструктури: `docker-compose.platform-sql.yml`.
- Виконано реальний smoke-run у режимі `Sql` проти локального PostgreSQL контейнера `docker-compose.platform-sql.yml`:
  - health probe пройшов
  - agent registration пройшов
  - session create пройшов
  - persisted session list прочитано з SQL
  - audit event stream прочитано з SQL
- Під час smoke-run додатково зафіксовано й виправлено:
  - JSON enum string binding для Minimal API
  - discovery `InitialCreate` migration у `HidBridge.Persistence.Sql.Migrations`
- `P1` вважається закритим по baseline implementation.
- Найближчий незакритий блок — API та flow для багатокористувацьких сценаріїв (`session_participants`, `session_shares`, invitation/grant/revoke).
- Dashboard baseline тепер включає:
  - fleet inventory dashboard
  - audit diagnostics dashboard
  - telemetry diagnostics dashboard
- Наступний незакритий блок:
  - optimized query projections
  - replay/archive diagnostics
  - policy/config persistence

## Phase P2 — Recovery, consistency, and startup reconciliation

### P2-1. Startup reconciliation

- [x] Під час старту API:
  - перечитати agents
  - перечитати endpoints
  - перечитати sessions
  - визначити які session snapshots є stale
- [x] Додати reconciliation policy:
  - `Requested` старіше N хвилин -> `Failed`
  - `Preparing/Arming` старіше N хвилин -> `Failed`
  - `Active` без heartbeat/lease -> `Recovering` або `Ended`

### P2-2. Session lease / heartbeat model

- [x] Додати `lease_expires_at`
- [x] Додати `last_heartbeat_at`
- [x] Додати background cleanup worker

### P2-3. Audit/telemetry retention

- [x] Додати політику retention:
  - audit: довше
  - telemetry: коротше
- [~] Додати archive/cleanup jobs

### Exit criteria для P2

- [x] Restart API не залишає session state у невизначеному стані
- [x] Є контрольований cleanup
- [x] Є формальна consistency policy

Поточний статус:
- Реалізовано startup reconciliation hosted service.
- Реалізовано lease model на сесіях:
  - `LastHeartbeatAtUtc`
  - `LeaseExpiresAtUtc`
- Реалізовано background maintenance loop:
  - stale session reconciliation
  - retention cleanup для audit/telemetry
- Архівація в окреме сховище ще не реалізована; наразі виконується trim/cleanup без cold-storage tier.

## Phase P3 — Control plane maturity

### P3-1. Розширити persistence model

- [~] Додати persistence для:
  - `sharing`
  - `invites`
  - `approvals`
  - `RBAC`
  - `policies`
  - `agent groups`
  - `tags`

Примітка:
- `session_participants` і `session_shares` уже зберігаються в SQL/file persistence.
- Додано API і orchestration flow для:
  - participant list/upsert/remove
  - share grant
  - share accept/reject/revoke
- Додано baseline invitation flow:
  - invitation request
  - approve -> `Pending`
  - decline -> `Rejected`
- Policy-driven approval rules і окремий approval engine ще не реалізовані.

### P3-2. Read models

- [~] Додати окремі query/read-model сервіси:
  - inventory summary
  - session dashboard
  - audit dashboard
  - telemetry dashboard

Примітка:
- already implemented:
  - `GET /api/v1/collaboration/sessions/{sessionId}/summary`
  - `GET /api/v1/collaboration/sessions/{sessionId}/dashboard`
  - `GET /api/v1/collaboration/sessions/{sessionId}/shares/dashboard`
  - `GET /api/v1/collaboration/sessions/{sessionId}/participants/activity`
  - `GET /api/v1/collaboration/sessions/{sessionId}/operators/timeline`
  - `GET /api/v1/events/timeline/{sessionId}`
- tenant/org-aware filtering тепер застосовується не тільки в endpoint-ах, а й у:
  - `ProjectionQueryService`
  - `OperationsDashboardReadModelService`
  - `ReplayArchiveDiagnosticsService`
- `operator.admin` має cross-tenant override для query/read-model доступу
- smoke/integration baseline тепер підтримує caller scope headers для перевірки tenant/org-aware behavior
- denial reasons у `403` responses тепер структуровані та придатні для UI/integration diagnostics
  - `GET /api/v1/sessions/{sessionId}/commands/journal`
  - `GET /api/v1/commands?sessionId=...`
- ще не реалізовано:
  - inventory summary
  - audit dashboard
  - telemetry dashboard
  - optimized query stores / materialized read models

### P3-3. Search and filtering

- [ ] Підтримати:
  - пошук endpoint by hostname/tag/agent type/status
  - фільтрацію session history
  - фільтрацію audit/telemetry

### Exit criteria для P3

- [ ] Платформа готова для multi-agent inventory control plane
- [~] Є нормальні dashboards і операторські запити
- [~] Є база для collaboration/sharing сценаріїв

Поточний статус:
- `P3` закрито по baseline.
- Collaboration API baseline уже додано:
  - `session_participants`
  - `session_shares`
  - `accept/reject/revoke`
  - compact collaboration summary read model
- Invitation flow baseline уже додано:
  - `Requested -> Pending -> Accepted/Rejected/Revoked`
  - lobby read model
- Richer collaboration read models уже додано:
  - session dashboard
  - share dashboard
  - participant activity view
  - operator timeline view
- Command journal baseline уже додано:
  - persisted `command_journal`
  - participant/share context у journal entry
  - session-scoped journal API
  - idempotent replay handling by `commandId` / `idempotencyKey`
- Unified timeline baseline уже додано:
  - audit + telemetry + command journal
- `P4-1/P4-2` baseline уже додано:
  - policy-driven moderation rules for participant/share/invitation actions
  - active controller lease
  - `request/grant/release/force-takeover` control flow
  - control-aware command dispatch policy
  - control dashboard read model
- Наступний підетап:
  - richer inventory / audit / telemetry dashboards
  - optimized query projections
  - archive/replay diagnostics

## Phase P4 — Meet-like collaboration persistence

### P4-1. Collaboration domain

- [x] Додати policy-driven approvals:
  - owner-only approval rules
  - role-based invite/grant/revoke rules
  - separate policy checks for viewer/controller/operator permissions
- [x] Додати control arbitration:
  - active controller lease
  - request/grant/release control
  - forced takeover by owner/admin
  - audit trail for control ownership transitions

Примітка:
- baseline policy engine вже реалізований у `SessionAuthorizationPolicy`;
- moderator semantics зараз такі:
  - owner
  - active controller з не простроченим lease;
- dispatch policy вже контролює:
  - owner dispatch без lease тільки коли lease відсутній;
  - активний controller dispatch коли lease існує;
  - відповідність `participantId` активному lease holder;
- окремий policy/config persistence backend ще попереду.

### P4-2. Timeline and event model

- [x] Додати unified session timeline:
  - room created
  - participant joined
  - presenter switched
  - controller granted
  - stream degraded
  - reconnect occurred

Примітка:
- baseline unified timeline вже реалізований для:
  - audit events
  - telemetry events
  - command journal
- control-specific audit/timeline події вже додані:
  - control requested
  - control released
  - forced takeover
  - control lease expired
- richer room/media-domain projections ще попереду.

### P4-3. Sharing and replay support

- [ ] Додати основу для:
  - replayable audit
  - incident investigation
  - historical diagnostics

### Exit criteria для P4

- [~] Архітектура готова для Meet-подібної багатокористувацької роботи
- [~] Persistence model покриває collaboration сценарії

Поточний статус:
- `P4-1` закрито по baseline:
  - policy-driven approvals
  - control arbitration
  - active controller lease
  - control dashboard projection
- `P4-2` закрито по baseline:
  - unified timeline
  - control ownership transition events
  - command journal + audit + telemetry composition
- `P4-3` закрито по baseline:
  - inventory dashboard
  - audit dashboard
  - telemetry dashboard
- `P4-4` закрито по baseline:
  - optimized session projections
  - optimized endpoint projections
  - optimized audit projections
  - optimized telemetry projections
- `P4-5` закрито по baseline:
  - session replay bundle
  - archive diagnostics summary
  - archive audit slices
  - archive telemetry slices
  - archive command slices
- не закрито:
  - thin operator UI shell

План наступної реалізації:
1. Побудувати thin operator UI shell:
   - fleet overview shell
   - session details shell
   - diagnostics shell over existing projections
   - telemetry dashboard
2. Після стабілізації projection layer додати thin operator UI.
3. UI не повинен збирати стан із великої кількості сирих endpoint-ів у браузері; він має читати готові read models.
4. Рекомендований підхід UI/UX:
   - console-style operator interface
   - high information density
   - мінімум декоративності
   - явний показ owner / active controller / pending approvals / health state
5. Рекомендована технічна послідовність:
   - projections
   - dashboard API
   - web shell
   - fleet overview
   - session details
   - audit / telemetry views

## 7. Практичний порядок робіт

Рекомендований порядок наступних ітерацій:

1. `P1-1` — створити `HidBridge.Persistence.Sql`
2. `P1-2` — зафіксувати schema baseline
3. `P1-3` — реалізувати `DbContext + migrations`
4. `P1-4` — реалізувати SQL stores
5. `P1-5` — увімкнути provider switch
6. `P2-1` — startup reconciliation
7. `P2-2` — leases / heartbeat state
8. `P2-3` — retention / cleanup
9. `P3-*` — control-plane maturity
10. `P4-*` — collaboration persistence and control arbitration

## 8. Що НЕ треба робити зараз

На поточному етапі не варто:

1. видаляти file persistence;
2. переносити legacy `Tools/` у архів до досягнення паритету;
3. змішувати domain objects і EF entities;
4. робити telemetry-only storage окремою time-series системою до стабілізації основного control plane;
5. ускладнювати persistence distributed coordination до появи реальної multi-node потреби.

## 9. Ризики

### Технічні ризики

1. передчасний перехід на SQL без стабілізації контрактів;
2. змішування runtime registry і persistence responsibilities;
3. занадто ранній перехід до складної event-sourcing моделі;
4. відсутність retention policy для telemetry.

### Організаційні ризики

1. паралельне ламання legacy і greenfield рішення;
2. відсутність формального migration cutoff;
3. відсутність acceptance criteria між фазами.

## 10. Короткий висновок

Поточний стан правильний:

1. `P0-3` вже закрито через file-based persistence baseline.
2. `P1` уже закрито через `SQL-backed persistence` baseline з реальним smoke-run проти локального PostgreSQL.
3. `P2` закрито по baseline implementation: reconciliation, leases, retention cleanup.
4. File backend треба залишити як `dev/test fallback`.
5. `P4` baseline закрито і посилено:
- endpoint/query/orchestration enforcement уже працює
- backend denial reasons already surface into web UX
- caller-scope smoke propagation already validates tenant/org and role denials

## 11. Оновлення стану `2026-03-02` — Web / Identity / Tenant

1. `P4` data/read-model частина фактично закрита по baseline.
2. Поверх thin web shell уже стартував `Identity & Access baseline`:
- cookie-backed session
- optional `OIDC` challenge flow
- development operator fallback identity
- baseline policies `OperatorViewer`, `OperatorModerator`, `OperatorAdmin`
- tenant/org seams projected from claims
- authorization-aware UX baseline:
  - current operator / roles / tenant / organization surfaced in the shell
  - moderation/control affordances explain why an action is available or blocked
  - backend `403` denial payloads now surface in web UX without losing denial code / required roles / required scope

3. `Policy/config persistence` перейшов у preparation baseline:
- додано `IPolicyStore`
- додано file/sql stores для:
  - policy scopes
  - policy assignments
- SQL migration і runtime activation ще попереду
2. Наступний практичний фокус:
- thin operator UI shell
- operator actions baseline у thin web shell
- `Blazor Web App`
- `Tailwind CSS` browser baseline
 - design baseline inherited from `landing/index.html`
 - localization baseline:
   - `en` default
   - `uk` secondary
   - browser locale auto-detection
   - manual locale override
3. Після стабілізації web shell наступний security layer:
- `OIDC + cookie session`
- `RBAC + ABAC`
- tenant/org-aware authorization


Оновлення web shell / Identity baseline:
- коли `Identity:Enabled=false`, development fallback identity тепер автентифікується автоматично без ручного login flow;
- ручний theme switch лишається окремим відкритим дефектом web shell і винесений у дизайн-backlog;
- `html, body` background зроблено напівпрозорим, щоб grid background із `landing/index.html` читався стабільно.

## 8. Додатковий статус `2026-03-03`

- [x] Web shell now forwards OIDC bearer tokens to `ControlPlane.Api` for protected API consumption baseline.
- [x] Web shell still propagates explicit caller-scope headers for backward-compatible smoke/dev flows.
- [x] Shared authorization denial panel now surfaces structured backend denials across fleet, audit, telemetry, and session flows.
- [x] Policy revision retention baseline implemented:
  - configurable retention window
  - configurable maximum revisions per entity
  - background pruning
  - audit events with category `policy.retention`


Останні доповнення:
- додано policy governance diagnostics summary endpoint: `GET /api/v1/diagnostics/policies/summary`;
- `Policy Governance` management baseline тепер включає:
  - scope `activate / deactivate / safe delete`
  - assignment `activate / deactivate / delete`
- стартував `micro meet` delivery baseline:
  - `Fleet Overview -> Start session`
  - `Session Room`
  - invite/request actions у кімнаті
  - participant activity + operator timeline
- додано manual policy prune endpoint: `POST /api/v1/diagnostics/policies/prune` (`operator.admin`);
- додано bearer-first smoke runner для non-web clients: `Platform/run_api_bearer_smoke.ps1`;
- `HIDBRIDGE_AUTH_ALLOW_HEADER_FALLBACK=false` тепер використовується для жорсткої перевірки protected API consumption без legacy header fallback.


Останні доповнення:
- у `HidBridge.ControlPlane.Web` додано окремий екран `Policy Governance`;
- додано фільтри й JSON export для policy revisions через web endpoint `/exports/policies/revisions`;
- додано edit flows для policy scopes та policy assignments через `Policy Governance` screen і API management endpoints;
- `Policy Governance` UI підтримує load-into-form / clear-form workflow для scopes та assignments;
- додано deactivate/delete semantics для policy assignments через `Policy Governance` screen та diagnostics API;
- додано activate/deactivate semantics для policy scopes через `Policy Governance` screen та diagnostics API;
- додано safe delete semantics для policy scopes: scope можна видалити лише після видалення всіх прив’язаних assignments;
- UI resilience pass для `HidBridge.ControlPlane.Web` завершено:
  `Fleet Overview`, `Session Details`, `Audit`, `Telemetry`, `Policy Governance`, `Ops Status`
  повинні показувати `Backend unavailable`, коли `ControlPlane.Api` недоступний;
- `Ops Status` тепер показує останні local operational artifacts і прямі посилання на відповідні логи/артефакти;
- `ControlPlane.Api` підтримує gradual bearer-only rollout через `HIDBRIDGE_AUTH_BEARER_ONLY_PREFIXES`;
- за замовчуванням bearer-only baseline застосовано до:
  - `/api/v1/dashboards`
  - `/api/v1/projections`
  - `/api/v1/diagnostics`
  - `/api/v1/sessions`
- bearer-only smoke в dev baseline тепер може автоматично отримувати JWT через `Keycloak`;
- `ControlPlane.Api` також підтримує `HIDBRIDGE_AUTH_CALLER_CONTEXT_REQUIRED_PREFIXES` для unsafe mutation path-ів;
- за замовчуванням caller-context-required baseline тепер застосовано до:
  - `/api/v1/sessions`
- `ControlPlane.Api` також підтримує `HIDBRIDGE_AUTH_HEADER_FALLBACK_DISABLED_PATTERNS` для path-level staged fallback removal (`*` = один path segment wildcard);
- для `Keycloak` dev realm додано окремий scope `hidbridge-caller-context-v2`, який проєктує `preferred_username`, `email`, `tenant_id`, `org_id`, `principal_id` і `role` у bearer token для `controlplane-web` та `controlplane-smoke`;
- `/api/v1/sessions` переведено в strict bearer-only baseline; caller-context mutation rules лишаються окремо видимими через `HIDBRIDGE_AUTH_CALLER_CONTEXT_REQUIRED_PREFIXES`.
- `HIDBRIDGE_AUTH_HEADER_FALLBACK_DISABLED_PATTERNS` тепер за замовчуванням відповідає verified `Phase 4` state для session mutation surface.
- smoke/direct-grant token flow тепер явно використовує `openid profile email` для richer bearer claims baseline без `invalid_scope` на dev Keycloak contour.
- `run_doctor.ps1` тепер показує окремий сигнал `smoke-claims`; після `identity-reset` він проходить як `PASS`.
- `run_checks.ps1`, `run_all_tests.ps1` і smoke scripts зберігають повні логи у файли та показують у консолі лише summary + log paths.
- додано dev realm sync script:
  - `Platform/Identity/Keycloak/Sync-HidBridgeDevRealm.ps1`
  - робить backup realm і нормалізує `Keycloak` dev baseline під актуальний smoke/auth flow.


- operational scripts consolidated under `Platform/Scripts/`; top-level `Platform/run_*.ps1` paths remain as compatibility wrappers, and unified entrypoint `Platform/run.ps1` was added.

- operational helpers added: `Platform/run_doctor.ps1`, `Platform/run_clean_logs.ps1`, `Platform/run_ci_local.ps1`, `Platform/run_full.ps1`, `Platform/run_token_debug.ps1`, and shared script helpers in `Platform/Scripts/Common/ScriptCommon.ps1`, `Platform/Scripts/Common/KeycloakCommon.ps1`.
- `micro meet` delivery baseline now includes a quick-launch card on `Fleet Overview` and a `Session Room` demo surface with room-link sharing plus a checklist for invite/control handoff demo flow.
- `Session Room` now also shows one explicit next-step recommendation so the live demo keeps moving without extra explanation.
- `Docs/GoToMarket/MicroMeet_Demo_Runbook_UA.md` now captures the manual sales/demo walkthrough.

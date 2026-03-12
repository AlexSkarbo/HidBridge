# Docs/SystemArchitecture — Index (UA)

Оновлено: `2026-03-03`

## Базові документи

1. Детальний системний опис:
- `Docs/SystemArchitecture/HidBridge_SystemArchitecture_UA.md`

2. Executive summary:
- `Docs/SystemArchitecture/HidBridge_SystemArchitecture_UA_Executive.md`

## Нормативні доповнення (Agent-first архітектура)

1. Agent Contract v1:
- `Docs/SystemArchitecture/HidBridge_AgentContract_v1_UA.md`

2. JSON Schema контракту:
- `Docs/SystemArchitecture/schemas/HidBridge_AgentContract_v1_Message.schema.json`

3. Карта модулів і міграції:
- `Docs/SystemArchitecture/HidBridge_ModuleArchitecture_Map_UA.md`

4. Цільова чиста модульна агентна архітектура (Meet-подібна collaboration модель):
- `Docs/SystemArchitecture/HidBridge_Clean_Modular_Agent_Architecture_UA.md`

5. Стратегія модернізації структури репозиторію:
- `Docs/SystemArchitecture/Repository_Modernization_Strategy_UA.md`

6. Поетапний план впровадження persistence layer:
- `Docs/SystemArchitecture/HidBridge_Persistence_Rollout_Plan_UA.md`

7. Відкриті питання дизайну web shell:
- `Docs/SystemArchitecture/HidBridge_ControlPlane_Web_Design_Questions_UA.md`

8. Identity / SSO baseline:
- `Docs/SystemArchitecture/HidBridge_Identity_SSO_Baseline_UA.md`

9. External IdP integration baseline:
- `Docs/SystemArchitecture/HidBridge_External_IdP_Integration_Baseline_UA.md`

10. Google/Keycloak practical runbook:
- `Docs/SystemArchitecture/HidBridge_Google_Keycloak_Runbook_UA.md`
- EN mirror:
  - `Docs/SystemArchitecture/HidBridge_Google_Keycloak_Runbook_EN.md`

11. Keycloak claim mapping runbook:
- `Docs/SystemArchitecture/HidBridge_Keycloak_Claim_Mapping_Runbook_UA.md`
- EN mirror:
  - `Docs/SystemArchitecture/HidBridge_Keycloak_Claim_Mapping_Runbook_EN.md`

12. Keycloak UI click-by-click claim mapping runbook:
- `Docs/SystemArchitecture/HidBridge_Keycloak_UI_Claim_Mapping_Clickpath_UA.md`
- EN mirror:
  - `Docs/SystemArchitecture/HidBridge_Keycloak_UI_Claim_Mapping_Clickpath_EN.md`

13. Bearer rollout profile:
- `Docs/SystemArchitecture/HidBridge_Bearer_Rollout_Profile_UA.md`

## Поточний статус реалізації

1. `P0` закрито:
- greenfield `Platform/` baseline створено
- modular solution піднято
- OpenAPI + Scalar підключено
- file-based persistence baseline реалізовано

2. `P1` закрито по baseline:
- `SQL-backed persistence` реалізовано
- `InitialCreate` migration реалізовано
- provider switch `File | Sql` реалізовано
- реальний `SQL smoke-run` проти локального PostgreSQL пройшов успішно

3. `P2` закрито по baseline:
- startup reconciliation реалізовано
- session lease / heartbeat model реалізовано
- retention cleanup loop для audit/telemetry реалізовано

4. `P3` закрито по baseline:
- multi-user sharing baseline реалізовано
- invitations / approvals baseline реалізовано
- command journal baseline реалізовано
- command journal idempotency baseline реалізовано
- richer collaboration read models реалізовано:
  - session dashboard
  - share dashboard
  - participant activity view
  - operator timeline view

5. `P4` активний:
- policy-driven approvals baseline реалізовано
- control arbitration / active controller model baseline реалізовано
- richer inventory / audit / telemetry dashboards baseline реалізовано
- optimized query projections baseline реалізовано
- replay/archive diagnostics baseline реалізовано
- thin operator UI shell baseline реалізовано
- centralized SSO baseline через `Keycloak` реалізовано
- `Google -> Keycloak -> HidBridge.ControlPlane.Web` перевірено
- tenant/org-aware enforcement baseline реалізовано:
  - endpoint layer
  - query/read-model layer
  - smoke/integration caller-scope headers
  - structured `403` denial payloads
- deep policy/orchestration enforcement baseline реалізовано:
  - session/control/collaboration decisions already enforced below endpoint layer
  - moderator/admin override rules already normalized
  - backend denial reasons already surface into `HidBridge.ControlPlane.Web`
  - denial UX expanded beyond session actions into fleet/audit/telemetry web flows
  - policy/config persistence moved to preparation baseline:
    - `IPolicyStore`
    - file/sql policy stores
    - SQL migration/runtime activation now added
  - operator roles прокидаються в orchestration requests
  - session policy rules застосовуються в `SessionAuthorizationPolicy`
  - moderator/admin overrides працюють у session/control/collaboration paths
  - shared operator policy service now resolves persisted assignments/scopes into effective caller context
  - API request pipeline now enriches caller scope before endpoint/query enforcement
  - versioned policy snapshot baseline added:
    - `IPolicyRevisionStore`
    - file/sql revision stores
    - startup policy bootstrap now writes policy audit events and policy revisions
    - diagnostics endpoint for policy revisions added
- backend denial reasons тепер готові до web UX:
  - structured `403` payload already rendered in operator actions
  - web client parses denial code / required roles / required scope
- staged bearer rollout:
  - `identity-reset` + smoke-claim normalization now unblock bearer caller context
  - `Phase 3: shares / participants mutations` passes under the strict rollback runner
  - `Phase 4: commands mutations` passes under the strict rollback runner
  - current effective rollout is `Phase 4`
- micro meet delivery baseline started:
  - `Docs/GoToMarket/MicroMeet_Demo_Runbook_UA.md` now captures the live demo/sales walkthrough
  - `Fleet Overview -> Start session` and quick-launch card for the first idle endpoint
  - `Session Room` as the primary collaboration surface
  - room-level invite/request flows
  - room-level control handoff actions
  - room-level participant activity and operator timeline
  - room-link sharing and demo checklist inside `Session Room`
  - `Session Room` now also surfaces one explicit “next recommended step” card to keep the demo moving
- стартувала підготовка до `policy/config persistence`:
  - `IPolicyStore`
  - file/sql persistence baseline for policy scopes and assignments
- Meet-подібна collaboration model
- dashboard/UI plan зафіксовано:
  - спочатку backend read models / projections
  - потім thin operator UI
  - dashboard не повинен збирати state з десятка endpoint-ів у браузері
  - окремі paged/filterable query endpoint-и вже виділені в `api/v1/projections/*`

## Призначення пакета документів

Цей пакет документів задає архітектурний baseline для наступної ітерації HidBridge:

1. Multi-agent + agentless модель.
2. Єдиний контракт обміну і помилок.
3. Модульний перехід від поточних рішень у `Tools`, `Firmware`, `WebRtcTests/exp-022-datachanneldotnet`.

## Оновлення стану `2026-03-02` — Web / Identity / Tenant

1. Стартував thin operator UI shell.
2. Затверджено web stack:
- `Blazor Web App`
- `Tailwind CSS` browser baseline
3. Візуальний baseline web shell синхронізується з `landing/index.html`:
- light/dark theme
- responsive layout
- спільна glass/grid visual language
4. У thin operator UI shell уже реалізовані baseline operator actions:
- `request control`
- `release control`
- `force takeover`
- `approve invitation`
- `decline invitation`
5. Реалізовано localization baseline:
- `en` default
- `uk` secondary
- browser locale auto-detection
- manual locale override через settings page
- ручний перемикач теми в settings лишається нестабільним і винесений в окремий backlog дизайну
- responsive operator layout, синхронізований з `landing/index.html`
- core layout більше не залежить від delayed Tailwind runtime refresh
6. Затверджено security direction:
- server-side web shell як BFF-style точка входу
- `OIDC + cookie session` для users
- `RBAC + ABAC` для operator/control дій
- `Tenant & Organization` enforcement після стабілізації web shell
7. `Identity & Access baseline` уже стартував у web shell:
- cookie-backed web session
- optional `OIDC` challenge flow
- development operator fallback identity
- baseline policies: `OperatorViewer`, `OperatorModerator`, `OperatorAdmin`
- tenant/org seams уже проектуються з claims у web shell
8. Поточний практичний фокус у web shell:
- authorization-aware UX
- видимість current operator / roles / tenant / organization
- пояснення, чому moderation/control дії дозволені або заборонені
9. Архітектурне рішення по SSO:
- `HidBridge.ControlPlane.Web` не є identity server
- централізований IdP baseline: `Keycloak`
- локальний login/password + external IdPs мають жити в централізованому IdP
10. Фактичний стан на `2026-03-03`:
- локальний Keycloak login/password перевірено
- login/logout між `HidBridge.ControlPlane.Web` і `Keycloak` перевірено
- Google federation через Keycloak перевірено
- claim mapping baseline через Keycloak UI перевірено
- `Current operator` може відображатися як email
- `User ID` відображається з `sub`
- `User added at` лишається optional claim і без окремого mapper-а може бути `not available`
- у backend стартував `tenant/org-aware enforcement` baseline для session-scoped collaboration/control/dispatch/read flows
- tenant/org scope-check тепер опущено глибше в `query/read-model` шар:
  - `ProjectionQueryService`
  - `OperationsDashboardReadModelService`
  - `ReplayArchiveDiagnosticsService`
- `operator.admin` має cross-tenant override для service/query layer
- `operator.viewer` / `operator.moderator` / `operator.admin` gating уже застосовується не тільки в endpoint-ах, а й у частині projection/diagnostics path
- smoke/integration path тепер може передавати caller scope через:
  - `X-HidBridge-UserId`
  - `X-HidBridge-PrincipalId`
  - `X-HidBridge-TenantId`
  - `X-HidBridge-OrganizationId`
  - `X-HidBridge-Role`
- `403` відповіді API тепер повертають структурований denial payload з:
  - `code`
  - `message`
  - caller context
  - required roles / required tenant/org scope коли вони відомі


Оновлення web shell / Identity baseline:
- коли `Identity:Enabled=false`, development fallback identity тепер автентифікується автоматично без ручного login flow;
- ручний theme switch працює через `localStorage` (`auto | light | dark`);
- `html, body` background зроблено напівпрозорим, щоб grid background із `landing/index.html` читався стабільно.

Оновлення backend scope baseline:
- caller identity із web shell прокидається в `ControlPlane.Api` через заголовки:
  - `X-HidBridge-UserId`
  - `X-HidBridge-PrincipalId`
  - `X-HidBridge-TenantId`
  - `X-HidBridge-OrganizationId`
  - `X-HidBridge-Role`
- session snapshots розширені полями `tenant_id` / `org_id`
- session-scoped reads/writes у collaboration/control/dispatch paths проходять tenant/org scope-check
- при наявному caller context backend також застосовує базові operator policies:
  - `operator.viewer` для read/session/control baseline
  - `operator.moderator` або `operator.admin` для moderation endpoints
- projections, dashboards і replay/archive diagnostics тепер також фільтруються по visible session scope всередині сервісів, а не лише на рівні endpoint-ів

## Поточний практичний фокус

1. `P4-continued`:
- fleet-wide policy consistency між API, projections і orchestration
- backend denial reasons hardening across more UI flows

2. Поточний наступний фокус:
- policy/config lifecycle hardening
- stronger protected API consumption rollout across non-web clients
- deeper denial UX coverage in remaining operator flows


Останні доповнення:
- додано policy governance diagnostics summary endpoint: `GET /api/v1/diagnostics/policies/summary`;
- додано manual policy prune endpoint: `POST /api/v1/diagnostics/policies/prune` (`operator.admin`);
- додано bearer-first smoke runner для non-web clients: `Platform/run_api_bearer_smoke.ps1`;
- додано unified operational launcher: `Platform/run.ps1`;
- додано `full` pipeline: `realm sync -> ci-local -> artifact export on failure`;
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
  повинні показувати `Backend unavailable`, коли `ControlPlane.Api` недоступний, замість падіння рендерингу;
- `Ops Status` тепер показує останні local operational artifacts і прямі посилання на відповідні логи/артефакти;
- `ControlPlane.Api` підтримує gradual bearer-only rollout через `HIDBRIDGE_AUTH_BEARER_ONLY_PREFIXES`;
- за замовчуванням bearer-only baseline застосовано до:
  - `/api/v1/dashboards`
  - `/api/v1/projections`
  - `/api/v1/diagnostics`
  - `/api/v1/sessions`
- `ControlPlane.Api` також підтримує `HIDBRIDGE_AUTH_CALLER_CONTEXT_REQUIRED_PREFIXES` для unsafe mutation path-ів;
- за замовчуванням caller-context-required baseline тепер застосовано до:
  - `/api/v1/sessions`
- `ControlPlane.Api` також підтримує `HIDBRIDGE_AUTH_HEADER_FALLBACK_DISABLED_PATTERNS` для path-level staged fallback removal (`*` = один path segment wildcard);
- для `Keycloak` dev realm додано окремий scope `hidbridge-caller-context-v2`, який проєктує `preferred_username`, `email`, `tenant_id`, `org_id`, `principal_id` і `role` у bearer token для `controlplane-web` та `controlplane-smoke`;
- `/api/v1/sessions` переведено в strict bearer-only baseline; caller-context mutation rules лишаються окремо видимими через `HIDBRIDGE_AUTH_CALLER_CONTEXT_REQUIRED_PREFIXES`.
- `HIDBRIDGE_AUTH_HEADER_FALLBACK_DISABLED_PATTERNS` тепер за замовчуванням відповідає verified `Phase 4` state:
  - `control`
  - `invitations`
  - `shares / participants`
  - `commands`
- smoke/direct-grant token flow тепер явно запитує `openid profile email`, щоб strict bearer rollout отримував richer claims baseline без `invalid_scope` на dev Keycloak contour.
- `run_doctor.ps1` тепер показує окремий сигнал `smoke-claims`, щоб було видно, чи bearer token уже містить достатній caller context.
- bearer-only smoke тепер вміє автоматично отримувати JWT із `Keycloak` dev realm через `controlplane-smoke`;
- smoke/test runners тепер зберігають детальні логи у файли й виводять у консоль лише summary + log paths.
- додано PowerShell script для backup/sync/normalization dev realm:
  - `Platform/Identity/Keycloak/Sync-HidBridgeDevRealm.ps1`


- operational scripts consolidated under `Platform/Scripts/`; top-level `Platform/run_*.ps1` paths remain as compatibility wrappers, and unified entrypoint `Platform/run.ps1` was added.

- operational helpers added: `Platform/run_doctor.ps1`, `Platform/run_clean_logs.ps1`, `Platform/run_ci_local.ps1`, `Platform/run_full.ps1`, `Platform/run_token_debug.ps1`, and shared script helpers in `Platform/Scripts/Common/ScriptCommon.ps1`, `Platform/Scripts/Common/KeycloakCommon.ps1`.

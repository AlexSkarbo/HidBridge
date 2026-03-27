# HidBridge — Runtime Flow Baseline (2026-03-19)

Версія: `1.1`  
Дата: `2026-03-20`  
Мова: `Українська`

---

## 1. Призначення

Цей документ фіксує **фінальний runtime flow** для поточного етапу платформи:

- production/runtime шлях іде через `Platform/` (API/Web/Identity/Persistence + Edge Agent),
- canonical orchestration — через `Platform/Tools/HidBridge.RuntimeCtl` (CLI-first),
- PowerShell `Platform/run.ps1 -Task ...` лишається лише compatibility shim,
- `WebRtcTests/exp-022-datachanneldotnet` зафіксовано як **lab-only** стенд.

---

## 2. Канонічна топологія

## 2.1 Core (container profile)

Контейнеризуються:

1. `HidBridge.ControlPlane.Api`
2. `HidBridge.ControlPlane.Web`
3. `Keycloak`
4. `PostgreSQL` (platform DB)
5. `PostgreSQL` (identity DB)

Рекомендований профіль:

- `docker-compose.platform-runtime.yml`
- запуск/зупинка через RuntimeCtl:
  - `dotnet run --project Platform/Tools/HidBridge.RuntimeCtl/HidBridge.RuntimeCtl.csproj -- --platform-root Platform platform-runtime -Action up|down|status|logs`

## 2.2 Edge (external processes/hosts)

Edge рівень не прив'язаний до core-container lifecycle:

1. `HidBridge.EdgeProxy.Agent` працює окремим процесом/хостом.
2. Один агент може обслуговувати один або багато endpoint/device.
3. Розміщення edge-agent можливе поруч із target device (географічно будь-де).

---

## 3. Runtime контури

## 3.1 Control path

`Web -> API -> Transport routing -> EdgeProxy.Agent -> HID protocol adapter -> target device`

Ключові правила:

1. Routing/readiness/lease policy визначаються на сервері (`ControlPlane.Api`).
2. Edge-agent виконує команду і повертає typed ACK (`Applied|Rejected|Timeout`).
3. ACK публікується в relay path і пишеться в audit trail.

## 3.2 Media path (MVP)

`Capture source -> Edge agent media snapshot -> API media registry -> room/readiness projection`

На поточному етапі:

1. Edge-agent публікує `media readiness` snapshot.
2. API включає media-сигнали в transport health/readiness.
3. Media-ready може бути обов'язковою умовою WebRTC readiness policy.
4. Для `UART` control-only acceptance без capture probe:
   - `HIDBRIDGE_EDGE_PROXY_ASSUMEMEDIAREADYWITHOUTPROBE=true`,
   - agent публікує `MediaState=Ready` при відсутньому probe URL,
   - readiness policy не блокується через `NoProbeConfigured`.

## 3.3 Telemetry/Diagnostics path

`Relay metrics + command journal + peer lifecycle -> diagnostics endpoints`

Базові endpoint-и:

1. `GET /api/v1/sessions/{id}/transport/health`
2. `GET /api/v1/sessions/{id}/transport/readiness`
3. `GET /api/v1/diagnostics/transport/slo`

---

## 4. Policy ownership (server-side)

Business policy не повинна жити в скриптах.

Сервер (`ControlPlane.Api`) є source of truth для:

1. transport readiness policy,
2. lease ensure/retry orchestration,
3. route/provider consistency checks,
4. SLO aggregation і alert classification,
5. security enforcement (auth scope + roles + audit trail).

Критичне уточнення:

1. `operator.edge` має серверний доступ до relay-потоку, включно з `GET /transport/webrtc/signals` (viewer **або** edge-relay доступ).
2. Це прибирає скриптові fallback-и для peer polling/signaling і переносить policy в API layer.

---

## 5. SLO/alerts baseline

Transport SLO summary включає:

1. `ackTimeoutRate`
2. `relayReadyLatencyP50/P95`
3. `reconnectFrequencyPerHour`
4. `alerts` (human-readable)
5. `alertCounters` (warning/critical)
6. `breaches` (explicit threshold flags)

Пороги задаються через runtime env:

1. `HIDBRIDGE_SLO_WINDOW_MINUTES`
2. `HIDBRIDGE_SLO_RELAY_READY_WARN_MS`
3. `HIDBRIDGE_SLO_RELAY_READY_CRITICAL_MS`
4. `HIDBRIDGE_SLO_ACK_TIMEOUT_RATE_WARN`
5. `HIDBRIDGE_SLO_ACK_TIMEOUT_RATE_CRITICAL`
6. `HIDBRIDGE_SLO_RECONNECT_FREQ_WARN`
7. `HIDBRIDGE_SLO_RECONNECT_FREQ_CRITICAL`

Operational verification lane (step 22):

1. `powershell -ExecutionPolicy Bypass -File Platform/run.ps1 -Task ops-slo-security-verify -BaseUrl http://127.0.0.1:18093 -OutputJsonPath Platform/.logs/ops-slo-security-verify.result.json`
2. Скрипт читає `GET /api/v1/diagnostics/transport/slo` і класифікує:
   - `PASS` для healthy,
   - `WARN` для warning (або `FAIL` з `-FailOnSloWarning`),
   - `FAIL` для critical.
3. У strict режимі CI можна підняти чутливість через:
   - `-FailOnSloWarning`.

---

## 6. Security baseline

Поточний runtime baseline:

1. OIDC/Bearer інтеграція через Keycloak.
2. Server-side scope/role enforcement (`viewer|moderator|admin`).
3. Edge-agent token lifecycle:
   - refresh-token grant,
   - fallback на password grant,
   - retry після `401`.
4. ACK audit trail з session/command/status/error контекстом.
5. У `docker-compose.platform-runtime.yml` API security увімкнений за замовчуванням:
   - `HIDBRIDGE_AUTH_ENABLED=true`,
   - `HIDBRIDGE_AUTH_ALLOW_HEADER_FALLBACK=false`,
   - задані bearer/caller-context/fallback-disabled policy env.
6. У `docker-compose.platform-runtime.yml` web auth теж strict-by-default:
   - `Identity__Enabled=true` (OIDC-only),
   - development identity fallback вимкнений у runtime profile,
   - `ControlPlaneApi__PropagateIdentityHeaders=false`, `ControlPlaneApi__PropagateAccessToken=true`.

Operational verification lane (step 23):

1. Той самий `ops-slo-security-verify` перевіряє runtime security posture:
   - `authentication.enabled`,
   - bearer/caller-context coverage,
   - header-fallback posture,
   - audit-trail категорії: `session.command`, `session.control*`, `transport.ack`.
2. `ci-local` і `full` запускають цей verify lane як default gate (`Ops SLO + Security Verify`).
3. Emergency override доступний лише явним прапором:
   - `-SkipOpsSloSecurityVerify`.

---

## 7. Роль скриптів

PowerShell scripts не містять business-policy.

Вони використовуються для:

1. thin start/stop orchestration,
2. smoke/acceptance automation,
3. локального операційного runbook.

---

## 8. Статус `exp-022`

`WebRtcTests/exp-022-datachanneldotnet` має статус:

1. integration-lab,
2. tooling для експериментів/діагностики,
3. не production runtime dependency.

Production шлях для edge execution:

1. `Platform/Edge/HidBridge.EdgeProxy.Agent`
2. `Platform/Edge/HidBridge.Edge.HidBridgeProtocol`

---

## 9. Acceptance критерій цього baseline

Runtime baseline вважається підтвердженим, коли:

1. `platform-runtime` profile стабільно піднімає `API/Web/Keycloak/Postgres`,
2. session persistence переживає рестарт API-контейнера,
3. edge-agent smoke/acceptance дає `Applied` у контрольному сценарії,
4. diagnostics endpoint-и віддають typed readiness/health/SLO payload.

Рекомендований acceptance lane:

1. `platform-runtime` (`API/Web/Keycloak/Postgres` у docker).
2. `webrtc-edge-agent-acceptance` у `CommandExecutor=uart` + `SkipRuntimeBootstrap`.
3. Очікуваний результат: `WebRTC Stack=PASS`, `WebRTC Edge Agent Smoke=PASS`, status `Applied`.

---

## 10. Що більше не є runtime policy

PowerShell сценарії не є source of truth для:

1. session auto-create fallback policy,
2. relay readiness/retry policy,
3. lease arbitration logic.

Це все закріплено в `ControlPlane.Api` use-cases/endpoints; скрипти залишаються orchestration/testing harness.

# Micro Meet Next Implementation Plan

> Note (CLI-first): canonical launcher is `HidBridge.RuntimeCtl` direct command syntax.  
> `Platform/run.ps1 -Task ...` references in historical sections are legacy compatibility form.

Дата: 12 березня 2026 (оновлено після стабілізації `demo-flow`)

## Поточний стан

Готово:
1. `doctor`, `ci-local`, `full` стабільно проходять у baseline контурі.
2. Bearer rollout доведений до `Phase 4`.
3. `policy assignment` lifecycle (`activate/deactivate/delete`) реалізований.
4. `Ops Status` має practical links (`Doctor / CI Local / Full / Smoke`).
5. `demo-flow` + `demo-seed` стабілізовані (cleanup, preflight, краща діагностика).
6. Додані runbook-и demo:
   - `Docs/GoToMarket/MicroMeet_Demo_Runbook_UA.md`
   - `Docs/GoToMarket/MicroMeet_Demo_Runbook_EN.md`

## Пріоритетний план подальшої розробки

### Phase 1 (P0): Policy Scope Lifecycle до production-ready

Ціль:
- завершити `policy scope deactivate semantics` так само, як assignment lifecycle.

Кроки:
1. Модель/сховище:
   - `IsActive` для scope в file/sql store + migrations.
2. API:
   - `POST /api/v1/diagnostics/policies/scopes/{scopeId}/activate`
   - `POST /api/v1/diagnostics/policies/scopes/{scopeId}/deactivate`
   - `DELETE /api/v1/diagnostics/policies/scopes/{scopeId}`
3. Runtime policy resolution:
   - inactive scope повністю виключається з effective policy evaluation.
4. UI (`/policy-governance`):
   - `Activate / Deactivate / Delete` для scope
   - явний `Active/Inactive` статус.
5. Тести:
   - lifecycle tests (API/service/store)
   - resolution tests (inactive scope не впливає на доступ).

### Phase 2 (P0): Demo UX hardening для продажу

Ціль:
- зробити демо "натиснув і працює" без ручних recovery-кроків.

Кроки:
1. `demo-flow`:
   - додати `--output-json` підсумок (endpoints, pids, log paths, exit summary).
2. `demo-seed`:
   - детальний machine-readable report (seed result + endpoint availability reason).
3. Web:
   - явний demo banner з room status і кнопкою `Open latest room`.
4. Docs:
   - коротка `Demo Troubleshooting Card` (1 сторінка) для live-call.

### Phase 3 (P1): External operator onboarding

Ціль:
- підключення зовнішніх операторів без ручного patching у Keycloak UI.

Кроки:
1. Скрипт onboarding:
   - assign attributes `tenant_id/org_id/principal_id`
   - assign group/roles (`hidbridge-admins` / `hidbridge-operators`)
2. Dry-run режим:
   - `-WhatIf` / `-PrintOnly` для безпечної перевірки.
3. Validation:
   - smoke для external federated user claims.
4. Docs:
   - `external user onboarding` runbook (UA+EN).

### Phase 4 (P1): Ops Status artifact productization

Ціль:
- зробити `Ops Status` точкою входу для support/debug.

Кроки:
1. Додати direct links:
   - останні `doctor/ci-local/full/demo-flow`
   - останні `artifact exports`.
2. Додати "download bundle" action для latest artifacts.
3. Додати status chips:
   - `PASS/WARN/FAIL`
   - timestamp + duration.

## Test plan (на кожну фазу)

Автоматичні перевірки:
```powershell
dotnet build Platform/HidBridge.Platform.sln -c Debug -v minimal -m:1 -nodeReuse:false
dotnet test Platform/Tests/HidBridge.Platform.Tests/HidBridge.Platform.Tests.csproj -c Debug -v minimal -m:1 -nodeReuse:false
powershell -ExecutionPolicy Bypass -File Platform/run.ps1 -Task ci-local
powershell -ExecutionPolicy Bypass -File Platform/run.ps1 -Task full
powershell -ExecutionPolicy Bypass -File Platform/run.ps1 -Task demo-flow -SkipIdentityReset
```

Manual smoke:
1. `/policy-governance`: scope lifecycle + status transitions.
2. `/ops-status`: links відкривають правильні логи/artifacts.
3. `/` + `/sessions/{id}`: `Launch room`, invite flow, control handoff, timeline update.

## DoD для наступного release

1. Scope lifecycle закритий (API + UI + tests).
2. `demo-flow` проходить в clean environment без ручних fix.
3. External user onboarding працює скриптом, не через ручний Keycloak click-path.
4. `doctor/ci-local/full/demo-flow` зелені.
5. Документація синхронізована (UA+EN runbooks + troubleshooting).

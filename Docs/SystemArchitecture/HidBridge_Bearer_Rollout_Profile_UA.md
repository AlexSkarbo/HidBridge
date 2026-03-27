# HidBridge Bearer Rollout Profile (UA)

> Note (CLI-first): canonical launcher is direct `HidBridge.RuntimeCtl` commands.  
> `RuntimeCtl ...` in historical examples is compatibility-only.

Оновлено: `2026-03-04`

## Поточний етап

Зараз система на етапі:
- `strict bearer-only` уже активний для:
  - `/api/v1/dashboards`
  - `/api/v1/projections`
  - `/api/v1/diagnostics`
  - `/api/v1/sessions`
- `caller-context-required` уже активний для unsafe mutation path-ів під:
  - `/api/v1/sessions`
- `header fallback` ще лишається migration seam для mutation flows, бо:
  - `doctor` показує `smoke-claims = WARN`
  - `smoke-bearer` і `full` уже проходять

Практично це означає:
- operational baseline стабільний
- read-heavy API вже в strict bearer-first режимі
- mutation API ще не готовий до повного fallback-off для всіх шляхів
- після `identity-reset` strict rollback runner уже підтверджує `Phase 4` як effective rollout state

## Поточний статус готовності

1. `realm sync`: `PASS`
2. `doctor`: `PASS`
3. `smoke-client`: `PASS`
4. `smoke-claims`: `PASS`
5. `smoke-bearer`: `PASS`
6. `full`: `PASS`

## Роллаут-політика

Не відрізати fallback одним рубильником.

Правильний порядок:
1. bearer-only для read-heavy API
2. caller-context-required для session-scoped mutation API
3. path-level fallback disable для окремих mutation surfaces
4. ширше fallback removal тільки після стабільного bearer caller context

## Налаштування

### Базові ключі

- `HIDBRIDGE_AUTH_BEARER_ONLY_PREFIXES`
- `HIDBRIDGE_AUTH_CALLER_CONTEXT_REQUIRED_PREFIXES`
- `HIDBRIDGE_AUTH_HEADER_FALLBACK_DISABLED_PATTERNS`

### Wildcard правило

`HIDBRIDGE_AUTH_HEADER_FALLBACK_DISABLED_PATTERNS` підтримує:
- `*` як wildcard для одного path segment

Приклади:
- `/api/v1/sessions/*/control/request`
- `/api/v1/sessions/*/invitations/*/approve`

## Safe Rollout Phases

### Фаза 0 — поточний baseline

- bearer-only:
  - `/api/v1/dashboards`
  - `/api/v1/projections`
  - `/api/v1/diagnostics`
  - `/api/v1/sessions`
- caller-context-required:
  - `/api/v1/sessions`
- fallback-disabled patterns:
  - порожньо

### Фаза 1 — `control` mutations

Рекомендоване значення:

```text
/api/v1/sessions/*/control/request;/api/v1/sessions/*/control/grant;/api/v1/sessions/*/control/force-takeover;/api/v1/sessions/*/control/release
```

Вмикати тільки якщо:
1. `smoke-bearer` зелений
2. `full` зелений
3. bearer caller context уже стабільно проєктується для control actors

Статус:
- [x] Успішно пройдена локально:
  - `doctor` зелений
  - `smoke-bearer` зелений
  - `full` зелений

### Фаза 2 — `invitations` mutations

Рекомендоване значення:

```text
/api/v1/sessions/*/control/request;/api/v1/sessions/*/control/grant;/api/v1/sessions/*/control/force-takeover;/api/v1/sessions/*/control/release;/api/v1/sessions/*/invitations/requests;/api/v1/sessions/*/invitations/*/approve;/api/v1/sessions/*/invitations/*/decline
```

Готовий preset:
- `Platform/Profiles/BearerRollout/Phase2-Invitations.ps1`
- `Platform/Profiles/BearerRollout/Phase2-Invitations.env.example`

Статус:
- [x] Успішно пройдена локально:
  - `doctor` зелений
  - `smoke-bearer` зелений
  - `full` зелений

### Фаза 3 — `shares` / `participants`

Рекомендоване розширення:

```text
/api/v1/sessions/*/participants;/api/v1/sessions/*/participants/*;/api/v1/sessions/*/shares;/api/v1/sessions/*/shares/*/accept;/api/v1/sessions/*/shares/*/reject;/api/v1/sessions/*/shares/*/revoke
```

Готовий preset:
- `Platform/Profiles/BearerRollout/Phase3-SharesParticipants.ps1`
- `Platform/Profiles/BearerRollout/Phase3-SharesParticipants.env.example`

Нормальний спосіб запуску:
```powershell
dotnet run --project Platform/Tools/HidBridge.RuntimeCtl/HidBridge.RuntimeCtl.csproj -- --platform-root Platform bearer-rollout -Phase 4
```

Це:
- застосовує rollout value
- проганяє `doctor`
- проганяє `smoke-bearer`
- проганяє `full`
- пише summary і логи
- якщо фаза ще не bearer-safe, автоматично відкочується через нижчі фази аж до `Phase 0` і лишає effective rollout на останньому зеленому стані

Для жорсткого режиму без rollback:
```powershell
dotnet run --project Platform/Tools/HidBridge.RuntimeCtl/HidBridge.RuntimeCtl.csproj -- --platform-root Platform bearer-rollout -Phase 4 -NoAutoRollback
```

### Фаза 4 — `commands`

Останній етап:

```text
/api/v1/sessions/*/commands
```

`commands` відрізати останніми, бо це найбільш чутливий mutation surface.

## Recommended First Activation

Перший безпечний кандидат:

```text
HIDBRIDGE_AUTH_HEADER_FALLBACK_DISABLED_PATTERNS=/api/v1/sessions/*/control/request;/api/v1/sessions/*/control/grant;/api/v1/sessions/*/control/force-takeover;/api/v1/sessions/*/control/release
```

Готовий preset:
- `Platform/Profiles/BearerRollout/Phase1-Control.ps1`
- `Platform/Profiles/BearerRollout/Phase1-Control.env.example`

## Verification Checklist

Після кожної фази проганяти:

1. realm sync
```powershell
powershell -ExecutionPolicy Bypass -File Platform/Identity/Keycloak/Sync-HidBridgeDevRealm.ps1
```

2. doctor
```powershell
dotnet run --project Platform/Tools/HidBridge.RuntimeCtl/HidBridge.RuntimeCtl.csproj -- --platform-root Platform doctor
```

3. bearer smoke
```powershell
dotnet run --project Platform/Tools/HidBridge.RuntimeCtl/HidBridge.RuntimeCtl.csproj -- --platform-root Platform smoke-bearer
```

4. full pipeline
```powershell
dotnet run --project Platform/Tools/HidBridge.RuntimeCtl/HidBridge.RuntimeCtl.csproj -- --platform-root Platform full
```

## Stop Conditions

Не йти в наступну фазу, якщо:
- `doctor` перестає бути зеленим
- `smoke-bearer` падає
- `full` падає
- `smoke-claims` погіршується до фактичного blocker-а для mutation path-ів

## Поточне рішення

На сьогодні:
- operational kit готовий
- staged fallback removal plumbing готовий
- `identity-reset` нормалізує dev realm і bearer caller context
- `Phase 1` (`control`) пройдена
- `Phase 2` (`invitations`) пройдена
- `Phase 3` (`shares / participants`) пройдена
- `Phase 4` (`commands`) пройдена
- current effective rollout state = `Phase 4`

## Dev Realm Reset

Для контрольованого reset/reimport dev realm:

```powershell
dotnet run --project Platform/Tools/HidBridge.RuntimeCtl/HidBridge.RuntimeCtl.csproj -- --platform-root Platform identity-reset
```

Скрипт:
- робить backup поточного realm
- видаляє `hidbridge-dev`
- відтворює його з import
- перевіряє direct-grant token issuance для smoke users

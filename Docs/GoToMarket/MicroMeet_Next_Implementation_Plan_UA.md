# Micro Meet Next Implementation Plan

Дата: 12 березня 2026

## Поточний стан

База demo-контуру стабільна:
- `doctor`/`ci-local`/`full` проходять
- bearer rollout до `Phase 4` проходить
- policy assignment lifecycle (`activate/deactivate/delete`) уже є

## Наступний практичний блок

### A. `policy scope deactivate semantics`

Ціль:
- scope можна деактивувати/активувати/видаляти
- inactive scope не бере участі в effective policy resolution

Роботи:
1. Додати `IsActive` у scope model/store (file + sql).
2. API дії:
   - `POST /api/v1/diagnostics/policies/scopes/{scopeId}/activate`
   - `POST /api/v1/diagnostics/policies/scopes/{scopeId}/deactivate`
   - `DELETE /api/v1/diagnostics/policies/scopes/{scopeId}`
3. Policy governance UI:
   - кнопки activate/deactivate/delete для scopes
   - статус `Active/Inactive`
4. Оновити policy resolution, щоб inactive scope ігнорувався.
5. Додати unit/integration тести.

### B. `Ops Status` links polishing

Ціль:
- оператор бачить лінки на актуальні логи/артефакти без ручного пошуку

Роботи:
1. Додати чіткі секції:
   - `doctor latest`
   - `ci-local latest`
   - `full latest`
   - `smoke latest`
2. Для кожної секції:
   - лог-файл
   - artifacts root (якщо є)
3. Показувати timestamp останнього запуску.
4. Додати graceful-message, якщо запусків ще не було.

## Як тестувати цей блок

```powershell
dotnet build Platform/HidBridge.Platform.sln -c Debug -v minimal -m:1 -nodeReuse:false
dotnet test Platform/Tests/HidBridge.Platform.Tests/HidBridge.Platform.Tests.csproj -c Debug -v minimal -m:1 -nodeReuse:false
powershell -ExecutionPolicy Bypass -File Platform/run.ps1 -Task doctor -StartApiProbe -RequireApi
powershell -ExecutionPolicy Bypass -File Platform/run.ps1 -Task ci-local
powershell -ExecutionPolicy Bypass -File Platform/run.ps1 -Task full
```

Manual:
1. Відкрити `/policy-governance`, перевірити lifecycle scopes.
2. Відкрити `/ops-status`, перевірити лінки та timestamps.
3. Перевірити, що inactive scope не дає прав у runtime flow.

## Критерій готовності (DoD)

- scope lifecycle працює через API + UI
- policy resolution коректно ігнорує inactive scopes
- є тести для lifecycle і resolution
- `doctor`/`ci-local`/`full` проходять
- Ops Status показує робочі лінки на логи/артефакти

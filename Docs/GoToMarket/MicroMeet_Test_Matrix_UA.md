# Micro Meet Test Matrix (Demo/Sales)

> Note (CLI-first): use direct `HidBridge.RuntimeCtl` commands.  
> `RuntimeCtl ...` references below are canonical command form.

Дата фіксації базового стану: 12 березня 2026

## 1) Gate-команди перед демо

```powershell
dotnet run --project Platform/Tools/HidBridge.RuntimeCtl/HidBridge.RuntimeCtl.csproj -- --platform-root Platform doctor -StartApiProbe -RequireApi
dotnet run --project Platform/Tools/HidBridge.RuntimeCtl/HidBridge.RuntimeCtl.csproj -- --platform-root Platform ci-local -StopOnFailure
dotnet run --project Platform/Tools/HidBridge.RuntimeCtl/HidBridge.RuntimeCtl.csproj -- --platform-root Platform full -StopOnFailure
```

Очікування:
- усі задачі `PASS`
- `exit code = 0`

## 2) Manual product flow matrix

| ID | Сценарій | Кроки | Очікування |
|---|---|---|---|
| UI-01 | Fleet відкривається | Відкрити `/` | Є список endpoint-ів і quick actions |
| UI-02 | Start session | Натиснути `Start session` або `Launch room` | Перехід на `/sessions/{id}` |
| UI-03 | Session room layout | Відкрити `Session Room` | Видно snapshot/control/invitations/participants/timeline |
| UI-04 | Invite flow | Grant invite або request invite, approve/accept | Другий оператор з’являється у participants |
| UI-05 | Control handoff | Request control -> release/force takeover | Статус контролера оновлюється коректно |
| UI-06 | Timeline | Виконати 2-3 дії в кімнаті | Події з’являються в timeline |
| UI-07 | Denial UX | Викликати дію без прав | UI показує відмову без падіння сторінки |
| UI-08 | API unavailable UX | Зупинити API, відкрити web | Показується graceful backend-unavailable state |

## 3) Auth/bearer rollout matrix

| ID | Сценарій | Команда | Очікування |
|---|---|---|---|
| AUTH-01 | Claims readiness | `dotnet run --project Platform/Tools/HidBridge.RuntimeCtl/HidBridge.RuntimeCtl.csproj -- --platform-root Platform token-debug` | Є `preferred_username`, `tenant_id`, `org_id`, `operator.* roles` |
| AUTH-02 | Doctor auth checks | `dotnet run --project Platform/Tools/HidBridge.RuntimeCtl/HidBridge.RuntimeCtl.csproj -- --platform-root Platform doctor` | `smoke-client PASS`, `smoke-claims PASS` |
| AUTH-03 | Bearer smoke | `dotnet run --project Platform/Tools/HidBridge.RuntimeCtl/HidBridge.RuntimeCtl.csproj -- --platform-root Platform smoke-bearer` | `PASS` |
| AUTH-04 | Rollout phase verify | `dotnet run --project Platform/Tools/HidBridge.RuntimeCtl/HidBridge.RuntimeCtl.csproj -- --platform-root Platform bearer-rollout -Phase 4` | `Doctor/Bearer Smoke/Full = PASS` |

## 4) Regression safety matrix

| ID | Сценарій | Команда | Очікування |
|---|---|---|---|
| REG-01 | Build | `dotnet build Platform/HidBridge.Platform.sln -c Debug -v minimal -m:1 -nodeReuse:false` | `Build succeeded` |
| REG-02 | Tests | `dotnet test Platform/Tests/HidBridge.Platform.Tests/HidBridge.Platform.Tests.csproj -c Debug -v minimal -m:1 -nodeReuse:false` | Усі тести PASS |
| REG-03 | Full contour | `dotnet run --project Platform/Tools/HidBridge.RuntimeCtl/HidBridge.RuntimeCtl.csproj -- --platform-root Platform full` | `Realm Sync PASS`, `CI Local PASS` |

## 5) Demo stop conditions

Демо зупиняємо і не публікуємо апдейт, якщо:
- `doctor` має `FAIL`
- `ci-local` має `FAIL`
- `full` має `FAIL`
- `token-debug` не показує caller-context claims
- `Session Room` падає або invite/control flow не проходить

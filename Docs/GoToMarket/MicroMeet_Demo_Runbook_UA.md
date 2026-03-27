# Micro Meet Demo Runbook (Step-by-Step)

> Note (CLI-first): canonical commands are direct `HidBridge.RuntimeCtl` invocations.  
> `dotnet run --project Platform/Tools/HidBridge.RuntimeCtl/HidBridge.RuntimeCtl.csproj -- --platform-root Platform ...` in this file is kept as canonical command syntax.

## Мета
Провести стабільне demo без ручного "лікування" інфраструктури:

1. Підготувати середовище.
2. Запустити автоматичний demo-flow.
3. Показати кімнату сесії з invite/control handoff.
4. Підтвердити після демо, що контур лишився зеленим.

## 1) Preflight (чистий старт)

1. Перейти в корінь репозиторію.
```powershell
cd C:\Work\Pocker\Server\pico_hid_bridge_v9.9
```

2. Зупинити старі API/Web рантайми (щоб уникнути конфліктів env/auth режимів).
```powershell
Get-CimInstance Win32_Process -Filter "Name='dotnet.exe'" | Where-Object {
  $_.CommandLine -like "*HidBridge.ControlPlane.Api.csproj*" -or
  $_.CommandLine -like "*HidBridge.ControlPlane.Web.csproj*"
} | ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
```

3. Базова перевірка.
```powershell
dotnet run --project Platform/Tools/HidBridge.RuntimeCtl/HidBridge.RuntimeCtl.csproj -- --platform-root Platform doctor -StartApiProbe -RequireApi
```

## 2) Базовий quality gate перед демо

1. Локальний інтеграційний контур.
```powershell
dotnet run --project Platform/Tools/HidBridge.RuntimeCtl/HidBridge.RuntimeCtl.csproj -- --platform-root Platform ci-local
```

2. Повний контур.
```powershell
dotnet run --project Platform/Tools/HidBridge.RuntimeCtl/HidBridge.RuntimeCtl.csproj -- --platform-root Platform full
```

Очікування:
- `Doctor PASS`
- `Checks (Sql) PASS`
- `Bearer Smoke PASS`
- `Realm Sync PASS`
- `CI Local PASS`

## 3) Автоматичний запуск демо

1. Стандартний сценарій (рекомендовано).
```powershell
dotnet run --project Platform/Tools/HidBridge.RuntimeCtl/HidBridge.RuntimeCtl.csproj -- --platform-root Platform demo-flow -SkipIdentityReset
```

2. Якщо свідомо хочеш реюзати вже запущені API/Web процеси.
```powershell
dotnet run --project Platform/Tools/HidBridge.RuntimeCtl/HidBridge.RuntimeCtl.csproj -- --platform-root Platform demo-flow -SkipIdentityReset -ReuseRunningServices
```

Успішний результат:
- `Doctor PASS`
- `CI Local PASS`
- `Start API PASS`
- `Demo Seed PASS`
- `Demo Gate PASS`
- `Start Web PASS`

## 4) Що робити в UI під час демо

1. Відкрити URL, який вивів `demo-flow` у блоці `Demo endpoints`.
2. Залогінитись через Keycloak.
3. На `/` (Fleet Overview):
- у `Quick Start` натиснути `Launch room`, або
- в endpoint-картці натиснути `Start session`.
4. Перевірити перехід на `/sessions/{id}`.
5. У `Session Room` показати:
- `Session Snapshot`
- `Control Operations`
- `Invitation Moderation`
- `Participants`
- `Timeline`
6. Скопіювати `Room link`, відкрити у другому профілі браузера.
7. В першому профілі виконати один із сценаріїв:
- `Grant invite`, або
- прийняти `Request invite` від другого профілю.
8. Виконати handoff:
- `Request control`
- `Release`
- `Force takeover` (для moderator/admin)
9. Підтвердити, що `Timeline` оновився.

## 5) Перевірка після демо

```powershell
dotnet run --project Platform/Tools/HidBridge.RuntimeCtl/HidBridge.RuntimeCtl.csproj -- --platform-root Platform smoke-bearer
```

Очікування:
- `PASS`

## 6) Як зупинити процеси після демо

1. Якщо `demo-flow` вивів `Stop-Process -Id ...`, виконати ці команди.
2. Або масово зупинити API/Web:
```powershell
Get-CimInstance Win32_Process -Filter "Name='dotnet.exe'" | Where-Object {
  $_.CommandLine -like "*HidBridge.ControlPlane.Api.csproj*" -or
  $_.CommandLine -like "*HidBridge.ControlPlane.Web.csproj*"
} | ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
```

## 7) Швидкий troubleshooting

1. `demo-seed` з помилкою `cannot connect`:
- причина: API не запущений;
- дія: запускати `demo-seed` тільки після `Start API` або через `demo-flow`.

2. `open session -> 401 authentication_required` у smoke/ci-local:
- причина: на порту 18093 лишився API з іншим auth-режимом;
- дія: зупинити процеси API/Web і повторити `ci-local`, потім `demo-flow`.

3. `invalid_scope` на `/signin-oidc`:
- причина: Keycloak realm/client-scope не синхронізовані;
- дія:
```powershell
dotnet run --project Platform/Tools/HidBridge.RuntimeCtl/HidBridge.RuntimeCtl.csproj -- --platform-root Platform identity-reset
```

4. Проблеми з зовнішнім Google provider після reset:
- дія:
```powershell
powershell -ExecutionPolicy Bypass -File Platform/Identity/Keycloak/Sync-HidBridgeDevRealm.ps1 -ExternalProviderConfigPaths "Platform/Identity/Keycloak/providers/google-oidc.local.json"
```

5. Команди мають `Applied`, але на цільовому ПК немає ефекту (текст/шорткат/рух миші):
- причина: невдалий fixed `itfSel` для поточного USB HID маршруту;
- дія: повернути auto-selectors і перевірити UART path через diagnostics:
```powershell
# runtime env (API)
$env:HIDBRIDGE_UART_MOUSE_SELECTOR="255"
$env:HIDBRIDGE_UART_KEYBOARD_SELECTOR="254"

dotnet run --project Platform/Tools/HidBridge.RuntimeCtl/HidBridge.RuntimeCtl.csproj -- --platform-root Platform uart-diagnostics -BaseUrl http://127.0.0.1:18093
```

## 8) Де шукати логи

- `Platform\.logs\doctor\...`
- `Platform\.logs\ci-local\...`
- `Platform\.logs\full\...`
- `Platform\.logs\demo-flow\...`
- `Platform\.smoke-data\Sql\...`

# Micro Meet Demo Runbook (Step-by-Step)

> Note (CLI-first): canonical commands are direct `HidBridge.RuntimeCtl` invocations.  
> `dotnet run --project Platform/Tools/HidBridge.RuntimeCtl/HidBridge.RuntimeCtl.csproj -- --platform-root Platform ...` in this file is kept as canonical command syntax.

## Goal
Run a stable product-style demo without manual infrastructure recovery:

1. Prepare the environment.
2. Run automated demo flow.
3. Demonstrate session room with invite/control handoff.
4. Confirm the stack is still green after demo.

## 1) Preflight (clean start)

1. Go to repository root.
```powershell
cd C:\Work\Pocker\Server\pico_hid_bridge_v9.9
```

2. Stop old API/Web runtimes (prevents auth/env conflicts on reused ports).
```powershell
Get-CimInstance Win32_Process -Filter "Name='dotnet.exe'" | Where-Object {
  $_.CommandLine -like "*HidBridge.ControlPlane.Api.csproj*" -or
  $_.CommandLine -like "*HidBridge.ControlPlane.Web.csproj*"
} | ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
```

3. Baseline stack check.
```powershell
dotnet run --project Platform/Tools/HidBridge.RuntimeCtl/HidBridge.RuntimeCtl.csproj -- --platform-root Platform doctor -StartApiProbe -RequireApi
```

## 2) Quality gate before demo

1. Local integration contour.
```powershell
dotnet run --project Platform/Tools/HidBridge.RuntimeCtl/HidBridge.RuntimeCtl.csproj -- --platform-root Platform ci-local
```

2. Full contour.
```powershell
dotnet run --project Platform/Tools/HidBridge.RuntimeCtl/HidBridge.RuntimeCtl.csproj -- --platform-root Platform full
```

Expected:
- `Doctor PASS`
- `Checks (Sql) PASS`
- `Bearer Smoke PASS`
- `Realm Sync PASS`
- `CI Local PASS`

## 3) Automated demo startup

1. Standard mode (recommended).
```powershell
dotnet run --project Platform/Tools/HidBridge.RuntimeCtl/HidBridge.RuntimeCtl.csproj -- --platform-root Platform demo-flow -SkipIdentityReset
```

2. Reuse currently running API/Web services (only if intentional).
```powershell
dotnet run --project Platform/Tools/HidBridge.RuntimeCtl/HidBridge.RuntimeCtl.csproj -- --platform-root Platform demo-flow -SkipIdentityReset -ReuseRunningServices
```

Expected:
- `Doctor PASS`
- `CI Local PASS`
- `Start API PASS`
- `Demo Seed PASS`
- `Demo Gate PASS`
- `Start Web PASS`

## 4) UI demo script

1. Open the `Start Web` URL printed by `demo-flow`.
2. Sign in via Keycloak.
3. On `/` (Fleet Overview):
- click `Launch room` in `Quick Start`, or
- click `Start session` in endpoint card.
4. Confirm navigation to `/sessions/{id}`.
5. In `Session Room`, show:
- `Session Snapshot`
- `Control Operations`
- `Invitation Moderation`
- `Participants`
- `Timeline`
6. Copy `Room link` and open it in a second browser profile.
7. In first profile:
- use `Grant invite`, or
- approve `Request invite` from second profile.
8. Perform control handoff:
- `Request control`
- `Release`
- `Force takeover` (moderator/admin)
9. Confirm `Timeline` updates.

## 5) Post-demo check

```powershell
dotnet run --project Platform/Tools/HidBridge.RuntimeCtl/HidBridge.RuntimeCtl.csproj -- --platform-root Platform smoke-bearer
```

Expected:
- `PASS`

## 6) Stop processes after demo

1. If `demo-flow` printed `Stop-Process -Id ...`, run those commands.
2. Or stop API/Web processes in one command:
```powershell
Get-CimInstance Win32_Process -Filter "Name='dotnet.exe'" | Where-Object {
  $_.CommandLine -like "*HidBridge.ControlPlane.Api.csproj*" -or
  $_.CommandLine -like "*HidBridge.ControlPlane.Web.csproj*"
} | ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
```

## 7) Quick troubleshooting

1. `demo-seed` fails with `cannot connect`:
- Cause: API is not running.
- Action: run `demo-flow` (or start API first), then run `demo-seed`.

2. `open session -> 401 authentication_required` in smoke/ci-local:
- Cause: stale API process on `18093` with different auth mode.
- Action: stop API/Web processes, rerun `ci-local`, then rerun `demo-flow`.

3. `invalid_scope` on `/signin-oidc`:
- Cause: Keycloak realm/client scopes not synchronized.
- Action:
```powershell
dotnet run --project Platform/Tools/HidBridge.RuntimeCtl/HidBridge.RuntimeCtl.csproj -- --platform-root Platform identity-reset
```

4. External Google provider missing after reset:
- Action:
```powershell
powershell -ExecutionPolicy Bypass -File Platform/Identity/Keycloak/Sync-HidBridgeDevRealm.ps1 -ExternalProviderConfigPaths "Platform/Identity/Keycloak/providers/google-oidc.local.json"
```

5. Commands show `Applied`, but there is no visible effect on the target PC (text/shortcut/mouse move):
- Cause: fixed `itfSel` is not valid for the current USB HID route.
- Action: switch back to auto-selectors and run UART diagnostics:
```powershell
# runtime env (API)
$env:HIDBRIDGE_UART_MOUSE_SELECTOR="255"
$env:HIDBRIDGE_UART_KEYBOARD_SELECTOR="254"

dotnet run --project Platform/Tools/HidBridge.RuntimeCtl/HidBridge.RuntimeCtl.csproj -- --platform-root Platform uart-diagnostics -BaseUrl http://127.0.0.1:18093
```

## 8) Where to read logs

- `Platform\.logs\doctor\...`
- `Platform\.logs\ci-local\...`
- `Platform\.logs\full\...`
- `Platform\.logs\demo-flow\...`
- `Platform\.smoke-data\Sql\...`

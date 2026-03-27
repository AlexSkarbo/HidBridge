# RuntimeCtl On-Call How-To (UA)

Короткий operational runbook для локального/CI smoke після переходу на `RuntimeCtl` (CLI-first, без PowerShell-оркестрації).

## 1) Швидкий старт (3 команди)

```powershell
dotnet run --project Platform/Tools/HidBridge.RuntimeCtl/HidBridge.RuntimeCtl.csproj -- --platform-root Platform platform-runtime -Action up -Build
dotnet run --project Platform/Tools/HidBridge.RuntimeCtl/HidBridge.RuntimeCtl.csproj -- --platform-root Platform ci-local -StopOnFailure
dotnet run --project Platform/Tools/HidBridge.RuntimeCtl/HidBridge.RuntimeCtl.csproj -- --platform-root Platform full -StopOnFailure
```

Очікування: обидва gate-и (`ci-local`, `full`) мають завершитись `PASS`.

## 2) Якщо завис `webrtc-acceptance`

Ознака: довго стоїть на `Waiting for WebRTC stack summary ...`.

Що робити:

```powershell
# best-effort cleanup stale .NET процесів acceptance/agent
$needles = @('HidBridge.RuntimeCtl','HidBridge.Acceptance.Runner','HidBridge.EdgeProxy.Agent')
Get-CimInstance Win32_Process -Filter "Name='dotnet.exe'" |
  ? { $cl=[string]$_.CommandLine; $cl -and ($needles | ? { $cl -like "*$_*" }) } |
  % { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }

# повторний запуск acceptance з таймаутами
dotnet run --project Platform/Tools/HidBridge.RuntimeCtl/HidBridge.RuntimeCtl.csproj -- --platform-root Platform webrtc-acceptance -CommandExecutor uart -SkipRuntimeBootstrap -StopExisting -StopStackAfter -StackBootstrapTimeoutSec 90 -MaxDurationSec 420
```

Примітка: за замовчуванням уже ввімкнено auto-cleanup stale процесів.

## 3) Де дивитися логи

- `Platform/.logs/ci-local/<timestamp>/`
- `Platform/.logs/full/<timestamp>/`
- `Platform/.logs/webrtc-edge-agent-acceptance/<timestamp>/`

Критичні файли:

- `WebRTC-Edge-Agent-Acceptance.log`
- `CI-Local.log`
- `Auth-API-Resync.log`
- `webrtc-stack.stdout.log`
- `webrtc-stack.stderr.log`

## 4) PASS/FAIL критерії (щоденний smoke)

- `ci-local`: `Doctor`, `Checks (Sql)`, `Bearer Smoke`, `WebRTC Edge Agent Acceptance`, `Ops SLO + Security Verify` = `PASS`.
- `full`: `Realm Sync`, `Auth/API Resync`, `CI Local` = `PASS`.

## 5) Рекомендований порядок дій при інциденті

1. Перезапустити acceptance з `-StopExisting`.
2. Якщо повторно fail — перевірити `webrtc-stack.stderr.log`.
3. Якщо після `identity-reset` нестабільно — запускати `full` (він включає `Auth/API Resync`).
4. Якщо відтворюється в CI — додати посилання на конкретний `<timestamp>` каталог логів у тикет.

## 6) Що змінилось у стабілізації (release note)

- `webrtc-acceptance` отримав явний bootstrap timeout (`-StackBootstrapTimeoutSec`).
- На timeout bootstrap тепер kill-иться весь process tree.
- Додано auto-cleanup stale acceptance/agent процесів при fail/timeout.
- `full` після `identity-reset` робить `Auth/API Resync`, що стабілізує повторні прогони.

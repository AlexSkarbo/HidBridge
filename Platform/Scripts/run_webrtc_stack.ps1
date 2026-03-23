param(
    [string]$ApiBaseUrl = "http://127.0.0.1:18093",
    [string]$WebBaseUrl = "http://127.0.0.1:18110",
    [ValidateSet("uart", "controlws")]
    [string]$CommandExecutor = "uart",
    [switch]$AllowLegacyControlWs,
    [string]$ControlWsUrl = "ws://127.0.0.1:28092/ws/control",
    [string]$UartPort = "COM6",
    [int]$UartBaud = 3000000,
    [string]$UartHmacKey = "your-master-secret",
    [int]$Exp022DurationSec = 600,
    [int]$AdapterDurationSec = 900,
    [string]$EndpointId = "endpoint_local_demo",
    [string]$PrincipalId = "smoke-runner",
    [string]$TokenUsername = "operator.smoke.admin",
    [string]$TokenPassword = "ChangeMe123!",
    [string]$TokenScope = "",
    [int]$PeerReadyTimeoutSec = 30,
    [string]$OutputJsonPath = "",
    [switch]$SkipIdentityReset = $true,
    [switch]$SkipCiLocal = $true,
    [switch]$SkipRuntimeBootstrap,
    [switch]$StopExisting
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$scriptsRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$platformRoot = Split-Path -Parent $scriptsRoot
$repoRoot = Split-Path -Parent $platformRoot
. (Join-Path $scriptsRoot "Common/ScriptCommon.ps1")

$logRoot = New-OperationalLogRoot -PlatformRoot $platformRoot -Category "webrtc-stack"
$session = "room-webrtc-peer-" + [DateTimeOffset]::UtcNow.ToString("yyyyMMddHHmmss")
$peerId = if ([string]::Equals($CommandExecutor, "uart", [StringComparison]::OrdinalIgnoreCase)) { "peer-local-uart-edge" } else { "peer-local-exp022" }
$sessionEnvPath = Join-Path $platformRoot ".logs/webrtc-peer-adapter.session.env"
$stackSummaryPath = Join-Path $logRoot "webrtc-stack.summary.json"
$demoFlowScript = Join-Path $scriptsRoot "run_demo_flow.ps1"
$edgeAgentProject = Join-Path $repoRoot "Platform/Edge/HidBridge.EdgeProxy.Agent/HidBridge.EdgeProxy.Agent.csproj"
$exp022Script = Join-Path $repoRoot "WebRtcTests/exp-022-datachanneldotnet/start.ps1"
$exp022Stdout = Join-Path $logRoot "exp022.stdout.log"
$exp022Stderr = Join-Path $logRoot "exp022.stderr.log"
$adapterStdout = Join-Path $logRoot "webrtc-peer-adapter.stdout.log"
$adapterStderr = Join-Path $logRoot "webrtc-peer-adapter.stderr.log"

# Thin helper: best-effort stop by name token when caller requests `-StopExisting`.
function Stop-MatchingProcesses {
    param(
        [string]$ProcessName,
        [string[]]$Needles
    )

    $processes = @(Get-CimInstance Win32_Process -Filter "Name='$ProcessName'" -ErrorAction SilentlyContinue)
    $currentPid = $PID
    foreach ($proc in $processes) {
        if ($proc.ProcessId -eq $currentPid) {
            continue
        }

        $line = [string]$proc.CommandLine
        if ([string]::IsNullOrWhiteSpace($line)) {
            continue
        }

        $matches = $false
        foreach ($needle in $Needles) {
            if ($line -like "*$needle*") {
                $matches = $true
                break
            }
        }

        if (-not $matches) {
            continue
        }

        try {
            Stop-Process -Id $proc.ProcessId -Force -ErrorAction Stop
            Write-Host "Stopped PID $($proc.ProcessId): $ProcessName"
        }
        catch {
            Write-Warning "Failed to stop PID $($proc.ProcessId): $($_.Exception.Message)"
        }
    }
}

if ($PSBoundParameters.ContainsKey("PeerReadyTimeoutSec")) {
    Write-Warning "PeerReadyTimeoutSec is deprecated for webrtc-stack and ignored."
}
if ($PSBoundParameters.ContainsKey("TokenScope") -and -not [string]::IsNullOrWhiteSpace($TokenScope)) {
    Write-Warning "TokenScope is deprecated for webrtc-stack and ignored."
}
if ($PSBoundParameters.ContainsKey("AdapterDurationSec")) {
    Write-Warning "AdapterDurationSec is deprecated for webrtc-stack and ignored."
}

if ([string]::Equals($CommandExecutor, "controlws", [StringComparison]::OrdinalIgnoreCase)) {
    if (-not $AllowLegacyControlWs) {
        throw "CommandExecutor 'controlws' is legacy compatibility mode. Use 'uart' or pass -AllowLegacyControlWs."
    }

    Assert-LegacyExp022Enabled -Context "run_webrtc_stack.ps1 controlws mode"
}

if ($StopExisting) {
    $needles = @("HidBridge.EdgeProxy.Agent", "exp-022-datachanneldotnet")
    Stop-MatchingProcesses -ProcessName "dotnet.exe" -Needles $needles
    Stop-MatchingProcesses -ProcessName "pwsh.exe" -Needles $needles
    Stop-MatchingProcesses -ProcessName "powershell.exe" -Needles $needles
}

if (-not $SkipRuntimeBootstrap) {
    # Runtime bootstrap only. No readiness/policy logic here.
    $demoFlowParameters = @{
        ApiBaseUrl = $ApiBaseUrl
        WebBaseUrl = $WebBaseUrl
        SkipDoctor = $true
        SkipDemoGate = $true
    }

    if ($SkipIdentityReset) { $demoFlowParameters.SkipIdentityReset = $true }
    if ($SkipCiLocal) { $demoFlowParameters.SkipCiLocal = $true }

    & $demoFlowScript @demoFlowParameters
    if ($LASTEXITCODE -ne 0) {
        throw "demo-flow failed with exit code $LASTEXITCODE."
    }
}

@"
SESSION_ID=$session
PEER_ID=$peerId
ENDPOINT_ID=$EndpointId
PRINCIPAL_ID=$PrincipalId
TENANT_ID=local-tenant
ORGANIZATION_ID=local-org
COMMAND_EXECUTOR=$CommandExecutor
"@ | Set-Content -Path $sessionEnvPath -Encoding ascii

New-Item -ItemType File -Force -Path $exp022Stdout | Out-Null
New-Item -ItemType File -Force -Path $exp022Stderr | Out-Null
New-Item -ItemType File -Force -Path $adapterStdout | Out-Null
New-Item -ItemType File -Force -Path $adapterStderr | Out-Null

$pwsh = Get-Command pwsh.exe -ErrorAction SilentlyContinue
if ($null -eq $pwsh) {
    $pwsh = Get-Command powershell.exe -ErrorAction SilentlyContinue
}
if ($null -eq $pwsh) {
    throw "Neither pwsh.exe nor powershell.exe found in PATH."
}

$dotnet = Get-Command dotnet.exe -ErrorAction SilentlyContinue
if ($null -eq $dotnet) {
    throw "dotnet.exe not found in PATH."
}

$exp022Process = $null
$adapterProcess = $null
if ([string]::Equals($CommandExecutor, "controlws", [StringComparison]::OrdinalIgnoreCase)) {
    $wsUri = [Uri]$ControlWsUrl
    $wsBind = if ([string]::Equals($wsUri.Host, "localhost", [StringComparison]::OrdinalIgnoreCase)) { "127.0.0.1" } else { $wsUri.Host }
    $exp022Args = @(
        "-NoProfile",
        "-ExecutionPolicy", "Bypass",
        "-File", $exp022Script,
        "-Mode", "dc-hid-poc",
        "-UartPort", $UartPort,
        "-UartBaud", [string]$UartBaud,
        "-UartHmacKey", $UartHmacKey,
        "-ControlWsBind", $wsBind,
        "-ControlWsPort", [string]$wsUri.Port,
        "-DurationSec", [string]$Exp022DurationSec
    )

    $exp022Process = Start-Process -FilePath $pwsh.Source -ArgumentList $exp022Args -WorkingDirectory $repoRoot -PassThru -RedirectStandardOutput $exp022Stdout -RedirectStandardError $exp022Stderr
}

$edgeEnvRestore = @{
    HIDBRIDGE_EDGE_PROXY_BASEURL = [Environment]::GetEnvironmentVariable("HIDBRIDGE_EDGE_PROXY_BASEURL", [EnvironmentVariableTarget]::Process)
    HIDBRIDGE_EDGE_PROXY_SESSIONID = [Environment]::GetEnvironmentVariable("HIDBRIDGE_EDGE_PROXY_SESSIONID", [EnvironmentVariableTarget]::Process)
    HIDBRIDGE_EDGE_PROXY_PEERID = [Environment]::GetEnvironmentVariable("HIDBRIDGE_EDGE_PROXY_PEERID", [EnvironmentVariableTarget]::Process)
    HIDBRIDGE_EDGE_PROXY_ENDPOINTID = [Environment]::GetEnvironmentVariable("HIDBRIDGE_EDGE_PROXY_ENDPOINTID", [EnvironmentVariableTarget]::Process)
    HIDBRIDGE_EDGE_PROXY_CONTROLWSURL = [Environment]::GetEnvironmentVariable("HIDBRIDGE_EDGE_PROXY_CONTROLWSURL", [EnvironmentVariableTarget]::Process)
    HIDBRIDGE_EDGE_PROXY_PRINCIPALID = [Environment]::GetEnvironmentVariable("HIDBRIDGE_EDGE_PROXY_PRINCIPALID", [EnvironmentVariableTarget]::Process)
    HIDBRIDGE_EDGE_PROXY_TOKENUSERNAME = [Environment]::GetEnvironmentVariable("HIDBRIDGE_EDGE_PROXY_TOKENUSERNAME", [EnvironmentVariableTarget]::Process)
    HIDBRIDGE_EDGE_PROXY_TOKENPASSWORD = [Environment]::GetEnvironmentVariable("HIDBRIDGE_EDGE_PROXY_TOKENPASSWORD", [EnvironmentVariableTarget]::Process)
    HIDBRIDGE_EDGE_PROXY_KEYCLOAKBASEURL = [Environment]::GetEnvironmentVariable("HIDBRIDGE_EDGE_PROXY_KEYCLOAKBASEURL", [EnvironmentVariableTarget]::Process)
    HIDBRIDGE_EDGE_PROXY_KEYCLOAKREALM = [Environment]::GetEnvironmentVariable("HIDBRIDGE_EDGE_PROXY_KEYCLOAKREALM", [EnvironmentVariableTarget]::Process)
    HIDBRIDGE_EDGE_PROXY_COMMANDEXECUTOR = [Environment]::GetEnvironmentVariable("HIDBRIDGE_EDGE_PROXY_COMMANDEXECUTOR", [EnvironmentVariableTarget]::Process)
    HIDBRIDGE_EDGE_PROXY_UARTPORT = [Environment]::GetEnvironmentVariable("HIDBRIDGE_EDGE_PROXY_UARTPORT", [EnvironmentVariableTarget]::Process)
    HIDBRIDGE_EDGE_PROXY_UARTBAUD = [Environment]::GetEnvironmentVariable("HIDBRIDGE_EDGE_PROXY_UARTBAUD", [EnvironmentVariableTarget]::Process)
    HIDBRIDGE_EDGE_PROXY_UARTHMACKEY = [Environment]::GetEnvironmentVariable("HIDBRIDGE_EDGE_PROXY_UARTHMACKEY", [EnvironmentVariableTarget]::Process)
    HIDBRIDGE_EDGE_PROXY_UARTRELEASEPORTAFTEREXECUTE = [Environment]::GetEnvironmentVariable("HIDBRIDGE_EDGE_PROXY_UARTRELEASEPORTAFTEREXECUTE", [EnvironmentVariableTarget]::Process)
    HIDBRIDGE_EDGE_PROXY_UARTMOUSEINTERFACESELECTOR = [Environment]::GetEnvironmentVariable("HIDBRIDGE_EDGE_PROXY_UARTMOUSEINTERFACESELECTOR", [EnvironmentVariableTarget]::Process)
    HIDBRIDGE_EDGE_PROXY_UARTKEYBOARDINTERFACESELECTOR = [Environment]::GetEnvironmentVariable("HIDBRIDGE_EDGE_PROXY_UARTKEYBOARDINTERFACESELECTOR", [EnvironmentVariableTarget]::Process)
    HIDBRIDGE_EDGE_PROXY_MEDIAHEALTHURL = [Environment]::GetEnvironmentVariable("HIDBRIDGE_EDGE_PROXY_MEDIAHEALTHURL", [EnvironmentVariableTarget]::Process)
    HIDBRIDGE_EDGE_PROXY_ASSUMEMEDIAREADYWITHOUTPROBE = [Environment]::GetEnvironmentVariable("HIDBRIDGE_EDGE_PROXY_ASSUMEMEDIAREADYWITHOUTPROBE", [EnvironmentVariableTarget]::Process)
}

try {
    [Environment]::SetEnvironmentVariable("HIDBRIDGE_EDGE_PROXY_BASEURL", $ApiBaseUrl, [EnvironmentVariableTarget]::Process)
    [Environment]::SetEnvironmentVariable("HIDBRIDGE_EDGE_PROXY_SESSIONID", $session, [EnvironmentVariableTarget]::Process)
    [Environment]::SetEnvironmentVariable("HIDBRIDGE_EDGE_PROXY_PEERID", $peerId, [EnvironmentVariableTarget]::Process)
    [Environment]::SetEnvironmentVariable("HIDBRIDGE_EDGE_PROXY_ENDPOINTID", $EndpointId, [EnvironmentVariableTarget]::Process)
    [Environment]::SetEnvironmentVariable("HIDBRIDGE_EDGE_PROXY_CONTROLWSURL", $ControlWsUrl, [EnvironmentVariableTarget]::Process)
    [Environment]::SetEnvironmentVariable("HIDBRIDGE_EDGE_PROXY_PRINCIPALID", $PrincipalId, [EnvironmentVariableTarget]::Process)
    [Environment]::SetEnvironmentVariable("HIDBRIDGE_EDGE_PROXY_TOKENUSERNAME", $TokenUsername, [EnvironmentVariableTarget]::Process)
    [Environment]::SetEnvironmentVariable("HIDBRIDGE_EDGE_PROXY_TOKENPASSWORD", $TokenPassword, [EnvironmentVariableTarget]::Process)
    [Environment]::SetEnvironmentVariable("HIDBRIDGE_EDGE_PROXY_KEYCLOAKBASEURL", "http://127.0.0.1:18096", [EnvironmentVariableTarget]::Process)
    [Environment]::SetEnvironmentVariable("HIDBRIDGE_EDGE_PROXY_KEYCLOAKREALM", "hidbridge-dev", [EnvironmentVariableTarget]::Process)
    [Environment]::SetEnvironmentVariable("HIDBRIDGE_EDGE_PROXY_COMMANDEXECUTOR", $CommandExecutor, [EnvironmentVariableTarget]::Process)
    [Environment]::SetEnvironmentVariable("HIDBRIDGE_EDGE_PROXY_UARTPORT", $UartPort, [EnvironmentVariableTarget]::Process)
    [Environment]::SetEnvironmentVariable("HIDBRIDGE_EDGE_PROXY_UARTBAUD", [string]$UartBaud, [EnvironmentVariableTarget]::Process)
    [Environment]::SetEnvironmentVariable("HIDBRIDGE_EDGE_PROXY_UARTHMACKEY", $UartHmacKey, [EnvironmentVariableTarget]::Process)
    [Environment]::SetEnvironmentVariable("HIDBRIDGE_EDGE_PROXY_UARTRELEASEPORTAFTEREXECUTE", "true", [EnvironmentVariableTarget]::Process)
    [Environment]::SetEnvironmentVariable("HIDBRIDGE_EDGE_PROXY_UARTMOUSEINTERFACESELECTOR", "255", [EnvironmentVariableTarget]::Process)
    [Environment]::SetEnvironmentVariable("HIDBRIDGE_EDGE_PROXY_UARTKEYBOARDINTERFACESELECTOR", "254", [EnvironmentVariableTarget]::Process)

    if ([string]::Equals($CommandExecutor, "uart", [StringComparison]::OrdinalIgnoreCase)) {
        [Environment]::SetEnvironmentVariable("HIDBRIDGE_EDGE_PROXY_MEDIAHEALTHURL", "", [EnvironmentVariableTarget]::Process)
        [Environment]::SetEnvironmentVariable("HIDBRIDGE_EDGE_PROXY_ASSUMEMEDIAREADYWITHOUTPROBE", "true", [EnvironmentVariableTarget]::Process)
    }
    else {
        $controlUri = [Uri]$ControlWsUrl
        $controlHealthUrl = if ([string]::Equals($controlUri.Scheme, "wss", [StringComparison]::OrdinalIgnoreCase)) {
            "https://$($controlUri.Host):$($controlUri.Port)/health"
        }
        else {
            "http://$($controlUri.Host):$($controlUri.Port)/health"
        }
        [Environment]::SetEnvironmentVariable("HIDBRIDGE_EDGE_PROXY_MEDIAHEALTHURL", $controlHealthUrl, [EnvironmentVariableTarget]::Process)
        [Environment]::SetEnvironmentVariable("HIDBRIDGE_EDGE_PROXY_ASSUMEMEDIAREADYWITHOUTPROBE", "false", [EnvironmentVariableTarget]::Process)
    }

    $adapterArgs = @(
        "run",
        "--project", $edgeAgentProject,
        "-c", "Debug"
    )
    $adapterProcess = Start-Process -FilePath $dotnet.Source -ArgumentList $adapterArgs -WorkingDirectory $repoRoot -PassThru -RedirectStandardOutput $adapterStdout -RedirectStandardError $adapterStderr
}
finally {
    foreach ($name in $edgeEnvRestore.Keys) {
        [Environment]::SetEnvironmentVariable($name, $edgeEnvRestore[$name], [EnvironmentVariableTarget]::Process)
    }
}

Start-Sleep -Milliseconds 500
$adapterProcess.Refresh()
if ($adapterProcess.HasExited) {
    $stderrTail = ""
    try {
        $stderrTail = (Get-Content -Path $adapterStderr -Tail 40 -ErrorAction Stop) -join [Environment]::NewLine
    }
    catch {
        $stderrTail = ""
    }

    if ([string]::IsNullOrWhiteSpace($stderrTail)) {
        throw "Edge proxy agent exited early with code $($adapterProcess.ExitCode). See: $adapterStderr"
    }

    throw "Edge proxy agent exited early with code $($adapterProcess.ExitCode).`n$stderrTail"
}

$summary = [pscustomobject]@{
    apiBaseUrl = $ApiBaseUrl
    webBaseUrl = $WebBaseUrl
    commandExecutor = $CommandExecutor
    controlWsUrl = $ControlWsUrl
    runtimeBootstrapSkipped = [bool]$SkipRuntimeBootstrap
    sessionId = $session
    peerId = $peerId
    exp022Pid = if ($null -ne $exp022Process) { $exp022Process.Id } else { $null }
    adapterPid = $adapterProcess.Id
    sessionEnvPath = $sessionEnvPath
    exp022Stdout = $exp022Stdout
    exp022Stderr = $exp022Stderr
    adapterStdout = $adapterStdout
    adapterStderr = $adapterStderr
}

$summary | ConvertTo-Json -Depth 6 | Set-Content -Path $stackSummaryPath -Encoding utf8
if (-not [string]::IsNullOrWhiteSpace($OutputJsonPath)) {
    $resolvedOutputPath = if ([System.IO.Path]::IsPathRooted($OutputJsonPath)) { $OutputJsonPath } else { Join-Path $platformRoot $OutputJsonPath }
    $resolvedOutputDir = Split-Path -Parent $resolvedOutputPath
    if (-not [string]::IsNullOrWhiteSpace($resolvedOutputDir) -and -not (Test-Path -Path $resolvedOutputDir)) {
        New-Item -ItemType Directory -Force -Path $resolvedOutputDir | Out-Null
    }

    $summary | ConvertTo-Json -Depth 6 | Set-Content -Path $resolvedOutputPath -Encoding utf8
}

Write-Host ""
Write-Host "=== WebRTC Stack Summary ==="
Write-Host "API:            $ApiBaseUrl"
Write-Host "Web:            $WebBaseUrl"
Write-Host "Executor:       $CommandExecutor"
if ($null -ne $exp022Process) {
    Write-Host "Control WS:     $ControlWsUrl"
}
Write-Host "Session:        $session"
Write-Host "Peer:           $peerId"
Write-Host "Adapter PID:    $($adapterProcess.Id)"
Write-Host "Session env:    $sessionEnvPath"
Write-Host "Summary JSON:   $stackSummaryPath"
if ($null -ne $exp022Process) {
    Write-Host "exp-022 stdout: $exp022Stdout"
    Write-Host "exp-022 stderr: $exp022Stderr"
}
Write-Host "adapter stdout: $adapterStdout"
Write-Host "adapter stderr: $adapterStderr"
Write-Host ""
Write-Host "Stop commands:"
Write-Host "- Stop-Process -Id $($adapterProcess.Id) -Force"
if ($null -ne $exp022Process) {
    Write-Host "- Stop-Process -Id $($exp022Process.Id) -Force"
}
Write-Host "- Stop runtime via demo-flow output (API/Web PIDs)."

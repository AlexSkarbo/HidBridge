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

# Stops process instances whose command line contains at least one requested token.
function Stop-MatchingProcesses {
    param([string]$Name, [string[]]$Needle)

    $processes = @(Get-CimInstance Win32_Process -Filter "Name='$Name'" -ErrorAction SilentlyContinue)
    $currentPid = $PID
    foreach ($proc in $processes) {
        if ($proc.ProcessId -eq $currentPid) {
            continue
        }

        $line = [string]$proc.CommandLine
        if ([string]::IsNullOrWhiteSpace($line)) {
            continue
        }

        $match = $false
        foreach ($item in $Needle) {
            if ($line -like "*$item*") {
                $match = $true
                break
            }
        }

        if (-not $match) {
            continue
        }

        try {
            Stop-Process -Id $proc.ProcessId -Force -ErrorAction Stop
            Write-Host "Stopped PID $($proc.ProcessId): $Name"
        }
        catch {
            Write-Warning "Failed to stop PID $($proc.ProcessId): $($_.Exception.Message)"
        }
    }
}

# Converts one control websocket URL to health endpoint URL.
function Get-ControlHealthUrl {
    param([string]$Url)

    if ([string]::IsNullOrWhiteSpace($Url)) {
        return ""
    }

    try {
        $uri = [Uri]$Url
        $scheme = if ([string]::Equals($uri.Scheme, "wss", [StringComparison]::OrdinalIgnoreCase)) { "https" } else { "http" }
        return "${scheme}://$($uri.Host):$($uri.Port)/health"
    }
    catch {
        return ""
    }
}

# Returns true when API /health responds with status=ok.
function Test-ApiHealth {
    param([string]$BaseUrl)

    try {
        $response = Invoke-RestMethod -Method Get -Uri "$($BaseUrl.TrimEnd('/'))/health" -TimeoutSec 5
        return ($null -ne $response -and [string]::Equals([string]$response.status, "ok", [StringComparison]::OrdinalIgnoreCase))
    }
    catch {
        return $false
    }
}

# Returns true when Web base URL responds successfully.
function Test-WebHealth {
    param([string]$BaseUrl)

    try {
        $response = Invoke-WebRequest -Method Get -Uri $BaseUrl -UseBasicParsing -TimeoutSec 5
        return ($null -ne $response -and $response.StatusCode -ge 200 -and $response.StatusCode -lt 500)
    }
    catch {
        return $false
    }
}

# Reads API runtime authentication authority when available.
function Resolve-ApiAuthAuthority {
    param([string]$BaseUrl)

    try {
        $runtime = Invoke-RestMethod -Method Get -Uri "$($BaseUrl.TrimEnd('/'))/api/v1/runtime/uart" -TimeoutSec 5
        if ($runtime.PSObject.Properties["authentication"] -and $null -ne $runtime.authentication) {
            $authority = [string]$runtime.authentication.authority
            if (-not [string]::IsNullOrWhiteSpace($authority)) {
                return $authority.TrimEnd('/')
            }
        }
    }
    catch {
    }

    return ""
}

# Returns true when Keycloak realm endpoint responds with matching realm.
function Test-KeycloakRealmHealth {
    param(
        [string]$BaseUrl,
        [string]$Realm
    )

    if ([string]::IsNullOrWhiteSpace($BaseUrl) -or [string]::IsNullOrWhiteSpace($Realm)) {
        return $false
    }

    try {
        $response = Invoke-RestMethod -Method Get -Uri "$($BaseUrl.TrimEnd('/'))/realms/$Realm" -TimeoutSec 3
        return [string]::Equals([string]$response.realm, $Realm, [StringComparison]::OrdinalIgnoreCase)
    }
    catch {
        return $false
    }
}

# Extracts realm name from authority path like /realms/{realm}.
function Get-RealmNameFromAuthorityPath {
    param([string]$Path)

    if ([string]::IsNullOrWhiteSpace($Path)) {
        return ""
    }

    $segments = $Path.Trim('/').Split('/', [System.StringSplitOptions]::RemoveEmptyEntries)
    for ($i = 0; $i -lt $segments.Length - 1; $i++) {
        if ([string]::Equals($segments[$i], "realms", [StringComparison]::OrdinalIgnoreCase)) {
            return [string]$segments[$i + 1]
        }
    }

    return ""
}

if ($PSBoundParameters.ContainsKey("PeerReadyTimeoutSec")) {
    Write-Warning "PeerReadyTimeoutSec is deprecated for webrtc-stack and ignored. Readiness checks run in smoke/acceptance via API policy."
}
if ($PSBoundParameters.ContainsKey("TokenScope") -and -not [string]::IsNullOrWhiteSpace($TokenScope)) {
    Write-Warning "TokenScope is deprecated for webrtc-stack and ignored. Edge agent token scope is runtime-managed."
}
if ($PSBoundParameters.ContainsKey("AdapterDurationSec")) {
    Write-Warning "AdapterDurationSec is deprecated for webrtc-stack and ignored. Stop stack processes explicitly."
}

if ([string]::Equals($CommandExecutor, "controlws", [StringComparison]::OrdinalIgnoreCase)) {
    if (-not $AllowLegacyControlWs) {
        throw "CommandExecutor 'controlws' is legacy compatibility mode. Use 'uart' for production path, or pass -AllowLegacyControlWs explicitly."
    }

    Assert-LegacyExp022Enabled -Context "run_webrtc_stack.ps1 controlws mode"
    Write-Warning "Legacy controlws executor enabled for webrtc-stack (exp-022 compatibility mode)."
}

$effectiveSkipRuntimeBootstrap = $SkipRuntimeBootstrap
if (-not $effectiveSkipRuntimeBootstrap) {
    $apiHealthy = Test-ApiHealth -BaseUrl $ApiBaseUrl
    $webHealthy = Test-WebHealth -BaseUrl $WebBaseUrl
    if ($apiHealthy -and $webHealthy) {
        Write-Warning "Detected healthy runtime at API/Web endpoints. Skipping runtime bootstrap and reusing running services."
        $effectiveSkipRuntimeBootstrap = $true
    }
}

if ($StopExisting) {
    $dotnetNeedles = @(
        "exp-022-datachanneldotnet",
        "HidBridge.EdgeProxy.Agent",
        "run_webrtc_stack.ps1"
    )
    if (-not $effectiveSkipRuntimeBootstrap) {
        $dotnetNeedles = @(
            "HidBridge.ControlPlane.Api",
            "HidBridge.ControlPlane.Web"
        ) + $dotnetNeedles
    }

    Stop-MatchingProcesses -Name "dotnet.exe" -Needle $dotnetNeedles
    Stop-MatchingProcesses -Name "pwsh.exe" -Needle @(
        "exp-022-datachanneldotnet",
        "HidBridge.EdgeProxy.Agent",
        "run_webrtc_stack.ps1"
    )
    Stop-MatchingProcesses -Name "powershell.exe" -Needle @(
        "exp-022-datachanneldotnet",
        "HidBridge.EdgeProxy.Agent",
        "run_webrtc_stack.ps1"
    )
}

if (-not $effectiveSkipRuntimeBootstrap) {
    $demoFlowParameters = @{
        ApiBaseUrl = $ApiBaseUrl
        WebBaseUrl = $WebBaseUrl
        SkipDoctor = $true
        SkipDemoGate = $true
    }
    if ($SkipIdentityReset) { $demoFlowParameters.SkipIdentityReset = $true }
    if ($SkipCiLocal) { $demoFlowParameters.SkipCiLocal = $true }

    $demoFlowRestore = @{
        HIDBRIDGE_UART_PASSIVE_HEALTH_MODE = [Environment]::GetEnvironmentVariable("HIDBRIDGE_UART_PASSIVE_HEALTH_MODE", [EnvironmentVariableTarget]::Process)
        HIDBRIDGE_UART_RELEASE_PORT_AFTER_EXECUTE = [Environment]::GetEnvironmentVariable("HIDBRIDGE_UART_RELEASE_PORT_AFTER_EXECUTE", [EnvironmentVariableTarget]::Process)
        HIDBRIDGE_UART_PORT = [Environment]::GetEnvironmentVariable("HIDBRIDGE_UART_PORT", [EnvironmentVariableTarget]::Process)
        HIDBRIDGE_TRANSPORT_PROVIDER = [Environment]::GetEnvironmentVariable("HIDBRIDGE_TRANSPORT_PROVIDER", [EnvironmentVariableTarget]::Process)
        HIDBRIDGE_TRANSPORT_FALLBACK_TO_DEFAULT_ON_WEBRTC_ERROR = [Environment]::GetEnvironmentVariable("HIDBRIDGE_TRANSPORT_FALLBACK_TO_DEFAULT_ON_WEBRTC_ERROR", [EnvironmentVariableTarget]::Process)
    }

    try {
        [Environment]::SetEnvironmentVariable("HIDBRIDGE_UART_PASSIVE_HEALTH_MODE", "true", [EnvironmentVariableTarget]::Process)
        [Environment]::SetEnvironmentVariable("HIDBRIDGE_UART_RELEASE_PORT_AFTER_EXECUTE", "true", [EnvironmentVariableTarget]::Process)
        [Environment]::SetEnvironmentVariable("HIDBRIDGE_UART_PORT", "COM255", [EnvironmentVariableTarget]::Process)
        [Environment]::SetEnvironmentVariable("HIDBRIDGE_TRANSPORT_PROVIDER", "webrtc-datachannel", [EnvironmentVariableTarget]::Process)
        [Environment]::SetEnvironmentVariable("HIDBRIDGE_TRANSPORT_FALLBACK_TO_DEFAULT_ON_WEBRTC_ERROR", "false", [EnvironmentVariableTarget]::Process)

        & $demoFlowScript @demoFlowParameters
        if ($LASTEXITCODE -ne 0) {
            throw "demo-flow failed with exit code $LASTEXITCODE."
        }
    }
    finally {
        foreach ($name in $demoFlowRestore.Keys) {
            [Environment]::SetEnvironmentVariable($name, $demoFlowRestore[$name], [EnvironmentVariableTarget]::Process)
        }
    }
}

$controlHealthUrl = if ([string]::Equals($CommandExecutor, "controlws", [StringComparison]::OrdinalIgnoreCase)) {
    Get-ControlHealthUrl -Url $ControlWsUrl
}
else {
    ""
}

@"
SESSION_ID=$session
PEER_ID=$peerId
ENDPOINT_ID=$EndpointId
PRINCIPAL_ID=$PrincipalId
TENANT_ID=local-tenant
ORGANIZATION_ID=local-org
COMMAND_EXECUTOR=$CommandExecutor
CONTROL_HEALTH_URL=$controlHealthUrl
"@ | Set-Content $sessionEnvPath -Encoding ascii

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
if ([string]::Equals($CommandExecutor, "controlws", [StringComparison]::OrdinalIgnoreCase)) {
    $wsUri = [Uri]$ControlWsUrl
    $wsBind = if ($wsUri.Host -eq "localhost") { "127.0.0.1" } else { $wsUri.Host }
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
    HIDBRIDGE_EDGE_PROXY_REQUIREMEDIAREADY = [Environment]::GetEnvironmentVariable("HIDBRIDGE_EDGE_PROXY_REQUIREMEDIAREADY", [EnvironmentVariableTarget]::Process)
    HIDBRIDGE_EDGE_PROXY_ASSUMEMEDIAREADYWITHOUTPROBE = [Environment]::GetEnvironmentVariable("HIDBRIDGE_EDGE_PROXY_ASSUMEMEDIAREADYWITHOUTPROBE", [EnvironmentVariableTarget]::Process)
}

$edgeProxyKeycloakBaseUrl = "http://127.0.0.1:18096"
$edgeProxyKeycloakRealm = "hidbridge-dev"
$keycloakBaseCandidates = [System.Collections.Generic.List[string]]::new()
$apiAuthAuthority = Resolve-ApiAuthAuthority -BaseUrl $ApiBaseUrl
if (-not [string]::IsNullOrWhiteSpace($apiAuthAuthority)) {
    $authUri = $null
    if ([Uri]::TryCreate($apiAuthAuthority, [UriKind]::Absolute, [ref]$authUri)) {
        $authorityBase = "$($authUri.Scheme)://$($authUri.Host):$($authUri.Port)"
        $keycloakBaseCandidates.Add($authorityBase) | Out-Null
        $realmFromAuthority = Get-RealmNameFromAuthorityPath -Path $authUri.AbsolutePath
        if (-not [string]::IsNullOrWhiteSpace($realmFromAuthority)) {
            $edgeProxyKeycloakRealm = $realmFromAuthority
        }
    }
}

if ($effectiveSkipRuntimeBootstrap -and $keycloakBaseCandidates -notcontains "http://host.docker.internal:18096") {
    $keycloakBaseCandidates.Add("http://host.docker.internal:18096") | Out-Null
}
foreach ($candidate in @("http://127.0.0.1:18096", "http://localhost:18096")) {
    if ($keycloakBaseCandidates -notcontains $candidate) {
        $keycloakBaseCandidates.Add($candidate) | Out-Null
    }
}

foreach ($candidate in $keycloakBaseCandidates) {
    if (Test-KeycloakRealmHealth -BaseUrl $candidate -Realm $edgeProxyKeycloakRealm) {
        $edgeProxyKeycloakBaseUrl = $candidate
        break
    }
}

[Environment]::SetEnvironmentVariable("HIDBRIDGE_EDGE_PROXY_BASEURL", $ApiBaseUrl, [EnvironmentVariableTarget]::Process)
[Environment]::SetEnvironmentVariable("HIDBRIDGE_EDGE_PROXY_SESSIONID", $session, [EnvironmentVariableTarget]::Process)
[Environment]::SetEnvironmentVariable("HIDBRIDGE_EDGE_PROXY_PEERID", $peerId, [EnvironmentVariableTarget]::Process)
[Environment]::SetEnvironmentVariable("HIDBRIDGE_EDGE_PROXY_ENDPOINTID", $EndpointId, [EnvironmentVariableTarget]::Process)
[Environment]::SetEnvironmentVariable("HIDBRIDGE_EDGE_PROXY_CONTROLWSURL", $ControlWsUrl, [EnvironmentVariableTarget]::Process)
[Environment]::SetEnvironmentVariable("HIDBRIDGE_EDGE_PROXY_PRINCIPALID", $PrincipalId, [EnvironmentVariableTarget]::Process)
[Environment]::SetEnvironmentVariable("HIDBRIDGE_EDGE_PROXY_TOKENUSERNAME", $TokenUsername, [EnvironmentVariableTarget]::Process)
[Environment]::SetEnvironmentVariable("HIDBRIDGE_EDGE_PROXY_TOKENPASSWORD", $TokenPassword, [EnvironmentVariableTarget]::Process)
[Environment]::SetEnvironmentVariable("HIDBRIDGE_EDGE_PROXY_KEYCLOAKBASEURL", $edgeProxyKeycloakBaseUrl, [EnvironmentVariableTarget]::Process)
[Environment]::SetEnvironmentVariable("HIDBRIDGE_EDGE_PROXY_KEYCLOAKREALM", $edgeProxyKeycloakRealm, [EnvironmentVariableTarget]::Process)
[Environment]::SetEnvironmentVariable("HIDBRIDGE_EDGE_PROXY_COMMANDEXECUTOR", $CommandExecutor, [EnvironmentVariableTarget]::Process)
[Environment]::SetEnvironmentVariable("HIDBRIDGE_EDGE_PROXY_UARTPORT", $UartPort, [EnvironmentVariableTarget]::Process)
[Environment]::SetEnvironmentVariable("HIDBRIDGE_EDGE_PROXY_UARTBAUD", [string]$UartBaud, [EnvironmentVariableTarget]::Process)
[Environment]::SetEnvironmentVariable("HIDBRIDGE_EDGE_PROXY_UARTHMACKEY", $UartHmacKey, [EnvironmentVariableTarget]::Process)
[Environment]::SetEnvironmentVariable("HIDBRIDGE_EDGE_PROXY_UARTRELEASEPORTAFTEREXECUTE", "true", [EnvironmentVariableTarget]::Process)
[Environment]::SetEnvironmentVariable("HIDBRIDGE_EDGE_PROXY_UARTMOUSEINTERFACESELECTOR", "255", [EnvironmentVariableTarget]::Process)
[Environment]::SetEnvironmentVariable("HIDBRIDGE_EDGE_PROXY_UARTKEYBOARDINTERFACESELECTOR", "254", [EnvironmentVariableTarget]::Process)
if (-not [string]::IsNullOrWhiteSpace($controlHealthUrl)) {
    [Environment]::SetEnvironmentVariable("HIDBRIDGE_EDGE_PROXY_MEDIAHEALTHURL", $controlHealthUrl, [EnvironmentVariableTarget]::Process)
}
elseif ([string]::Equals($CommandExecutor, "uart", [StringComparison]::OrdinalIgnoreCase)) {
    # UART acceptance path can run without capture/runtime media probe.
    [Environment]::SetEnvironmentVariable("HIDBRIDGE_EDGE_PROXY_MEDIAHEALTHURL", "", [EnvironmentVariableTarget]::Process)
    [Environment]::SetEnvironmentVariable("HIDBRIDGE_EDGE_PROXY_ASSUMEMEDIAREADYWITHOUTPROBE", "true", [EnvironmentVariableTarget]::Process)
}

if (-not [string]::Equals($CommandExecutor, "uart", [StringComparison]::OrdinalIgnoreCase)) {
    [Environment]::SetEnvironmentVariable("HIDBRIDGE_EDGE_PROXY_ASSUMEMEDIAREADYWITHOUTPROBE", "false", [EnvironmentVariableTarget]::Process)
}

$adapterArgs = @(
    "run",
    "--project", $edgeAgentProject,
    "-c", "Debug"
)
$adapterProcess = Start-Process -FilePath $dotnet.Source -ArgumentList $adapterArgs -WorkingDirectory $repoRoot -PassThru -RedirectStandardOutput $adapterStdout -RedirectStandardError $adapterStderr
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

foreach ($name in $edgeEnvRestore.Keys) {
    [Environment]::SetEnvironmentVariable($name, $edgeEnvRestore[$name], [EnvironmentVariableTarget]::Process)
}

$summary = [pscustomobject]@{
    apiBaseUrl = $ApiBaseUrl
    webBaseUrl = $WebBaseUrl
    commandExecutor = $CommandExecutor
    controlWsUrl = $ControlWsUrl
    edgeProxyKeycloakBaseUrl = $edgeProxyKeycloakBaseUrl
    edgeProxyKeycloakRealm = $edgeProxyKeycloakRealm
    runtimeBootstrapSkipped = [bool]$effectiveSkipRuntimeBootstrap
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

    # Writes stack summary to caller-specified location for CI/acceptance orchestration.
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
Write-Host "Peer ready:     (delegated to smoke/readiness checks)"
if ($null -ne $exp022Process) {
    Write-Host "exp-022 PID:    $($exp022Process.Id)"
}
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

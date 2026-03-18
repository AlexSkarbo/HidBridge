param(
    [string]$ApiBaseUrl = "http://127.0.0.1:18093",
    [string]$WebBaseUrl = "http://127.0.0.1:18110",
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
$peerId = "peer-local-exp022"
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

# Waits until an HTTP health endpoint returns one of the expected "ok" shapes.
function Wait-HttpHealth {
    param(
        [string]$Uri,
        [int]$Attempts = 40,
        [int]$DelayMs = 500
    )

    for ($i = 0; $i -lt $Attempts; $i++) {
        try {
            $response = Invoke-RestMethod -Method Get -Uri $Uri -TimeoutSec 2
            if ($null -ne $response) {
                if ($response -is [System.Collections.IDictionary] -and $response.Contains("ok")) {
                    if ([bool]$response["ok"]) {
                        return $true
                    }
                }
                elseif ($response -is [System.Collections.IDictionary] -and $response.Contains("status")) {
                    if ([string]$response["status"] -eq "ok") {
                        return $true
                    }
                }
                elseif ($response.PSObject.Properties["ok"] -and [bool]$response.ok) {
                    return $true
                }
                elseif ($response.PSObject.Properties["status"] -and [string]$response.status -eq "ok") {
                    return $true
                }
            }
        }
        catch {
        }

        Start-Sleep -Milliseconds $DelayMs
    }

    return $false
}

# Normalizes collection-style API responses to a plain array.
function Get-CollectionItems {
    param([object]$Response)

    if ($null -eq $Response) { return @() }
    if ($Response -is [System.Collections.IDictionary] -and $Response.Contains("items")) {
        return @($Response["items"])
    }
    if ($Response.PSObject.Properties["items"]) {
        return @($Response.items)
    }
    return @($Response)
}

# Returns the latest tail from a log file for failure diagnostics.
function Get-LogTail {
    param(
        [string]$Path,
        [int]$LineCount = 40
    )

    if (-not (Test-Path -Path $Path -PathType Leaf)) {
        return "<log file not found: $Path>"
    }

    try {
        return ((Get-Content -Path $Path -Tail $LineCount -ErrorAction Stop) -join [Environment]::NewLine)
    }
    catch {
        return "<failed to read log tail: $($_.Exception.Message)>"
    }
}

# Checks whether a spawned process is still running.
function Test-ProcessAlive {
    param([System.Diagnostics.Process]$Process)

    if ($null -eq $Process) {
        return $false
    }

    try {
        return (-not $Process.HasExited)
    }
    catch {
        return $false
    }
}

# Waits until the relay peer is visible as online in API session peer list.
function Wait-RelayPeerOnline {
    param(
        [string]$ApiBaseUrl,
        [string]$SessionId,
        [string]$PeerId,
        [hashtable]$AuthHeaders,
        [int]$TimeoutSec = 30,
        [int]$DelayMs = 500,
        [System.Diagnostics.Process]$AdapterProcess,
        [System.Diagnostics.Process]$Exp022Process,
        [string]$AdapterStdErrPath,
        [string]$Exp022StdErrPath
    )

    $deadlineUtc = [DateTimeOffset]::UtcNow.AddSeconds([Math]::Max(5, $TimeoutSec))
    $peersUrl = "$($ApiBaseUrl.TrimEnd('/'))/api/v1/sessions/$([Uri]::EscapeDataString($SessionId))/transport/webrtc/peers"

    while ([DateTimeOffset]::UtcNow -lt $deadlineUtc) {
        if (-not (Test-ProcessAlive -Process $AdapterProcess)) {
            $adapterTail = Get-LogTail -Path $AdapterStdErrPath
            throw "Edge proxy agent exited before relay peer became online.`nAdapter stderr tail:`n$adapterTail"
        }

        if (-not (Test-ProcessAlive -Process $Exp022Process)) {
            $expTail = Get-LogTail -Path $Exp022StdErrPath
            throw "exp-022 exited before relay peer became online.`nexp-022 stderr tail:`n$expTail"
        }

        try {
            $response = Invoke-RestMethod -Method Get -Uri $peersUrl -Headers $AuthHeaders -TimeoutSec 5
            $peers = @(Get-CollectionItems -Response $response)
            $peer = $peers | Where-Object { [string]$_.peerId -eq $PeerId } | Select-Object -First 1
            if ($null -ne $peer) {
                $isOnline = $false
                if ($peer -is [System.Collections.IDictionary] -and $peer.Contains("isOnline")) {
                    $isOnline = [bool]$peer["isOnline"]
                }
                elseif ($peer.PSObject.Properties["isOnline"]) {
                    $isOnline = [bool]$peer.isOnline
                }

                if ($isOnline) {
                    return $true
                }
            }
        }
        catch {
        }

        Start-Sleep -Milliseconds ([Math]::Max(100, $DelayMs))
    }

    return $false
}

# Obtains bearer token for stack bootstrapping calls.
function Get-OidcAccessToken {
    param(
        [string]$KeycloakBaseUrl,
        [string]$Realm,
        [string]$ClientId,
        [string]$Username,
        [string]$Password
    )

    $tokenResp = Invoke-RestMethod -Method Post `
        -Uri "$($KeycloakBaseUrl.TrimEnd('/'))/realms/$Realm/protocol/openid-connect/token" `
        -ContentType "application/x-www-form-urlencoded" `
        -Body @{
            grant_type = "password"
            client_id = $ClientId
            username = $Username
            password = $Password
        } `
        -TimeoutSec 30

    $token = [string]$tokenResp.access_token
    if ([string]::IsNullOrWhiteSpace($token)) {
        throw "OIDC token response did not contain access_token."
    }

    return $token
}

# Asserts COM port can be opened before runtime startup.
function Assert-UartPortAvailable {
    param([string]$Port, [int]$Baud)

    $serialPortType = $null
    try {
        $serialPortType = [type]"System.IO.Ports.SerialPort"
    }
    catch {
    }

    if ($null -eq $serialPortType) {
        foreach ($assemblyName in @("System.IO.Ports", "System")) {
            try {
                Add-Type -AssemblyName $assemblyName -ErrorAction Stop
                $serialPortType = [type]"System.IO.Ports.SerialPort"
                break
            }
            catch {
            }
        }
    }

    if ($null -eq $serialPortType) {
        Write-Warning "System.IO.Ports.SerialPort type is unavailable in this PowerShell host; skipping COM port probe for $Port."
        return
    }

    $serialPort = [System.IO.Ports.SerialPort]::new($Port, $Baud)
    try {
        $serialPort.Open()
    }
    finally {
        if ($serialPort.IsOpen) {
            $serialPort.Close()
        }

        $serialPort.Dispose()
    }
}

if ($StopExisting) {
    $dotnetNeedles = @(
        "exp-022-datachanneldotnet",
        "HidBridge.EdgeProxy.Agent",
        "run_webrtc_stack.ps1"
    )
    if (-not $SkipRuntimeBootstrap) {
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

Assert-UartPortAvailable -Port $UartPort -Baud $UartBaud

if (-not $SkipRuntimeBootstrap) {
    $demoFlowParameters = @{
        ApiBaseUrl = $ApiBaseUrl
        WebBaseUrl = $WebBaseUrl
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

if (-not (Wait-HttpHealth -Uri "$($ApiBaseUrl.TrimEnd('/'))/health")) {
    throw "API health probe failed at $($ApiBaseUrl.TrimEnd('/'))/health."
}

$accessToken = Get-OidcAccessToken `
    -KeycloakBaseUrl "http://127.0.0.1:18096" `
    -Realm "hidbridge-dev" `
    -ClientId "controlplane-smoke" `
    -Username $TokenUsername `
    -Password $TokenPassword

$authHeaders = @{
    "Authorization" = "Bearer $accessToken"
}

$inventory = Invoke-RestMethod -Method Get -Uri "$($ApiBaseUrl.TrimEnd('/'))/api/v1/dashboards/inventory" -Headers $authHeaders -TimeoutSec 30
$inventoryEndpoints = if ($inventory.PSObject.Properties["endpoints"]) { @($inventory.endpoints) } else { @() }
$selectedEndpoint = $inventoryEndpoints | Where-Object { [string]$_.endpointId -eq $EndpointId } | Select-Object -First 1
if ($null -eq $selectedEndpoint) {
    throw "Endpoint '$EndpointId' was not found in inventory."
}

$targetAgentId = [string]$selectedEndpoint.agentId
if ([string]::IsNullOrWhiteSpace($targetAgentId)) {
    throw "Endpoint '$EndpointId' does not expose agentId in inventory."
}

$null = Invoke-RestMethod -Method Post -Uri "$($ApiBaseUrl.TrimEnd('/'))/api/v1/sessions" -Headers $authHeaders -TimeoutSec 30 -ContentType "application/json" -Body (@{
    sessionId = $session
    profile = "UltraLowLatency"
    requestedBy = $PrincipalId
    targetAgentId = $targetAgentId
    targetEndpointId = $EndpointId
    shareMode = "Owner"
    tenantId = "local-tenant"
    organizationId = "local-org"
    transportProvider = "webrtc-datachannel"
} | ConvertTo-Json -Depth 10)

$wsUri = [Uri]$ControlWsUrl
$wsBind = if ($wsUri.Host -eq "localhost") { "127.0.0.1" } else { $wsUri.Host }

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

if (-not (Wait-HttpHealth -Uri "http://$($wsUri.Host):$($wsUri.Port)/health")) {
    throw "exp-022 control health probe failed at http://$($wsUri.Host):$($wsUri.Port)/health."
}

@"
SESSION_ID=$session
PEER_ID=$peerId
ENDPOINT_ID=$EndpointId
PRINCIPAL_ID=$PrincipalId
TENANT_ID=local-tenant
ORGANIZATION_ID=local-org
"@ | Set-Content $sessionEnvPath -Encoding ascii

$edgeEnvRestore = @{
    HIDBRIDGE_EDGE_PROXY_BASEURL = [Environment]::GetEnvironmentVariable("HIDBRIDGE_EDGE_PROXY_BASEURL", [EnvironmentVariableTarget]::Process)
    HIDBRIDGE_EDGE_PROXY_SESSIONID = [Environment]::GetEnvironmentVariable("HIDBRIDGE_EDGE_PROXY_SESSIONID", [EnvironmentVariableTarget]::Process)
    HIDBRIDGE_EDGE_PROXY_PEERID = [Environment]::GetEnvironmentVariable("HIDBRIDGE_EDGE_PROXY_PEERID", [EnvironmentVariableTarget]::Process)
    HIDBRIDGE_EDGE_PROXY_ENDPOINTID = [Environment]::GetEnvironmentVariable("HIDBRIDGE_EDGE_PROXY_ENDPOINTID", [EnvironmentVariableTarget]::Process)
    HIDBRIDGE_EDGE_PROXY_CONTROLWSURL = [Environment]::GetEnvironmentVariable("HIDBRIDGE_EDGE_PROXY_CONTROLWSURL", [EnvironmentVariableTarget]::Process)
    HIDBRIDGE_EDGE_PROXY_PRINCIPALID = [Environment]::GetEnvironmentVariable("HIDBRIDGE_EDGE_PROXY_PRINCIPALID", [EnvironmentVariableTarget]::Process)
    HIDBRIDGE_EDGE_PROXY_TOKENUSERNAME = [Environment]::GetEnvironmentVariable("HIDBRIDGE_EDGE_PROXY_TOKENUSERNAME", [EnvironmentVariableTarget]::Process)
    HIDBRIDGE_EDGE_PROXY_TOKENPASSWORD = [Environment]::GetEnvironmentVariable("HIDBRIDGE_EDGE_PROXY_TOKENPASSWORD", [EnvironmentVariableTarget]::Process)
    HIDBRIDGE_EDGE_PROXY_TOKENSCOPE = [Environment]::GetEnvironmentVariable("HIDBRIDGE_EDGE_PROXY_TOKENSCOPE", [EnvironmentVariableTarget]::Process)
    HIDBRIDGE_EDGE_PROXY_ACCESSTOKEN = [Environment]::GetEnvironmentVariable("HIDBRIDGE_EDGE_PROXY_ACCESSTOKEN", [EnvironmentVariableTarget]::Process)
}

[Environment]::SetEnvironmentVariable("HIDBRIDGE_EDGE_PROXY_BASEURL", $ApiBaseUrl, [EnvironmentVariableTarget]::Process)
[Environment]::SetEnvironmentVariable("HIDBRIDGE_EDGE_PROXY_SESSIONID", $session, [EnvironmentVariableTarget]::Process)
[Environment]::SetEnvironmentVariable("HIDBRIDGE_EDGE_PROXY_PEERID", $peerId, [EnvironmentVariableTarget]::Process)
[Environment]::SetEnvironmentVariable("HIDBRIDGE_EDGE_PROXY_ENDPOINTID", $EndpointId, [EnvironmentVariableTarget]::Process)
[Environment]::SetEnvironmentVariable("HIDBRIDGE_EDGE_PROXY_CONTROLWSURL", $ControlWsUrl, [EnvironmentVariableTarget]::Process)
[Environment]::SetEnvironmentVariable("HIDBRIDGE_EDGE_PROXY_PRINCIPALID", $PrincipalId, [EnvironmentVariableTarget]::Process)
[Environment]::SetEnvironmentVariable("HIDBRIDGE_EDGE_PROXY_TOKENUSERNAME", $TokenUsername, [EnvironmentVariableTarget]::Process)
[Environment]::SetEnvironmentVariable("HIDBRIDGE_EDGE_PROXY_TOKENPASSWORD", $TokenPassword, [EnvironmentVariableTarget]::Process)
[Environment]::SetEnvironmentVariable("HIDBRIDGE_EDGE_PROXY_ACCESSTOKEN", $accessToken, [EnvironmentVariableTarget]::Process)
if (-not [string]::IsNullOrWhiteSpace($TokenScope)) {
    [Environment]::SetEnvironmentVariable("HIDBRIDGE_EDGE_PROXY_TOKENSCOPE", $TokenScope, [EnvironmentVariableTarget]::Process)
}

$adapterArgs = @(
    "run",
    "--project", $edgeAgentProject,
    "-c", "Debug"
)
$adapterProcess = Start-Process -FilePath $dotnet.Source -ArgumentList $adapterArgs -WorkingDirectory $repoRoot -PassThru -RedirectStandardOutput $adapterStdout -RedirectStandardError $adapterStderr

foreach ($name in $edgeEnvRestore.Keys) {
    [Environment]::SetEnvironmentVariable($name, $edgeEnvRestore[$name], [EnvironmentVariableTarget]::Process)
}

$peerReady = Wait-RelayPeerOnline `
    -ApiBaseUrl $ApiBaseUrl `
    -SessionId $session `
    -PeerId $peerId `
    -AuthHeaders $authHeaders `
    -TimeoutSec $PeerReadyTimeoutSec `
    -DelayMs 500 `
    -AdapterProcess $adapterProcess `
    -Exp022Process $exp022Process `
    -AdapterStdErrPath $adapterStderr `
    -Exp022StdErrPath $exp022Stderr

if (-not $peerReady) {
    $adapterTail = Get-LogTail -Path $adapterStderr
    $expTail = Get-LogTail -Path $exp022Stderr
    throw "WebRTC relay peer '$peerId' did not become online within $PeerReadyTimeoutSec seconds.`nAdapter stderr tail:`n$adapterTail`n`nexp-022 stderr tail:`n$expTail"
}

$summary = [pscustomobject]@{
    apiBaseUrl = $ApiBaseUrl
    webBaseUrl = $WebBaseUrl
    controlWsUrl = $ControlWsUrl
    runtimeBootstrapSkipped = [bool]$SkipRuntimeBootstrap
    sessionId = $session
    peerId = $peerId
    peerReady = $peerReady
    peerReadyTimeoutSec = $PeerReadyTimeoutSec
    exp022Pid = $exp022Process.Id
    adapterPid = $adapterProcess.Id
    sessionEnvPath = $sessionEnvPath
    exp022Stdout = $exp022Stdout
    exp022Stderr = $exp022Stderr
    adapterStdout = $adapterStdout
    adapterStderr = $adapterStderr
}
$summary | ConvertTo-Json -Depth 6 | Set-Content -Path $stackSummaryPath -Encoding utf8

Write-Host ""
Write-Host "=== WebRTC Stack Summary ==="
Write-Host "API:            $ApiBaseUrl"
Write-Host "Web:            $WebBaseUrl"
Write-Host "Control WS:     $ControlWsUrl"
Write-Host "Session:        $session"
Write-Host "Peer:           $peerId"
Write-Host "Peer ready:     $peerReady"
Write-Host "exp-022 PID:    $($exp022Process.Id)"
Write-Host "Adapter PID:    $($adapterProcess.Id)"
Write-Host "Session env:    $sessionEnvPath"
Write-Host "Summary JSON:   $stackSummaryPath"
Write-Host "exp-022 stdout: $exp022Stdout"
Write-Host "exp-022 stderr: $exp022Stderr"
Write-Host "adapter stdout: $adapterStdout"
Write-Host "adapter stderr: $adapterStderr"
Write-Host ""
Write-Host "Stop commands:"
Write-Host "- Stop-Process -Id $($adapterProcess.Id) -Force"
Write-Host "- Stop-Process -Id $($exp022Process.Id) -Force"
Write-Host "- Stop runtime via demo-flow output (API/Web PIDs)."

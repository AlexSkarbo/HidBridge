param(
    [Alias("BaseUrl")]
    [string]$ApiBaseUrl = "http://127.0.0.1:18093",
    [string]$KeycloakBaseUrl = "http://127.0.0.1:18096",
    [string]$RealmName = "hidbridge-dev",
    [string]$ControlHealthUrl = "http://127.0.0.1:28092/health",
    [int]$RequestTimeoutSec = 15,
    [int]$ControlHealthAttempts = 20,
    [int]$ControlHealthDelayMs = 500,
    [string]$TokenClientId = "controlplane-smoke",
    [string]$TokenUsername = "operator.smoke.admin",
    [string]$TokenPassword = "ChangeMe123!",
    [string]$PrincipalId = "smoke-runner",
    [string]$TenantId = "local-tenant",
    [string]$OrganizationId = "local-org",
    [string]$CommandAction = "keyboard.text",
    [string]$CommandText = "",
    [int]$TimeoutMs = 8000,
    [switch]$SkipControlLeaseRequest,
    [int]$LeaseSeconds = 120,
    [string]$OutputJsonPath = "Platform/.logs/webrtc-edge-agent-smoke.result.json"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$scriptsRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$platformRoot = Split-Path -Parent $scriptsRoot
$sessionEnvPath = Join-Path $platformRoot ".logs/webrtc-peer-adapter.session.env"

function Wait-ControlHealth {
    param(
        [string]$Url,
        [int]$Attempts,
        [int]$DelayMs
    )

    for ($i = 0; $i -lt $Attempts; $i++) {
        try {
            $response = Invoke-RestMethod -Method Get -Uri $Url -TimeoutSec 2
            if (($response.PSObject.Properties["ok"] -and [bool]$response.ok) -or ($response -is [System.Collections.IDictionary] -and $response.Contains("ok") -and [bool]$response["ok"])) {
                return $true
            }
        }
        catch {
        }

        Start-Sleep -Milliseconds $DelayMs
    }

    return $false
}

if (-not (Test-Path $sessionEnvPath)) {
    throw "Session env file was not found: $sessionEnvPath. Start webrtc-stack first."
}

if (-not (Wait-ControlHealth -Url $ControlHealthUrl -Attempts ([Math]::Max(1, $ControlHealthAttempts)) -DelayMs ([Math]::Max(100, $ControlHealthDelayMs)))) {
    throw "Control WS health endpoint is not ready: $ControlHealthUrl"
}

$kv = @{}
Get-Content $sessionEnvPath | ForEach-Object {
    if ($_ -match "=") {
        $parts = $_.Split("=", 2)
        $kv[$parts[0]] = $parts[1]
    }
}

$sessionId = [string]$kv["SESSION_ID"]
$peerId = [string]$kv["PEER_ID"]
$sessionEndpointId = [string]$kv["ENDPOINT_ID"]
$sessionPrincipalId = [string]$kv["PRINCIPAL_ID"]
$sessionTenantId = [string]$kv["TENANT_ID"]
$sessionOrganizationId = [string]$kv["ORGANIZATION_ID"]
if ([string]::IsNullOrWhiteSpace($sessionId) -or [string]::IsNullOrWhiteSpace($peerId)) {
    throw "SESSION_ID/PEER_ID not found in $sessionEnvPath"
}

$effectivePrincipalId = if (-not [string]::IsNullOrWhiteSpace($sessionPrincipalId)) { $sessionPrincipalId } else { $PrincipalId }
$effectiveTenantId = if (-not [string]::IsNullOrWhiteSpace($sessionTenantId)) { $sessionTenantId } else { $TenantId }
$effectiveOrganizationId = if (-not [string]::IsNullOrWhiteSpace($sessionOrganizationId)) { $sessionOrganizationId } else { $OrganizationId }

$tokenEndpoint = "$($KeycloakBaseUrl.TrimEnd('/'))/realms/$RealmName/protocol/openid-connect/token"
$tokenResponse = Invoke-RestMethod -Method Post -Uri $tokenEndpoint -TimeoutSec ([Math]::Max(1, $RequestTimeoutSec)) -ContentType "application/x-www-form-urlencoded" -Body @{
    grant_type = "password"
    client_id = $TokenClientId
    username = $TokenUsername
    password = $TokenPassword
}

$accessToken = [string]$tokenResponse.access_token
if ([string]::IsNullOrWhiteSpace($accessToken)) {
    throw "Token response did not contain access_token."
}

$authHeaders = @{
    Authorization = "Bearer $accessToken"
    "X-HidBridge-UserId" = $effectivePrincipalId
    "X-HidBridge-PrincipalId" = $effectivePrincipalId
    "X-HidBridge-TenantId" = $effectiveTenantId
    "X-HidBridge-OrganizationId" = $effectiveOrganizationId
    "X-HidBridge-Role" = "operator.admin,operator.moderator,operator.viewer"
}

$onlineBody = @{
    metadata = @{
        adapter = "webrtc-edge-agent-smoke"
        state = "Connected"
        principalId = $effectivePrincipalId
    }
}
if (-not [string]::IsNullOrWhiteSpace($sessionEndpointId)) {
    $onlineBody["endpointId"] = $sessionEndpointId
}
$onlineUri = "$($ApiBaseUrl.TrimEnd('/'))/api/v1/sessions/$([Uri]::EscapeDataString($sessionId))/transport/webrtc/peers/$([Uri]::EscapeDataString($peerId))/online"
try {
    $null = Invoke-RestMethod -Method Post -Uri $onlineUri -Headers $authHeaders -TimeoutSec ([Math]::Max(1, $RequestTimeoutSec)) -ContentType "application/json" -Body ($onlineBody | ConvertTo-Json -Depth 10)
}
catch {
    $message = $_.Exception.Message
    if ($message -like "*404*" -or $message -like "*Не найден*") {
        Write-Warning "Peer online endpoint is unavailable for session '$sessionId'. Continuing without explicit online mark."
    }
    else {
        throw
    }
}

if (-not $SkipControlLeaseRequest) {
    $leaseBody = @{
        participantId = "owner:$effectivePrincipalId"
        requestedBy = $effectivePrincipalId
        leaseSeconds = [Math]::Max(30, $LeaseSeconds)
        reason = "webrtc edge-agent smoke"
        tenantId = $effectiveTenantId
        organizationId = $effectiveOrganizationId
    }

    $leaseUri = "$($ApiBaseUrl.TrimEnd('/'))/api/v1/sessions/$([Uri]::EscapeDataString($sessionId))/control/request"
    try {
        $null = Invoke-RestMethod -Method Post -Uri $leaseUri -Headers $authHeaders -TimeoutSec ([Math]::Max(1, $RequestTimeoutSec)) -ContentType "application/json" -Body ($leaseBody | ConvertTo-Json -Depth 10)
    }
    catch {
        $message = $_.Exception.Message
        if ($message -like "*404*" -or $message -like "*Не найден*") {
            Write-Warning "Control request endpoint is unavailable for session '$sessionId'. Continuing without explicit lease."
        }
        else {
            throw
        }
    }
}

$commandId = "cmd-webrtc-smoke-" + [DateTimeOffset]::UtcNow.ToString("HHmmss")
$effectiveText = if ([string]::IsNullOrWhiteSpace($CommandText)) { "hello webrtc $([DateTimeOffset]::UtcNow.ToString('HH:mm:ss'))" } else { $CommandText }
$commandBody = @{
    commandId = $commandId
    sessionId = $sessionId
    channel = "Hid"
    action = $CommandAction
    args = @{
        text = $effectiveText
        transportProvider = "webrtc-datachannel"
        recipientPeerId = $peerId
        participantId = "owner:$effectivePrincipalId"
        principalId = $effectivePrincipalId
    }
    timeoutMs = [Math]::Max(1000, $TimeoutMs)
    idempotencyKey = "idem-$commandId"
    tenantId = $effectiveTenantId
    organizationId = $effectiveOrganizationId
}

try {
    $commandResult = Invoke-RestMethod -Method Post -Uri "$($ApiBaseUrl.TrimEnd('/'))/api/v1/sessions/$([Uri]::EscapeDataString($sessionId))/commands" -Headers $authHeaders -TimeoutSec ([Math]::Max(1, $RequestTimeoutSec)) -ContentType "application/json" -Body ($commandBody | ConvertTo-Json -Depth 20)
}
catch {
    $message = $_.Exception.Message
    if ($message -like "*404*" -or $message -like "*Не найден*") {
        throw "WebRTC edge-agent smoke command returned 404 for session '$sessionId'. Re-run webrtc-stack and retry."
    }

    throw
}

$status = [string]$commandResult.status
$summary = [pscustomobject]@{
    apiBaseUrl = $ApiBaseUrl
    controlHealthUrl = $ControlHealthUrl
    sessionId = $sessionId
    peerId = $peerId
    principalId = $effectivePrincipalId
    commandId = $commandId
    commandAction = $CommandAction
    commandStatus = $status
    commandResponse = $commandResult
}

if (-not [string]::IsNullOrWhiteSpace($OutputJsonPath)) {
    $resultPath = if ([System.IO.Path]::IsPathRooted($OutputJsonPath)) { $OutputJsonPath } else { Join-Path $platformRoot $OutputJsonPath }
    $resultDir = Split-Path -Parent $resultPath
    if (-not [string]::IsNullOrWhiteSpace($resultDir) -and -not (Test-Path $resultDir)) {
        New-Item -ItemType Directory -Force -Path $resultDir | Out-Null
    }

    $summary | ConvertTo-Json -Depth 20 | Set-Content -Path $resultPath -Encoding utf8
}

Write-Host ""
Write-Host "=== WebRTC Edge Agent Smoke ==="
Write-Host "Session:  $sessionId"
Write-Host "Peer:     $peerId"
Write-Host "Command:  $commandId"
Write-Host "Result:   $status"
if (-not [string]::IsNullOrWhiteSpace($OutputJsonPath)) {
    Write-Host "Summary:  $OutputJsonPath"
}

if (-not [string]::Equals($status, "Applied", [StringComparison]::OrdinalIgnoreCase)) {
    throw "WebRTC edge-agent smoke failed: command status is '$status' (expected 'Applied')."
}

Write-Host "PASS"

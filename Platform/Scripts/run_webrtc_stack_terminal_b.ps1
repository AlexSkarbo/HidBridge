param(
    [Alias("BaseUrl")]
    [string]$ApiBaseUrl = "http://127.0.0.1:18093",
    [string]$KeycloakBaseUrl = "http://127.0.0.1:18096",
    [string]$RealmName = "hidbridge-dev",
    [string]$ControlHealthUrl = "http://127.0.0.1:28092/health",
    [int]$ControlHealthAttempts = 30,
    [int]$ControlHealthDelayMs = 500,
    [int]$RequestTimeoutSec = 15,
    [string]$TokenClientId = "controlplane-smoke",
    [string]$TokenUsername = "operator.smoke.admin",
    [string]$TokenPassword = "ChangeMe123!",
    [string]$PrincipalId = "smoke-runner",
    [string]$TenantId = "local-tenant",
    [string]$OrganizationId = "local-org",
    [int]$LeaseSeconds = 120,
    [string]$CommandAction = "keyboard.text",
    [string]$CommandText = "",
    [int]$TimeoutMs = 8000,
    [switch]$SkipTransportHealthCheck,
    [string]$OutputJsonPath = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$scriptsRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$platformRoot = Split-Path -Parent $scriptsRoot

function Wait-ControlHealth {
    param([string]$Url, [int]$Attempts, [int]$DelayMs)

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

function Get-MetricAsInt {
    param([object]$Metrics, [string]$Name)

    if ($null -eq $Metrics -or [string]::IsNullOrWhiteSpace($Name)) {
        return 0
    }

    $value = $null
    if ($Metrics -is [System.Collections.IDictionary]) {
        if ($Metrics.Contains($Name)) {
            $value = $Metrics[$Name]
        }
    }
    elseif ($Metrics.PSObject.Properties[$Name]) {
        $value = $Metrics.$Name
    }

    if ($null -eq $value) {
        return 0
    }

    $parsed = 0
    if ([int]::TryParse([string]$value, [ref]$parsed)) {
        return $parsed
    }

    return 0
}

function Test-SessionExists {
    param(
        [string]$ApiBaseUrl,
        [string]$SessionId,
        [hashtable]$Headers,
        [int]$RequestTimeoutSec
    )

    try {
        $null = Invoke-RestMethod -Method Get -Uri "$($ApiBaseUrl.TrimEnd('/'))/api/v1/sessions/$([Uri]::EscapeDataString($SessionId))" -Headers $Headers -TimeoutSec $RequestTimeoutSec
        return $true
    }
    catch {
        $message = $_.Exception.Message
        if ($message -like "*404*" -or $message -like "*Не найден*") {
            return $false
        }

        throw
    }
}

$sessionEnvPath = Join-Path $platformRoot ".logs/webrtc-peer-adapter.session.env"
if (-not (Test-Path $sessionEnvPath)) {
    throw "Session env file was not found: $sessionEnvPath"
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
if ([string]::IsNullOrWhiteSpace($sessionId) -or [string]::IsNullOrWhiteSpace($peerId)) {
    throw "SESSION_ID/PEER_ID not found in $sessionEnvPath"
}

if (-not (Wait-ControlHealth -Url $ControlHealthUrl -Attempts $ControlHealthAttempts -DelayMs $ControlHealthDelayMs)) {
    throw "Control WS health endpoint is not ready: $ControlHealthUrl"
}

$tokenEndpoint = "$($KeycloakBaseUrl.TrimEnd('/'))/realms/$RealmName/protocol/openid-connect/token"
$tokenResponse = Invoke-RestMethod -Method Post -Uri $tokenEndpoint -TimeoutSec $RequestTimeoutSec -ContentType "application/x-www-form-urlencoded" -Body @{
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
    "X-HidBridge-UserId" = $PrincipalId
    "X-HidBridge-PrincipalId" = $PrincipalId
    "X-HidBridge-TenantId" = $TenantId
    "X-HidBridge-OrganizationId" = $OrganizationId
    "X-HidBridge-Role" = "operator.admin,operator.moderator,operator.viewer"
}

if (-not (Test-SessionExists -ApiBaseUrl $ApiBaseUrl -SessionId $sessionId -Headers $authHeaders -RequestTimeoutSec $RequestTimeoutSec)) {
    Write-Warning "Session '$sessionId' was not found via /api/v1/sessions/{id} lookup. Continuing; API build may hide this route by scope/profile."
}

if (-not $SkipTransportHealthCheck) {
    try {
        $transportHealth = Invoke-RestMethod -Method Get -Uri "$($ApiBaseUrl.TrimEnd('/'))/api/v1/sessions/$([Uri]::EscapeDataString($sessionId))/transport/health?provider=webrtc-datachannel" -Headers $authHeaders -TimeoutSec $RequestTimeoutSec
        $onlinePeerCount = Get-MetricAsInt -Metrics $transportHealth.metrics -Name "onlinePeerCount"
        if ($onlinePeerCount -lt 1) {
            throw "WebRTC relay peer is offline for session '$sessionId' (onlinePeerCount=$onlinePeerCount). Restart Terminal A stack or increase -AdapterDurationSec."
        }
    }
    catch {
        $message = $_.Exception.Message
        if ($message -like "*404*" -or $message -like "*Не найден*") {
            Write-Warning "Transport health endpoint is unavailable on this API build. Continuing without pre-check."
        }
        else {
            throw
        }
    }
}

$leaseBody = @{
    participantId = "owner:$PrincipalId"
    requestedBy = $PrincipalId
    leaseSeconds = [Math]::Max(30, $LeaseSeconds)
    reason = "webrtc stack terminal-b"
    tenantId = $TenantId
    organizationId = $OrganizationId
}

try {
    $null = Invoke-RestMethod -Method Post -Uri "$($ApiBaseUrl.TrimEnd('/'))/api/v1/sessions/$([Uri]::EscapeDataString($sessionId))/control/request" -Headers $authHeaders -TimeoutSec $RequestTimeoutSec -ContentType "application/json" -Body ($leaseBody | ConvertTo-Json -Depth 10)
}
catch {
    $message = $_.Exception.Message
    if ($message -like "*404*" -or $message -like "*Не найден*") {
        throw "Control request endpoint returned 404 for session '$sessionId'. Ensure API is up-to-date and session is active."
    }

    throw
}

$commandId = "cmd-webrtc-b-" + [DateTimeOffset]::UtcNow.ToString("HHmmss")
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
        participantId = "owner:$PrincipalId"
        principalId = $PrincipalId
    }
    timeoutMs = [Math]::Max(1000, $TimeoutMs)
    idempotencyKey = "idem-$commandId"
    tenantId = $TenantId
    organizationId = $OrganizationId
}

try {
    $commandResult = Invoke-RestMethod -Method Post -Uri "$($ApiBaseUrl.TrimEnd('/'))/api/v1/sessions/$([Uri]::EscapeDataString($sessionId))/commands" -Headers $authHeaders -TimeoutSec $RequestTimeoutSec -ContentType "application/json" -Body ($commandBody | ConvertTo-Json -Depth 20)
}
catch {
    $message = $_.Exception.Message
    if ($message -like "*404*" -or $message -like "*Не найден*") {
        throw "Command dispatch returned 404 for session '$sessionId'. Session is likely stale or API routes differ from expected build."
    }

    throw
}

$summary = [pscustomobject]@{
    apiBaseUrl = $ApiBaseUrl
    controlHealthUrl = $ControlHealthUrl
    sessionId = $sessionId
    peerId = $peerId
    principalId = $PrincipalId
    leaseSeconds = $LeaseSeconds
    commandId = $commandId
    commandAction = $CommandAction
    commandStatus = [string]$commandResult.status
    commandResponse = $commandResult
}

if (-not [string]::IsNullOrWhiteSpace($OutputJsonPath)) {
    $outputPath = if ([System.IO.Path]::IsPathRooted($OutputJsonPath)) { $OutputJsonPath } else { Join-Path $platformRoot $OutputJsonPath }
    $outputDir = Split-Path -Parent $outputPath
    if (-not [string]::IsNullOrWhiteSpace($outputDir) -and -not (Test-Path $outputDir)) {
        New-Item -ItemType Directory -Force -Path $outputDir | Out-Null
    }

    $summary | ConvertTo-Json -Depth 20 | Set-Content -Path $outputPath -Encoding utf8
}

Write-Host ""
Write-Host "=== WebRTC Terminal-B Summary ==="
Write-Host "Session:       $sessionId"
Write-Host "Peer:          $peerId"
Write-Host "Command:       $commandId"
Write-Host "Command status:$([string]$commandResult.status)"
if (-not [string]::IsNullOrWhiteSpace($OutputJsonPath)) {
    Write-Host "Summary JSON:  $OutputJsonPath"
}
Write-Host ""
$commandResult | ConvertTo-Json -Depth 20

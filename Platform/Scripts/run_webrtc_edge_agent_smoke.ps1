param(
    [Alias("BaseUrl")]
    [string]$ApiBaseUrl = "http://127.0.0.1:18093",
    [string]$KeycloakBaseUrl = "http://127.0.0.1:18096",
    [string]$RealmName = "hidbridge-dev",
    [string]$ControlHealthUrl = "http://127.0.0.1:28092/health",
    [int]$RequestTimeoutSec = 15,
    [int]$ControlHealthAttempts = 20,
    [int]$ControlHealthDelayMs = 500,
    [switch]$SkipTransportHealthCheck,
    [int]$TransportHealthAttempts = 20,
    [int]$TransportHealthDelayMs = 500,
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

# Waits until control health endpoint returns ok=true.
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

# Sends one JSON request and keeps response-body context on failures.
function Invoke-SmokeJson {
    param(
        [string]$Method,
        [string]$Uri,
        [hashtable]$Headers,
        [object]$Body = $null,
        [int]$TimeoutSec = 15
    )

    try {
        if ($null -eq $Body) {
            return Invoke-RestMethod -Method $Method -Uri $Uri -Headers $Headers -TimeoutSec $TimeoutSec
        }

        return Invoke-RestMethod `
            -Method $Method `
            -Uri $Uri `
            -Headers $Headers `
            -TimeoutSec $TimeoutSec `
            -ContentType "application/json" `
            -Body ($Body | ConvertTo-Json -Depth 20)
    }
    catch {
        $responseBody = $null
        try {
            $response = $_.Exception.Response
            if ($null -ne $response) {
                $stream = $response.GetResponseStream()
                if ($null -ne $stream) {
                    $reader = New-Object System.IO.StreamReader($stream)
                    try {
                        $responseBody = $reader.ReadToEnd()
                    }
                    finally {
                        $reader.Dispose()
                        $stream.Dispose()
                    }
                }
            }
        }
        catch {
        }

        if ([string]::IsNullOrWhiteSpace($responseBody)) {
            throw "HTTP $Method $Uri failed: $($_.Exception.Message)"
        }

        throw "HTTP $Method $Uri failed: $($_.Exception.Message)`n$responseBody"
    }
}

# Extracts HTTP status code from a web exception when available.
function Get-HttpStatusCode {
    param(
        [System.Exception]$Exception
    )

    if ($null -eq $Exception) {
        return $null
    }

    try {
        $responseProperty = $Exception.PSObject.Properties["Response"]
        $response = if ($null -ne $responseProperty) { $responseProperty.Value } else { $null }
        if ($null -ne $response) {
            $statusCodeProperty = $response.PSObject.Properties["StatusCode"]
            if ($null -ne $statusCodeProperty -and $null -ne $statusCodeProperty.Value) {
                return [int]$statusCodeProperty.Value
            }
        }
    }
    catch {
    }

    $message = [string]$Exception.Message
    if ($message -match "\((\d{3})\)") {
        return [int]$matches[1]
    }

    return $null
}

# Reads one property from object/dictionary without throwing in strict mode.
function Get-PropertyValue {
    param(
        [object]$InputObject,
        [string]$Name
    )

    if ($null -eq $InputObject -or [string]::IsNullOrWhiteSpace($Name)) {
        return $null
    }

    if ($InputObject -is [System.Collections.IDictionary] -and $InputObject.Contains($Name)) {
        return $InputObject[$Name]
    }

    foreach ($property in $InputObject.PSObject.Properties) {
        if ([string]::Equals($property.Name, $Name, [StringComparison]::OrdinalIgnoreCase)) {
            return $property.Value
        }
    }

    return $null
}

# Parses a value into int when possible.
function ConvertTo-NullableInt {
    param([object]$Value)

    if ($null -eq $Value) {
        return $null
    }

    $parsed = 0
    if ([int]::TryParse([string]$Value, [ref]$parsed)) {
        return $parsed
    }

    return $null
}

# Polls transport health until relay peer is connected and online.
function Wait-RelayTransportReady {
    param(
        [string]$ApiBaseUrl,
        [string]$SessionId,
        [hashtable]$Headers,
        [int]$Attempts,
        [int]$DelayMs,
        [int]$TimeoutSec
    )

    $healthUri = "$($ApiBaseUrl.TrimEnd('/'))/api/v1/sessions/$([Uri]::EscapeDataString($SessionId))/transport/health?provider=webrtc-datachannel"
    $lastSnapshot = $null

    for ($i = 0; $i -lt $Attempts; $i++) {
        try {
            $health = Invoke-SmokeJson -Method "GET" -Uri $healthUri -Headers $Headers -TimeoutSec $TimeoutSec
            $connected = [bool](Get-PropertyValue -InputObject $health -Name "connected")
            $onlinePeerCount = ConvertTo-NullableInt (Get-PropertyValue -InputObject $health -Name "onlinePeerCount")
            if ($null -eq $onlinePeerCount) {
                $metrics = Get-PropertyValue -InputObject $health -Name "metrics"
                $onlinePeerCount = ConvertTo-NullableInt (Get-PropertyValue -InputObject $metrics -Name "onlinePeerCount")
            }

            $peerState = [string](Get-PropertyValue -InputObject $health -Name "lastPeerState")
            if ([string]::IsNullOrWhiteSpace($peerState)) {
                $metrics = Get-PropertyValue -InputObject $health -Name "metrics"
                $peerState = [string](Get-PropertyValue -InputObject $metrics -Name "lastPeerState")
            }

            $isHealthyState = [string]::IsNullOrWhiteSpace($peerState) -or
                [string]::Equals($peerState, "Connected", [StringComparison]::OrdinalIgnoreCase) -or
                [string]::Equals($peerState, "Degraded", [StringComparison]::OrdinalIgnoreCase)

            $lastSnapshot = [pscustomobject]@{
                connected = $connected
                onlinePeerCount = $onlinePeerCount
                lastPeerState = if ([string]::IsNullOrWhiteSpace($peerState)) { $null } else { $peerState }
                lastPeerFailureReason = [string](Get-PropertyValue -InputObject $health -Name "lastPeerFailureReason")
                lastPeerSeenAtUtc = [string](Get-PropertyValue -InputObject $health -Name "lastPeerSeenAtUtc")
                lastRelayAckAtUtc = [string](Get-PropertyValue -InputObject $health -Name "lastRelayAckAtUtc")
            }

            if ($connected -and ($onlinePeerCount -ge 1) -and $isHealthyState) {
                return $lastSnapshot
            }
        }
        catch {
            $statusCode = Get-HttpStatusCode -Exception $_.Exception
            if ($statusCode -eq 404) {
                Write-Warning "Transport health endpoint is unavailable for session '$SessionId'. Skipping transport-health readiness check."
                return $null
            }
        }

        Start-Sleep -Milliseconds ([Math]::Max(100, $DelayMs))
    }

    if ($null -eq $lastSnapshot) {
        throw "WebRTC transport health did not become ready within $Attempts attempts."
    }

    $onlinePeerCountText = if ($null -eq $lastSnapshot.onlinePeerCount) { "null" } else { [string]$lastSnapshot.onlinePeerCount }
    $lastPeerStateText = if ([string]::IsNullOrWhiteSpace([string]$lastSnapshot.lastPeerState)) { "null" } else { [string]$lastSnapshot.lastPeerState }
    $lastPeerFailureReasonText = if ([string]::IsNullOrWhiteSpace([string]$lastSnapshot.lastPeerFailureReason)) { "null" } else { [string]$lastSnapshot.lastPeerFailureReason }
    throw ("WebRTC transport health is not ready (connected={0}, onlinePeerCount={1}, lastPeerState={2}, lastPeerFailureReason={3})." -f `
        $lastSnapshot.connected, `
        $onlinePeerCountText, `
        $lastPeerStateText, `
        $lastPeerFailureReasonText)
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
    $null = Invoke-SmokeJson -Method "POST" -Uri $onlineUri -Headers $authHeaders -Body $onlineBody -TimeoutSec ([Math]::Max(1, $RequestTimeoutSec))
}
catch {
    $message = $_.Exception.Message
    $statusCode = Get-HttpStatusCode -Exception $_.Exception
    if ($statusCode -eq 404) {
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
        $null = Invoke-SmokeJson -Method "POST" -Uri $leaseUri -Headers $authHeaders -Body $leaseBody -TimeoutSec ([Math]::Max(1, $RequestTimeoutSec))
    }
    catch {
        $statusCode = Get-HttpStatusCode -Exception $_.Exception
        if ($statusCode -eq 404) {
            Write-Warning "Control request endpoint is unavailable for session '$sessionId'. Continuing without explicit lease."
        }
        else {
            throw
        }
    }
}

if (-not $SkipTransportHealthCheck) {
    $transportReadiness = Wait-RelayTransportReady `
        -ApiBaseUrl $ApiBaseUrl `
        -SessionId $sessionId `
        -Headers $authHeaders `
        -Attempts ([Math]::Max(1, $TransportHealthAttempts)) `
        -DelayMs ([Math]::Max(100, $TransportHealthDelayMs)) `
        -TimeoutSec ([Math]::Max(1, $RequestTimeoutSec))
}
else {
    $transportReadiness = $null
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
    $commandResult = Invoke-SmokeJson `
        -Method "POST" `
        -Uri "$($ApiBaseUrl.TrimEnd('/'))/api/v1/sessions/$([Uri]::EscapeDataString($sessionId))/commands" `
        -Headers $authHeaders `
        -Body $commandBody `
        -TimeoutSec ([Math]::Max(1, $RequestTimeoutSec))
}
catch {
    $statusCode = Get-HttpStatusCode -Exception $_.Exception
    if ($statusCode -eq 404) {
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
    transportReadiness = $transportReadiness
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

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
    [string]$EndpointId = "endpoint_local_demo",
    [string]$Profile = "UltraLowLatency",
    [switch]$AutoCreateSessionIfMissing,
    [switch]$SkipControlLeaseRequest,
    [bool]$AllowMissingControlRequestEndpoint = $true,
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

# Reads HTTP status code from an exception response when available.
function Get-HttpStatusCode {
    param([System.Exception]$Exception)

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
        $statusCode = Get-HttpStatusCode -Exception $_.Exception
        if ($statusCode -eq 404) {
            return $false
        }

        throw
    }
}

function Get-LiveRelaySessionId {
    param(
        [string]$ApiBaseUrl,
        [hashtable]$Headers,
        [int]$RequestTimeoutSec,
        [string]$EndpointId
    )

    $sessionsResponse = Invoke-RestMethod -Method Get -Uri "$($ApiBaseUrl.TrimEnd('/'))/api/v1/sessions" -Headers $Headers -TimeoutSec $RequestTimeoutSec
    $sessions = @()
    if ($null -ne $sessionsResponse -and $sessionsResponse.PSObject.Properties["items"]) {
        $sessions = @($sessionsResponse.items)
    }
    else {
        $sessions = @($sessionsResponse)
    }

    foreach ($session in $sessions) {
        if ($null -eq $session) { continue }
        $candidateSessionId = if ($session.PSObject.Properties["sessionId"]) { [string]$session.sessionId } else { "" }
        if ([string]::IsNullOrWhiteSpace($candidateSessionId)) { continue }

        $state = if ($session.PSObject.Properties["state"]) { [string]$session.state } else { "" }
        if ([string]::Equals($state, "Ended", [StringComparison]::OrdinalIgnoreCase) -or
            [string]::Equals($state, "Failed", [StringComparison]::OrdinalIgnoreCase)) {
            continue
        }

        if (-not [string]::IsNullOrWhiteSpace($EndpointId) -and $session.PSObject.Properties["endpointId"]) {
            $candidateEndpointId = [string]$session.endpointId
            if (-not [string]::IsNullOrWhiteSpace($candidateEndpointId) -and
                -not [string]::Equals($candidateEndpointId, $EndpointId, [StringComparison]::OrdinalIgnoreCase)) {
                continue
            }
        }

        try {
            $health = Invoke-RestMethod -Method Get -Uri "$($ApiBaseUrl.TrimEnd('/'))/api/v1/sessions/$([Uri]::EscapeDataString($candidateSessionId))/transport/health?provider=webrtc-datachannel" -Headers $Headers -TimeoutSec $RequestTimeoutSec
            $onlinePeerCount = Get-MetricAsInt -Metrics $health.metrics -Name "onlinePeerCount"
            if ($onlinePeerCount -ge 1) {
                return $candidateSessionId
            }
        }
        catch {
        }
    }

    return $null
}

function Ensure-SessionExistsOrCreate {
    param(
        [string]$ApiBaseUrl,
        [string]$SessionId,
        [string]$EndpointId,
        [string]$Profile,
        [string]$PrincipalId,
        [string]$TenantId,
        [string]$OrganizationId,
        [hashtable]$Headers,
        [int]$RequestTimeoutSec
    )

    if (Test-SessionExists -ApiBaseUrl $ApiBaseUrl -SessionId $SessionId -Headers $Headers -RequestTimeoutSec $RequestTimeoutSec) {
        return $SessionId
    }

    $inventory = Invoke-RestMethod -Method Get -Uri "$($ApiBaseUrl.TrimEnd('/'))/api/v1/dashboards/inventory" -Headers $Headers -TimeoutSec $RequestTimeoutSec
    $endpoints = @()
    if ($null -ne $inventory -and $inventory.PSObject.Properties["endpoints"]) {
        $endpoints = @($inventory.endpoints)
    }

    $endpointCard = $endpoints | Where-Object {
        $_ -and $_.PSObject.Properties["endpointId"] -and [string]$_.endpointId -eq $EndpointId
    } | Select-Object -First 1

    if ($null -eq $endpointCard) {
        $endpointCard = $endpoints | Select-Object -First 1
    }

    if ($null -eq $endpointCard) {
        throw "Session '$SessionId' was not found and inventory did not return any endpoint to auto-create a replacement session."
    }

    $targetEndpointId = if ($endpointCard.PSObject.Properties["endpointId"]) { [string]$endpointCard.endpointId } else { "" }
    $targetAgentId = if ($endpointCard.PSObject.Properties["agentId"]) { [string]$endpointCard.agentId } else { "" }
    if ([string]::IsNullOrWhiteSpace($targetEndpointId) -or [string]::IsNullOrWhiteSpace($targetAgentId)) {
        throw "Session '$SessionId' was not found and inventory endpoint card is incomplete (endpointId/agentId missing)."
    }

    $openBody = @{
        sessionId = $SessionId
        profile = $Profile
        requestedBy = $PrincipalId
        targetAgentId = $targetAgentId
        targetEndpointId = $targetEndpointId
        shareMode = "Owner"
        tenantId = $TenantId
        organizationId = $OrganizationId
    }

    $null = Invoke-RestMethod -Method Post -Uri "$($ApiBaseUrl.TrimEnd('/'))/api/v1/sessions" -Headers $Headers -TimeoutSec $RequestTimeoutSec -ContentType "application/json" -Body ($openBody | ConvertTo-Json -Depth 20)
    return $SessionId
}

function New-ReplacementSessionId {
    param([string]$EndpointId)

    $segment = if ([string]::IsNullOrWhiteSpace($EndpointId)) { "endpoint" } else { $EndpointId.ToLowerInvariant().Replace("_", "-").Replace(" ", "-") }
    $suffix = [Guid]::NewGuid().ToString("N").Substring(0, 10)
    $candidate = "room-$segment-$suffix"
    if ($candidate.Length -le 64) {
        return $candidate
    }

    return $candidate.Substring(0, 64)
}

function Request-ControlLease {
    param(
        [string]$ApiBaseUrl,
        [string]$SessionId,
        [hashtable]$Headers,
        [int]$RequestTimeoutSec,
        [string]$PrincipalId,
        [string]$TenantId,
        [string]$OrganizationId,
        [int]$LeaseSeconds
    )

    $leaseBody = @{
        participantId = "owner:$PrincipalId"
        requestedBy = $PrincipalId
        leaseSeconds = [Math]::Max(30, $LeaseSeconds)
        reason = "webrtc stack terminal-b"
        tenantId = $TenantId
        organizationId = $OrganizationId
    }

    $leaseUri = "$($ApiBaseUrl.TrimEnd('/'))/api/v1/sessions/$([Uri]::EscapeDataString($SessionId))/control/request"
    $maxAttempts = 10
    for ($attempt = 1; $attempt -le $maxAttempts; $attempt++) {
        try {
            Invoke-RestMethod -Method Post -Uri $leaseUri -Headers $Headers -TimeoutSec $RequestTimeoutSec -ContentType "application/json" -Body ($leaseBody | ConvertTo-Json -Depth 10) | Out-Null
            return
        }
        catch {
            $statusCode = Get-HttpStatusCode -Exception $_.Exception
            $isNotFound = $statusCode -eq 404
            if ($isNotFound -and $attempt -lt $maxAttempts) {
                Start-Sleep -Milliseconds 250
                continue
            }

            throw
        }
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

if (Test-SessionExists -ApiBaseUrl $ApiBaseUrl -SessionId $sessionId -Headers $authHeaders -RequestTimeoutSec $RequestTimeoutSec) {
    # session exists, continue
}
else {
    $liveSessionId = Get-LiveRelaySessionId -ApiBaseUrl $ApiBaseUrl -Headers $authHeaders -RequestTimeoutSec $RequestTimeoutSec -EndpointId $EndpointId
    if (-not [string]::IsNullOrWhiteSpace($liveSessionId)) {
        Write-Warning "Session '$sessionId' was not found. Reusing live relay session '$liveSessionId'."
        $sessionId = $liveSessionId
        @"
SESSION_ID=$sessionId
PEER_ID=$peerId
"@ | Set-Content -Path $sessionEnvPath -Encoding ascii
    }
    elseif ($AutoCreateSessionIfMissing) {
        Write-Warning "Session '$sessionId' was not found and no live relay session detected. Auto-creating replacement session for Terminal-B flow."
        $sessionId = Ensure-SessionExistsOrCreate `
            -ApiBaseUrl $ApiBaseUrl `
            -SessionId $sessionId `
            -EndpointId $EndpointId `
            -Profile $Profile `
            -PrincipalId $PrincipalId `
            -TenantId $TenantId `
            -OrganizationId $OrganizationId `
            -Headers $authHeaders `
            -RequestTimeoutSec $RequestTimeoutSec

        @"
SESSION_ID=$sessionId
PEER_ID=$peerId
"@ | Set-Content -Path $sessionEnvPath -Encoding ascii
    }
    else {
        throw "Session '$sessionId' was not found and no live WebRTC relay session is available. Start/restart Terminal A stack first."
    }
}

if (-not $SkipTransportHealthCheck) {
    try {
        $transportHealth = Invoke-RestMethod -Method Get -Uri "$($ApiBaseUrl.TrimEnd('/'))/api/v1/sessions/$([Uri]::EscapeDataString($sessionId))/transport/health?provider=webrtc-datachannel" -Headers $authHeaders -TimeoutSec $RequestTimeoutSec
        $onlinePeerCount = Get-MetricAsInt -Metrics $transportHealth.metrics -Name "onlinePeerCount"
        if ($onlinePeerCount -lt 1) {
            if ($AutoCreateSessionIfMissing) {
                $liveSessionId = Get-LiveRelaySessionId -ApiBaseUrl $ApiBaseUrl -Headers $authHeaders -RequestTimeoutSec $RequestTimeoutSec -EndpointId $EndpointId
                if (-not [string]::IsNullOrWhiteSpace($liveSessionId) -and -not [string]::Equals($liveSessionId, $sessionId, [StringComparison]::OrdinalIgnoreCase)) {
                    Write-Warning "Current session '$sessionId' has no online relay peers. Switching to live relay session '$liveSessionId'."
                    $sessionId = $liveSessionId
                    @"
SESSION_ID=$sessionId
PEER_ID=$peerId
"@ | Set-Content -Path $sessionEnvPath -Encoding ascii
                }
                else {
                    throw "WebRTC relay peer is offline for session '$sessionId' (onlinePeerCount=$onlinePeerCount). Restart Terminal A stack or increase -AdapterDurationSec."
                }
            }
            else {
                throw "WebRTC relay peer is offline for session '$sessionId' (onlinePeerCount=$onlinePeerCount). Restart Terminal A stack or increase -AdapterDurationSec."
            }
        }
    }
    catch {
        $statusCode = Get-HttpStatusCode -Exception $_.Exception
        if ($statusCode -eq 404) {
            Write-Warning "Transport health endpoint is unavailable on this API build. Continuing without pre-check."
        }
        else {
            throw
        }
    }
}

if (-not $SkipControlLeaseRequest) {
    try {
        Request-ControlLease `
            -ApiBaseUrl $ApiBaseUrl `
            -SessionId $sessionId `
            -Headers $authHeaders `
            -RequestTimeoutSec $RequestTimeoutSec `
            -PrincipalId $PrincipalId `
            -TenantId $TenantId `
            -OrganizationId $OrganizationId `
            -LeaseSeconds $LeaseSeconds
    }
    catch {
        $statusCode = Get-HttpStatusCode -Exception $_.Exception
        if ($statusCode -eq 404) {
            if ($AutoCreateSessionIfMissing) {
                $replacementSessionId = New-ReplacementSessionId -EndpointId $EndpointId
                Write-Warning "Control request returned 404 for session '$sessionId'. Creating replacement session '$replacementSessionId' and retrying once."

                $sessionId = Ensure-SessionExistsOrCreate `
                    -ApiBaseUrl $ApiBaseUrl `
                    -SessionId $replacementSessionId `
                    -EndpointId $EndpointId `
                    -Profile $Profile `
                    -PrincipalId $PrincipalId `
                    -TenantId $TenantId `
                    -OrganizationId $OrganizationId `
                    -Headers $authHeaders `
                    -RequestTimeoutSec $RequestTimeoutSec

                @"
SESSION_ID=$sessionId
PEER_ID=$peerId
"@ | Set-Content -Path $sessionEnvPath -Encoding ascii

                Request-ControlLease `
                    -ApiBaseUrl $ApiBaseUrl `
                    -SessionId $sessionId `
                    -Headers $authHeaders `
                    -RequestTimeoutSec $RequestTimeoutSec `
                    -PrincipalId $PrincipalId `
                    -TenantId $TenantId `
                    -OrganizationId $OrganizationId `
                    -LeaseSeconds $LeaseSeconds
            }
            else {
                $liveSessionId = Get-LiveRelaySessionId -ApiBaseUrl $ApiBaseUrl -Headers $authHeaders -RequestTimeoutSec $RequestTimeoutSec -EndpointId $EndpointId
                if (-not [string]::IsNullOrWhiteSpace($liveSessionId) -and -not [string]::Equals($liveSessionId, $sessionId, [StringComparison]::OrdinalIgnoreCase)) {
                    Write-Warning "Control request returned 404 for session '$sessionId'. Switching to live relay session '$liveSessionId' and retrying once."
                    $sessionId = $liveSessionId
                    @"
SESSION_ID=$sessionId
PEER_ID=$peerId
"@ | Set-Content -Path $sessionEnvPath -Encoding ascii

                    Request-ControlLease `
                        -ApiBaseUrl $ApiBaseUrl `
                        -SessionId $sessionId `
                        -Headers $authHeaders `
                        -RequestTimeoutSec $RequestTimeoutSec `
                        -PrincipalId $PrincipalId `
                        -TenantId $TenantId `
                        -OrganizationId $OrganizationId `
                        -LeaseSeconds $LeaseSeconds
                }
                else {
                    if ($AllowMissingControlRequestEndpoint) {
                        Write-Warning "Control request endpoint returned 404 for session '$sessionId'. Continuing without explicit lease because this API build may hide /control/request."
                    }
                    else {
                        throw "Control request endpoint returned 404 for session '$sessionId'. Ensure API is up-to-date and session is active."
                    }
                }
            }
        }
        else {
            throw
        }
    }
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
    $statusCode = Get-HttpStatusCode -Exception $_.Exception
    if ($statusCode -eq 404) {
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

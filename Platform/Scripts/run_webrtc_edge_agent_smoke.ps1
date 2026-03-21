param(
    [Alias("BaseUrl")]
    [string]$ApiBaseUrl = "http://127.0.0.1:18093",
    [string]$KeycloakBaseUrl = "http://127.0.0.1:18096",
    [string]$RealmName = "hidbridge-dev",
    [string]$ControlHealthUrl = "http://127.0.0.1:28092/health",
    [int]$RequestTimeoutSec = 15,
    [int]$ControlHealthAttempts = 20,
    [int]$ControlHealthDelayMs = 500,
    [int]$KeycloakHealthAttempts = 60,
    [int]$KeycloakHealthDelayMs = 500,
    [int]$TokenRequestAttempts = 5,
    [int]$TokenRequestDelayMs = 500,
    [switch]$SkipControlHealthCheck,
    [switch]$SkipTransportHealthCheck,
    [int]$TransportHealthAttempts = 20,
    [int]$TransportHealthDelayMs = 500,
    [string]$TokenClientId = "controlplane-smoke",
    [string]$TokenClientSecret = "",
    [string]$TokenScope = "openid profile email",
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

# Waits until Keycloak realm endpoint responds and confirms the realm name.
function Wait-KeycloakRealmHealth {
    param(
        [string]$BaseUrl,
        [string]$Realm,
        [int]$Attempts,
        [int]$DelayMs
    )

    $realmUri = "$($BaseUrl.TrimEnd('/'))/realms/$Realm"
    for ($i = 0; $i -lt $Attempts; $i++) {
        try {
            $response = Invoke-RestMethod -Method Get -Uri $realmUri -TimeoutSec 2
            $realmValue = [string]$response.realm
            if (-not [string]::IsNullOrWhiteSpace($realmValue) -and [string]::Equals($realmValue, $Realm, [StringComparison]::OrdinalIgnoreCase)) {
                return $true
            }
        }
        catch {
        }

        Start-Sleep -Milliseconds $DelayMs
    }

    return $false
}

# Requests one OIDC access token with retry to absorb transient startup/network races.
function Request-KeycloakTokenWithRetry {
    param(
        [string]$TokenEndpoint,
        [int]$TimeoutSec,
        [int]$Attempts,
        [int]$DelayMs,
        [hashtable]$Body
    )

    $lastError = $null
    $lastErrorResponseBody = ""
    for ($attempt = 1; $attempt -le $Attempts; $attempt++) {
        try {
            return Invoke-RestMethod `
                -Method Post `
                -Uri $TokenEndpoint `
                -TimeoutSec $TimeoutSec `
                -ContentType "application/x-www-form-urlencoded" `
                -Body $Body
        }
        catch {
            $lastError = $_.Exception
            $lastErrorResponseBody = ""
            try {
                $responseProperty = $lastError.PSObject.Properties["Response"]
                $response = if ($null -ne $responseProperty) { $responseProperty.Value } else { $null }
                if ($null -ne $response) {
                    $stream = $response.GetResponseStream()
                    if ($null -ne $stream) {
                        $reader = New-Object System.IO.StreamReader($stream)
                        try {
                            $lastErrorResponseBody = $reader.ReadToEnd()
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

            if ($attempt -lt $Attempts) {
                Start-Sleep -Milliseconds $DelayMs
            }
        }
    }

    if ($null -eq $lastError) {
        throw "Keycloak token request failed: unknown error."
    }

    if (-not [string]::IsNullOrWhiteSpace($lastErrorResponseBody)) {
        throw "Keycloak token request failed after $Attempts attempt(s): $($lastError.Message)`n$lastErrorResponseBody"
    }

    throw "Keycloak token request failed after $Attempts attempt(s): $($lastError.Message)"
}

# Attempts to read active API auth authority from runtime settings so token issuer can match API validation authority.
function Resolve-ApiAuthAuthority {
    param(
        [string]$BaseUrl,
        [int]$TimeoutSec
    )

    try {
        $runtime = Invoke-RestMethod -Method Get -Uri "$($BaseUrl.TrimEnd('/'))/api/v1/runtime/uart" -TimeoutSec $TimeoutSec
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

# Polls server-side readiness endpoint (with transport-health fallback for older API builds).
function Wait-RelayTransportReady {
    param(
        [string]$ApiBaseUrl,
        [string]$SessionId,
        [hashtable]$Headers,
        [int]$Attempts,
        [int]$DelayMs,
        [int]$TimeoutSec
    )

    $readinessUri = "$($ApiBaseUrl.TrimEnd('/'))/api/v1/sessions/$([Uri]::EscapeDataString($SessionId))/transport/readiness?provider=webrtc-datachannel"
    $healthUri = "$($ApiBaseUrl.TrimEnd('/'))/api/v1/sessions/$([Uri]::EscapeDataString($SessionId))/transport/health?provider=webrtc-datachannel"
    $lastSnapshot = $null
    $fallbackToHealth = $false

    for ($i = 0; $i -lt $Attempts; $i++) {
        try {
            if (-not $fallbackToHealth) {
                $readiness = Invoke-SmokeJson -Method "GET" -Uri $readinessUri -Headers $Headers -TimeoutSec $TimeoutSec
                $ready = [bool](Get-PropertyValue -InputObject $readiness -Name "ready")
                $connected = [bool](Get-PropertyValue -InputObject $readiness -Name "connected")
                $onlinePeerCount = ConvertTo-NullableInt (Get-PropertyValue -InputObject $readiness -Name "onlinePeerCount")
                $peerState = [string](Get-PropertyValue -InputObject $readiness -Name "lastPeerState")
                $reasonCode = [string](Get-PropertyValue -InputObject $readiness -Name "reasonCode")
                $reason = [string](Get-PropertyValue -InputObject $readiness -Name "reason")
                $lastSnapshot = [pscustomobject]@{
                    connected = $connected
                    onlinePeerCount = $onlinePeerCount
                    lastPeerState = if ([string]::IsNullOrWhiteSpace($peerState)) { $null } else { $peerState }
                    lastPeerFailureReason = [string](Get-PropertyValue -InputObject $readiness -Name "lastPeerFailureReason")
                    lastPeerSeenAtUtc = [string](Get-PropertyValue -InputObject $readiness -Name "lastPeerSeenAtUtc")
                    lastRelayAckAtUtc = [string](Get-PropertyValue -InputObject $readiness -Name "lastRelayAckAtUtc")
                    readinessReasonCode = if ([string]::IsNullOrWhiteSpace($reasonCode)) { $null } else { $reasonCode }
                    readinessReason = if ([string]::IsNullOrWhiteSpace($reason)) { $null } else { $reason }
                }

                if ($ready) {
                    return $lastSnapshot
                }
            }
            else {
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
                    readinessReasonCode = if ($isHealthyState -and $connected -and ($onlinePeerCount -ge 1)) { "ready" } else { "legacy_transport_health_not_ready" }
                    readinessReason = if ($isHealthyState -and $connected -and ($onlinePeerCount -ge 1)) { "Legacy transport health indicates ready route." } else { "Legacy transport health did not satisfy readiness checks." }
                }

                if ($connected -and ($onlinePeerCount -ge 1) -and $isHealthyState) {
                    return $lastSnapshot
                }
            }
        }
        catch {
            $statusCode = Get-HttpStatusCode -Exception $_.Exception
            if (-not $fallbackToHealth -and $statusCode -eq 404) {
                Write-Warning "Transport readiness endpoint is unavailable for session '$SessionId'. Falling back to transport-health checks."
                $fallbackToHealth = $true
                continue
            }

            if ($fallbackToHealth -and $statusCode -eq 404) {
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
    $reasonCodeText = if ($lastSnapshot.PSObject.Properties["readinessReasonCode"] -and -not [string]::IsNullOrWhiteSpace([string]$lastSnapshot.readinessReasonCode)) { [string]$lastSnapshot.readinessReasonCode } else { "unknown" }
    $reasonText = if ($lastSnapshot.PSObject.Properties["readinessReason"] -and -not [string]::IsNullOrWhiteSpace([string]$lastSnapshot.readinessReason)) { [string]$lastSnapshot.readinessReason } else { "n/a" }
    throw ("WebRTC transport readiness is not ready (reasonCode={0}, reason={1}, connected={2}, onlinePeerCount={3}, lastPeerState={4}, lastPeerFailureReason={5})." -f `
        $reasonCodeText, `
        $reasonText, `
        $lastSnapshot.connected, `
        $onlinePeerCountText, `
        $lastPeerStateText, `
        $lastPeerFailureReasonText)
}

if (-not (Test-Path $sessionEnvPath)) {
    throw "Session env file was not found: $sessionEnvPath. Start webrtc-stack first."
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
$sessionCommandExecutor = [string]$kv["COMMAND_EXECUTOR"]
$sessionControlHealthUrl = [string]$kv["CONTROL_HEALTH_URL"]
$sessionEndpointId = [string]$kv["ENDPOINT_ID"]
$sessionPrincipalId = [string]$kv["PRINCIPAL_ID"]
$sessionTenantId = [string]$kv["TENANT_ID"]
$sessionOrganizationId = [string]$kv["ORGANIZATION_ID"]
if ([string]::IsNullOrWhiteSpace($sessionId) -or [string]::IsNullOrWhiteSpace($peerId)) {
    throw "SESSION_ID/PEER_ID not found in $sessionEnvPath"
}

if (($null -eq $PSBoundParameters["ControlHealthUrl"] -or [string]::IsNullOrWhiteSpace($PSBoundParameters["ControlHealthUrl"])) `
    -and -not [string]::IsNullOrWhiteSpace($sessionControlHealthUrl)) {
    $ControlHealthUrl = $sessionControlHealthUrl
}

$isUartExecutor = [string]::Equals($sessionCommandExecutor, "uart", [StringComparison]::OrdinalIgnoreCase)
$effectiveSkipControlHealthCheck = $SkipControlHealthCheck -or $isUartExecutor
if (-not $effectiveSkipControlHealthCheck) {
    if (-not (Wait-ControlHealth -Url $ControlHealthUrl -Attempts ([Math]::Max(1, $ControlHealthAttempts)) -DelayMs ([Math]::Max(100, $ControlHealthDelayMs)))) {
        throw "Control WS health endpoint is not ready: $ControlHealthUrl"
    }
}
elseif ($isUartExecutor) {
    Write-Warning "Session executor is UART; skipping control-health precheck."
}

$effectivePrincipalId = if (-not [string]::IsNullOrWhiteSpace($sessionPrincipalId)) { $sessionPrincipalId } else { $PrincipalId }
$effectiveTenantId = if (-not [string]::IsNullOrWhiteSpace($sessionTenantId)) { $sessionTenantId } else { $TenantId }
$effectiveOrganizationId = if (-not [string]::IsNullOrWhiteSpace($sessionOrganizationId)) { $sessionOrganizationId } else { $OrganizationId }

$effectiveKeycloakBaseUrl = $KeycloakBaseUrl.TrimEnd('/')
$effectiveRealmName = $RealmName
$runtimeAuthAuthority = Resolve-ApiAuthAuthority -BaseUrl $ApiBaseUrl -TimeoutSec ([Math]::Max(1, $RequestTimeoutSec))
if (-not [string]::IsNullOrWhiteSpace($runtimeAuthAuthority)) {
    $runtimeAuthUri = $null
    if ([Uri]::TryCreate($runtimeAuthAuthority, [UriKind]::Absolute, [ref]$runtimeAuthUri)) {
        $runtimeRealmName = Get-RealmNameFromAuthorityPath -Path $runtimeAuthUri.AbsolutePath
        if (-not [string]::IsNullOrWhiteSpace($runtimeRealmName)) {
            $effectiveRealmName = $runtimeRealmName
        }
    }
}

$tokenAuthorityCandidates = [System.Collections.Generic.List[string]]::new()
if (-not [string]::IsNullOrWhiteSpace($runtimeAuthAuthority)) {
    $tokenAuthorityCandidates.Add($runtimeAuthAuthority.TrimEnd('/')) | Out-Null
}

$defaultAuthorities = @(
    "$($KeycloakBaseUrl.TrimEnd('/'))/realms/$effectiveRealmName",
    "http://127.0.0.1:18096/realms/$effectiveRealmName",
    "http://localhost:18096/realms/$effectiveRealmName",
    "http://host.docker.internal:18096/realms/$effectiveRealmName"
)

foreach ($authorityCandidate in $defaultAuthorities) {
    if ($tokenAuthorityCandidates -notcontains $authorityCandidate) {
        $tokenAuthorityCandidates.Add($authorityCandidate) | Out-Null
    }
}
$scopeCandidates = [System.Collections.Generic.List[string]]::new()
if (-not [string]::IsNullOrWhiteSpace($TokenScope)) {
    $scopeCandidates.Add($TokenScope) | Out-Null
}
if ($scopeCandidates -notcontains "openid") {
    $scopeCandidates.Add("openid") | Out-Null
}
if ($scopeCandidates -notcontains "") {
    $scopeCandidates.Add("") | Out-Null
}

$accessToken = ""
$effectiveKeycloakBaseUrl = ""
$tokenAcquireErrors = [System.Collections.Generic.List[string]]::new()
$probeUri = "$($ApiBaseUrl.TrimEnd('/'))/api/v1/dashboards/inventory"

foreach ($authorityCandidate in $tokenAuthorityCandidates) {
    $authorityUri = $null
    if (-not [Uri]::TryCreate($authorityCandidate, [UriKind]::Absolute, [ref]$authorityUri)) {
        continue
    }

    $candidateRealmName = Get-RealmNameFromAuthorityPath -Path $authorityUri.AbsolutePath
    if ([string]::IsNullOrWhiteSpace($candidateRealmName)) {
        $candidateRealmName = $effectiveRealmName
    }

    $candidateBase = "$($authorityUri.Scheme)://$($authorityUri.Host):$($authorityUri.Port)".TrimEnd('/')
    if (-not (Wait-KeycloakRealmHealth -BaseUrl $candidateBase -Realm $candidateRealmName -Attempts ([Math]::Max(1, $KeycloakHealthAttempts)) -DelayMs ([Math]::Max(100, $KeycloakHealthDelayMs)))) {
        $tokenAcquireErrors.Add("authority=${authorityCandidate}: realm endpoint not ready") | Out-Null
        continue
    }

    $tokenEndpoint = "$($candidateBase)/realms/$candidateRealmName/protocol/openid-connect/token"
    foreach ($scopeCandidate in $scopeCandidates) {
        $tokenBody = @{
            grant_type = "password"
            client_id = $TokenClientId
            username = $TokenUsername
            password = $TokenPassword
        }
        if (-not [string]::IsNullOrWhiteSpace($TokenClientSecret)) {
            $tokenBody.client_secret = $TokenClientSecret
        }
        if (-not [string]::IsNullOrWhiteSpace($scopeCandidate)) {
            $tokenBody.scope = $scopeCandidate
        }

        try {
            $tokenResponse = Request-KeycloakTokenWithRetry `
                -TokenEndpoint $tokenEndpoint `
                -TimeoutSec ([Math]::Max(1, $RequestTimeoutSec)) `
                -Attempts ([Math]::Max(1, $TokenRequestAttempts)) `
                -DelayMs ([Math]::Max(100, $TokenRequestDelayMs)) `
                -Body $tokenBody

            $candidateAccessToken = [string]$tokenResponse.access_token
            if ([string]::IsNullOrWhiteSpace($candidateAccessToken)) {
                throw "Token response did not contain access_token."
            }

            $probeHeaders = @{
                Authorization = "Bearer $candidateAccessToken"
                "X-HidBridge-UserId" = $effectivePrincipalId
                "X-HidBridge-PrincipalId" = $effectivePrincipalId
                "X-HidBridge-TenantId" = $effectiveTenantId
                "X-HidBridge-OrganizationId" = $effectiveOrganizationId
                "X-HidBridge-Role" = "operator.admin,operator.moderator,operator.viewer"
            }

            try {
                $null = Invoke-SmokeJson -Method "GET" -Uri $probeUri -Headers $probeHeaders -TimeoutSec ([Math]::Max(1, $RequestTimeoutSec))
            }
            catch {
                $probeStatus = Get-HttpStatusCode -Exception $_.Exception
                if ($probeStatus -eq 401) {
                    throw "Probe endpoint rejected bearer token with 401."
                }
            }

            $accessToken = $candidateAccessToken
            $effectiveKeycloakBaseUrl = $candidateBase
            break
        }
        catch {
            $scopeLabel = if ([string]::IsNullOrWhiteSpace($scopeCandidate)) { "<empty>" } else { $scopeCandidate }
            $tokenAcquireErrors.Add("authority=$authorityCandidate; scope=${scopeLabel}: $($_.Exception.Message)") | Out-Null
        }
    }

    if (-not [string]::IsNullOrWhiteSpace($accessToken)) {
        break
    }
}

if ([string]::IsNullOrWhiteSpace($accessToken)) {
    throw "Keycloak token request failed for all authority/scope candidates: $($tokenAcquireErrors -join '; ')"
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
    $leaseEnsureBody = @{
        participantId = "owner:$effectivePrincipalId"
        requestedBy = $effectivePrincipalId
        endpointId = if ([string]::IsNullOrWhiteSpace($sessionEndpointId)) { $null } else { $sessionEndpointId }
        profile = "UltraLowLatency"
        leaseSeconds = [Math]::Max(30, $LeaseSeconds)
        reason = "webrtc edge-agent smoke"
        autoCreateSessionIfMissing = $true
        preferLiveRelaySession = $true
        tenantId = $effectiveTenantId
        organizationId = $effectiveOrganizationId
    }

    $leaseEnsureUri = "$($ApiBaseUrl.TrimEnd('/'))/api/v1/sessions/$([Uri]::EscapeDataString($sessionId))/control/ensure"
    try {
        $leaseEnsureResult = Invoke-SmokeJson -Method "POST" -Uri $leaseEnsureUri -Headers $authHeaders -Body $leaseEnsureBody -TimeoutSec ([Math]::Max(1, $RequestTimeoutSec))
        $effectiveSessionId = [string](Get-PropertyValue -InputObject $leaseEnsureResult -Name "effectiveSessionId")
        if (-not [string]::IsNullOrWhiteSpace($effectiveSessionId) -and -not [string]::Equals($effectiveSessionId, $sessionId, [StringComparison]::OrdinalIgnoreCase)) {
            Write-Warning "Control ensure switched session '$sessionId' -> '$effectiveSessionId'."
            $sessionId = $effectiveSessionId
        }
    }
    catch {
        $statusCode = Get-HttpStatusCode -Exception $_.Exception
        if ($statusCode -eq 404) {
            Write-Warning "Control ensure endpoint is unavailable for session '$sessionId'. Falling back to legacy /control/request flow."
            $legacyLeaseBody = @{
                participantId = "owner:$effectivePrincipalId"
                requestedBy = $effectivePrincipalId
                leaseSeconds = [Math]::Max(30, $LeaseSeconds)
                reason = "webrtc edge-agent smoke"
                tenantId = $effectiveTenantId
                organizationId = $effectiveOrganizationId
            }
            $legacyLeaseUri = "$($ApiBaseUrl.TrimEnd('/'))/api/v1/sessions/$([Uri]::EscapeDataString($sessionId))/control/request"
            try {
                $null = Invoke-SmokeJson -Method "POST" -Uri $legacyLeaseUri -Headers $authHeaders -Body $legacyLeaseBody -TimeoutSec ([Math]::Max(1, $RequestTimeoutSec))
            }
            catch {
                $legacyStatusCode = Get-HttpStatusCode -Exception $_.Exception
                if ($legacyStatusCode -eq 404) {
                    Write-Warning "Legacy control request endpoint is unavailable for session '$sessionId'. Continuing without explicit lease."
                }
                else {
                    throw
                }
            }
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
    commandExecutor = if ([string]::IsNullOrWhiteSpace($sessionCommandExecutor)) { "unknown" } else { $sessionCommandExecutor }
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
if (-not [string]::IsNullOrWhiteSpace($sessionCommandExecutor)) {
    Write-Host "Executor: $sessionCommandExecutor"
}
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

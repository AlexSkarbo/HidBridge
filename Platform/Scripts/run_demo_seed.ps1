param(
    [string]$BaseUrl = "http://127.0.0.1:18093",
    [string]$KeycloakBaseUrl = "http://127.0.0.1:18096",
    [string]$RealmName = "hidbridge-dev",
    [string]$TokenClientId = "controlplane-smoke",
    [string]$TokenClientSecret = "",
    [string]$TokenScope = "openid profile email",
    [string]$TokenUsername = "operator.smoke.admin",
    [string]$TokenPassword = "ChangeMe123!",
    [string]$AccessToken = "",
    [string]$PrincipalId = "demo-seed-admin",
    [string]$TenantId = "local-tenant",
    [string]$OrganizationId = "local-org",
    [switch]$SkipCloseActiveSessions,
    [int]$ReadyEndpointTimeoutSec = 60,
    [int]$ReadyEndpointPollIntervalMs = 500
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$scriptsRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $scriptsRoot "Common/KeycloakCommon.ps1")

function Get-ErrorResponseBody {
    param([System.Exception]$Exception)

    $responseProperty = $Exception.PSObject.Properties["Response"]
    $response = if ($null -ne $responseProperty) { $responseProperty.Value } else { $null }
    if ($null -eq $response) {
        return $null
    }

    try {
        $stream = $response.GetResponseStream()
        if ($null -eq $stream) {
            return $null
        }

        $reader = New-Object System.IO.StreamReader($stream)
        try {
            return $reader.ReadToEnd()
        }
        finally {
            $reader.Dispose()
            $stream.Dispose()
        }
    }
    catch {
        return $null
    }
}

function Invoke-DemoJson {
    param(
        [string]$Method,
        [string]$Uri,
        [object]$Body = $null,
        [hashtable]$Headers = @{}
    )

    try {
        if ($null -eq $Body) {
            return Invoke-RestMethod -Method $Method -Uri $Uri -Headers $Headers -TimeoutSec 20
        }

        $json = $Body | ConvertTo-Json -Depth 20
        return Invoke-RestMethod -Method $Method -Uri $Uri -Headers $Headers -ContentType "application/json" -Body $json -TimeoutSec 20
    }
    catch {
        $responseBody = Get-ErrorResponseBody $_.Exception
        if ([string]::IsNullOrWhiteSpace($responseBody)) {
            throw "HTTP $Method $Uri failed: $($_.Exception.Message)"
        }

        throw "HTTP $Method $Uri failed: $($_.Exception.Message)`n$responseBody"
    }
}

function Get-ObjectCount {
    param([object]$Value)

    if ($null -eq $Value) {
        return 0
    }

    return @($Value).Count
}

function Get-NormalizedSessionState {
    param([object]$Session)

    if ($null -eq $Session) {
        return ""
    }

    $stateProperty = $Session.PSObject.Properties["state"]
    if ($null -eq $stateProperty -or $null -eq $stateProperty.Value) {
        return ""
    }

    return [string]$stateProperty.Value
}

$resolvedToken = $AccessToken
if ([string]::IsNullOrWhiteSpace($resolvedToken)) {
    try {
        $tokenResponse = Get-KeycloakOidcToken `
            -BaseUrl $KeycloakBaseUrl `
            -RealmName $RealmName `
            -ClientId $TokenClientId `
            -ClientSecret $TokenClientSecret `
            -Scope $TokenScope `
            -Username $TokenUsername `
            -Password $TokenPassword
    }
    catch {
        throw "Demo seed could not acquire bearer token for '$TokenUsername'. $($_.Exception.Message)"
    }

    if ([string]::IsNullOrWhiteSpace($tokenResponse.access_token)) {
        throw "Demo seed token response did not contain access_token."
    }

    $resolvedToken = $tokenResponse.access_token
}

$callerHeaders = @{
    "Authorization" = "Bearer $resolvedToken"
    "X-HidBridge-UserId" = $PrincipalId
    "X-HidBridge-PrincipalId" = $PrincipalId
    "X-HidBridge-TenantId" = $TenantId
    "X-HidBridge-OrganizationId" = $OrganizationId
    "X-HidBridge-Role" = "operator.admin,operator.moderator,operator.viewer"
}

$sessions = @(Invoke-DemoJson -Method "GET" -Uri "$($BaseUrl.TrimEnd('/'))/api/v1/sessions" -Headers $callerHeaders)
$activeSessions = @($sessions | Where-Object {
    $_ -and
    $_.PSObject.Properties["state"] -and
    [string]$_.state -eq "Active"
})
$sessionsToClose = @($sessions | Where-Object {
    if ($null -eq $_ -or $null -eq $_.PSObject.Properties["sessionId"]) {
        return $false
    }

    $state = Get-NormalizedSessionState -Session $_
    if ([string]::IsNullOrWhiteSpace($state)) {
        return $true
    }

    return -not (
        [string]::Equals($state, "Ended", [StringComparison]::OrdinalIgnoreCase) -or
        [string]::Equals($state, "Failed", [StringComparison]::OrdinalIgnoreCase)
    )
})

$closedSessionIds = [System.Collections.Generic.List[string]]::new()
if (-not $SkipCloseActiveSessions) {
    foreach ($session in $sessionsToClose) {
        if ($null -eq $session.PSObject.Properties["sessionId"] -or [string]::IsNullOrWhiteSpace([string]$session.sessionId)) {
            continue
        }

        $sessionId = [string]$session.sessionId
        $null = Invoke-DemoJson -Method "POST" -Uri "$($BaseUrl.TrimEnd('/'))/api/v1/sessions/$([Uri]::EscapeDataString($sessionId))/close" -Headers $callerHeaders -Body @{
            sessionId = $sessionId
            reason = "demo-seed cleanup"
        }
        $closedSessionIds.Add($sessionId) | Out-Null
    }
}

$inventory = $null
$endpoints = @()
$readyEndpoint = $null
$timeoutSeconds = [Math]::Max(1, $ReadyEndpointTimeoutSec)
$pollIntervalMs = [Math]::Max(100, $ReadyEndpointPollIntervalMs)
$deadlineUtc = [DateTimeOffset]::UtcNow.AddSeconds($timeoutSeconds)

do {
    $inventory = Invoke-DemoJson -Method "GET" -Uri "$($BaseUrl.TrimEnd('/'))/api/v1/dashboards/inventory" -Headers $callerHeaders
    $endpointsProperty = $inventory.PSObject.Properties["endpoints"]
    $endpoints = if ($null -ne $endpointsProperty) { @($endpointsProperty.Value) } else { @() }

    $readyEndpoint = $endpoints | Where-Object {
        if ($null -eq $_) {
            return $false
        }

        $activeSessionProperty = $_.PSObject.Properties["activeSessionId"]
        $activeSessionId = if ($null -ne $activeSessionProperty) { [string]$activeSessionProperty.Value } else { "" }
        if ([string]::IsNullOrWhiteSpace($activeSessionId)) {
            return $true
        }

        $linkedSession = $sessions | Where-Object {
            $_ -and
            $_.PSObject.Properties["sessionId"] -and
            [string]$_.sessionId -eq $activeSessionId
        } | Select-Object -First 1

        if ($null -eq $linkedSession) {
            # Inventory can reference stale latest-session metadata; if session is absent, treat endpoint as reusable.
            return $true
        }

        $linkedState = Get-NormalizedSessionState -Session $linkedSession
        if ([string]::Equals($linkedState, "Ended", [StringComparison]::OrdinalIgnoreCase)) {
            return $true
        }

        if ([string]::Equals($linkedState, "Failed", [StringComparison]::OrdinalIgnoreCase)) {
            return $true
        }

        return $false
    } | Select-Object -First 1

    if ($null -ne $readyEndpoint) {
        break
    }

    if ([DateTimeOffset]::UtcNow -ge $deadlineUtc) {
        break
    }

    Start-Sleep -Milliseconds $pollIntervalMs
} while ($true)

if ($null -eq $readyEndpoint) {
    $activeSessionCount = Get-ObjectCount -Value $activeSessions
    $sessionsToCloseCount = Get-ObjectCount -Value $sessionsToClose
    $endpointCount = Get-ObjectCount -Value $endpoints
    $endpointStates = @($endpoints | Select-Object -First 5 | ForEach-Object {
        $idProp = $_.PSObject.Properties["endpointId"]
        $sessionProp = $_.PSObject.Properties["activeSessionId"]
        $endpointId = if ($null -ne $idProp) { [string]$idProp.Value } else { "<unknown>" }
        $activeSessionId = if ($null -ne $sessionProp) { [string]$sessionProp.Value } else { "" }
        if ([string]::IsNullOrWhiteSpace($activeSessionId)) { return "${endpointId}:idle" }

        $linkedSession = $sessions | Where-Object {
            $_ -and
            $_.PSObject.Properties["sessionId"] -and
            [string]$_.sessionId -eq $activeSessionId
        } | Select-Object -First 1
        $linkedState = if ($null -eq $linkedSession) { "stale" } else { Get-NormalizedSessionState -Session $linkedSession }
        "${endpointId}:busy(${activeSessionId},state=${linkedState})"
    })

    throw "Demo seed did not find any idle endpoint within $timeoutSeconds second(s). Active sessions in scope: $activeSessionCount; non-terminal sessions in scope: $sessionsToCloseCount; endpoints visible: $endpointCount; endpoint sample: $($endpointStates -join ', ')."
}

Write-Host ""
Write-Host "=== Demo Seed Summary ==="
Write-Host "BaseUrl: $BaseUrl"
Write-Host "Active sessions found: $(Get-ObjectCount -Value $activeSessions)"
Write-Host "Non-terminal sessions found: $(Get-ObjectCount -Value $sessionsToClose)"
Write-Host "Closed sessions: $($closedSessionIds.Count)"
Write-Host "Endpoints visible: $(Get-ObjectCount -Value $endpoints)"
if ($closedSessionIds.Count -gt 0) {
    Write-Host "Closed session IDs: $($closedSessionIds -join ', ')"
}
Write-Host "Ready endpoint: $($readyEndpoint.endpointId)"
Write-Host "Ready endpoint agent: $($readyEndpoint.agentId)"

exit 0

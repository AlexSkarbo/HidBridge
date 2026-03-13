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
    [string]$PrincipalId = "failed-room-cleanup",
    [string]$TenantId = "local-tenant",
    [string]$OrganizationId = "local-org",
    [string]$Reason = "manual failed-room cleanup",
    [switch]$DryRun
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

function Invoke-CleanupJson {
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
    $tokenResponse = Get-KeycloakOidcToken `
        -BaseUrl $KeycloakBaseUrl `
        -RealmName $RealmName `
        -ClientId $TokenClientId `
        -ClientSecret $TokenClientSecret `
        -Scope $TokenScope `
        -Username $TokenUsername `
        -Password $TokenPassword

    if ([string]::IsNullOrWhiteSpace($tokenResponse.access_token)) {
        throw "Failed-room cleanup token response did not contain access_token."
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

$sessionsResponse = Invoke-CleanupJson -Method "GET" -Uri "$($BaseUrl.TrimEnd('/'))/api/v1/sessions" -Headers $callerHeaders
$sessions = if ($null -ne $sessionsResponse -and $null -ne $sessionsResponse.PSObject.Properties["items"]) {
    @($sessionsResponse.items)
}
else {
    @($sessionsResponse)
}

$failedSessions = @(
    $sessions | Where-Object {
        $_ -and
        $_.PSObject.Properties["sessionId"] -and
        [string]::Equals((Get-NormalizedSessionState -Session $_), "Failed", [StringComparison]::OrdinalIgnoreCase)
    }
)

$closedSessionIds = [System.Collections.Generic.List[string]]::new()
$failedClosures = [System.Collections.Generic.List[string]]::new()

if (-not $DryRun) {
    foreach ($session in $failedSessions) {
        $sessionId = [string]$session.sessionId
        if ([string]::IsNullOrWhiteSpace($sessionId)) {
            continue
        }

        try {
            $null = Invoke-CleanupJson `
                -Method "POST" `
                -Uri "$($BaseUrl.TrimEnd('/'))/api/v1/sessions/$([Uri]::EscapeDataString($sessionId))/close" `
                -Headers $callerHeaders `
                -Body @{
                    sessionId = $sessionId
                    reason = $Reason
                }
            $closedSessionIds.Add($sessionId) | Out-Null
        }
        catch {
            $failedClosures.Add("${sessionId}: $($_.Exception.Message)") | Out-Null
        }
    }
}

Write-Host ""
Write-Host "=== Close Failed Rooms Summary ==="
Write-Host "BaseUrl: $BaseUrl"
Write-Host "Sessions visible: $(@($sessions).Count)"
Write-Host "Failed sessions found: $(@($failedSessions).Count)"
Write-Host "Dry run: $($DryRun.IsPresent)"
Write-Host "Closed sessions: $($closedSessionIds.Count)"
Write-Host "Close failures: $($failedClosures.Count)"
if ($failedSessions.Count -gt 0) {
    Write-Host "Failed session IDs: $(@($failedSessions | ForEach-Object { [string]$_.sessionId }) -join ', ')"
}
if ($closedSessionIds.Count -gt 0) {
    Write-Host "Closed session IDs: $($closedSessionIds -join ', ')"
}
if ($failedClosures.Count -gt 0) {
    Write-Host "Close errors:"
    foreach ($failure in $failedClosures) {
        Write-Host "  - $failure"
    }
}

if ($failedClosures.Count -gt 0) {
    exit 1
}

exit 0

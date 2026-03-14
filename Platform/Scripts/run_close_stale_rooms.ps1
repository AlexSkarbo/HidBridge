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
    [string]$PrincipalId = "stale-room-cleanup",
    [string]$TenantId = "local-tenant",
    [string]$OrganizationId = "local-org",
    [string]$Reason = "manual stale-room cleanup",
    [int]$StaleAfterMinutes = 30,
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

function ConvertTo-StringArray {
    param([object]$Value)

    if ($null -eq $Value) {
        return @()
    }

    if ($Value -is [string]) {
        if ([string]::IsNullOrWhiteSpace($Value)) {
            return @()
        }

        return @($Value)
    }

    $values = @($Value)
    if ($values.Count -eq 1 -and $values[0] -is [System.Collections.IEnumerable] -and -not ($values[0] -is [string])) {
        $values = @($values[0])
    }

    return @(
        $values |
            Where-Object { $null -ne $_ -and -not [string]::IsNullOrWhiteSpace([string]$_) } |
            ForEach-Object { [string]$_ }
    )
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
        throw "Stale-room cleanup token response did not contain access_token."
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

if ($StaleAfterMinutes -lt 1) {
    throw "StaleAfterMinutes must be greater than 0."
}

$result = Invoke-CleanupJson `
    -Method "POST" `
    -Uri "$($BaseUrl.TrimEnd('/'))/api/v1/sessions/actions/close-stale" `
    -Headers $callerHeaders `
    -Body @{
        dryRun = $DryRun.IsPresent
        reason = $Reason
        staleAfterMinutes = $StaleAfterMinutes
    }

$sessionsVisible = if ($null -ne $result -and $null -ne $result.PSObject.Properties["scannedSessions"]) {
    [int]$result.scannedSessions
}
else {
    0
}

$matchedSessions = if ($null -ne $result -and $null -ne $result.PSObject.Properties["matchedSessions"]) {
    [int]$result.matchedSessions
}
else {
    0
}

$closedSessions = if ($null -ne $result -and $null -ne $result.PSObject.Properties["closedSessions"]) {
    [int]$result.closedSessions
}
else {
    0
}

$skippedSessions = if ($null -ne $result -and $null -ne $result.PSObject.Properties["skippedSessions"]) {
    [int]$result.skippedSessions
}
else {
    0
}

$failedClosureCount = if ($null -ne $result -and $null -ne $result.PSObject.Properties["failedClosures"]) {
    [int]$result.failedClosures
}
else {
    0
}

$matchedSessionIds = if ($null -ne $result -and $null -ne $result.PSObject.Properties["matchedSessionIds"]) {
    ConvertTo-StringArray -Value $result.matchedSessionIds
}
else {
    @()
}

$closedSessionIds = if ($null -ne $result -and $null -ne $result.PSObject.Properties["closedSessionIds"]) {
    ConvertTo-StringArray -Value $result.closedSessionIds
}
else {
    @()
}

$errors = if ($null -ne $result -and $null -ne $result.PSObject.Properties["errors"]) {
    ConvertTo-StringArray -Value $result.errors
}
else {
    @()
}

Write-Host ""
Write-Host "=== Close Stale Rooms Summary ==="
Write-Host "BaseUrl: $BaseUrl"
Write-Host "Stale after minutes: $StaleAfterMinutes"
Write-Host "Sessions visible: $sessionsVisible"
Write-Host "Stale sessions found: $matchedSessions"
Write-Host "Dry run: $($DryRun.IsPresent)"
Write-Host "Closed sessions: $closedSessions"
Write-Host "Skipped sessions: $skippedSessions"
Write-Host "Close failures: $failedClosureCount"
if (@($matchedSessionIds).Count -gt 0) {
    Write-Host "Stale session IDs: $($matchedSessionIds -join ', ')"
}
if (@($closedSessionIds).Count -gt 0) {
    Write-Host "Closed session IDs: $($closedSessionIds -join ', ')"
}
if (@($errors).Count -gt 0) {
    Write-Host "Close errors:"
    foreach ($failure in $errors) {
        Write-Host "  - $failure"
    }
}

if (@($errors).Count -gt 0) {
    exit 1
}

exit 0

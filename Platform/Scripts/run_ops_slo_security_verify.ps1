param(
    [string]$BaseUrl = "http://127.0.0.1:18093",
    [int]$SloWindowMinutes = 60,
    [string]$AccessToken = "auto",
    [string]$AuthAuthority = "http://host.docker.internal:18096/realms/hidbridge-dev",
    [string]$TokenClientId = "controlplane-smoke",
    [string]$TokenClientSecret = "",
    [string]$TokenScope = "openid profile email",
    [string]$TokenUsername = "operator.smoke.admin",
    [string]$TokenPassword = "ChangeMe123!",
    [switch]$AllowAuthDisabled,
    [switch]$FailOnSloWarning,
    [switch]$FailOnSecurityWarning,
    [switch]$RequireAuditTrailCategories,
    [int]$AuditSampleTake = 250,
    [string]$OutputJsonPath = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$checks = [System.Collections.Generic.List[object]]::new()
$script:tokenAuthorityCandidates = [System.Collections.Generic.List[string]]::new()
$script:tokenAuthorityCursor = 0
$script:resolvedAccessToken = if (
    -not [string]::IsNullOrWhiteSpace($AccessToken) -and
    -not [string]::Equals($AccessToken, "auto", [StringComparison]::OrdinalIgnoreCase) -and
    -not [string]::Equals($AccessToken, "none", [StringComparison]::OrdinalIgnoreCase)
) {
    $AccessToken
}
else {
    $null
}

function Get-HttpStatusCodeFromException {
    param(
        [System.Exception]$Exception
    )

    if ($null -eq $Exception) {
        return $null
    }

    $responseProperty = $Exception.PSObject.Properties["Response"]
    $response = if ($null -ne $responseProperty) { $responseProperty.Value } else { $null }
    if ($null -eq $response) {
        return $null
    }

    $statusCodeProperty = $response.PSObject.Properties["StatusCode"]
    if ($null -eq $statusCodeProperty) {
        return $null
    }

    return [int]$statusCodeProperty.Value
}

function Add-VerificationCheck {
    param(
        [string]$Name,
        [ValidateSet("PASS", "WARN", "FAIL")]
        [string]$Status,
        [string]$Message,
        [object]$Observed = $null,
        [object]$Expected = $null
    )

    $checks.Add([pscustomobject]@{
        Name = $Name
        Status = $Status
        Message = $Message
        Observed = $Observed
        Expected = $Expected
    }) | Out-Null
}

function Initialize-TokenAuthorityCandidates {
    param(
        [string]$PrimaryAuthority
    )

    $candidates = [System.Collections.Generic.List[string]]::new()

    if (-not [string]::IsNullOrWhiteSpace($PrimaryAuthority)) {
        $candidates.Add($PrimaryAuthority.TrimEnd('/')) | Out-Null
    }

    $parsedPrimary = $null
    if ([Uri]::TryCreate($PrimaryAuthority, [UriKind]::Absolute, [ref]$parsedPrimary)) {
        $pathAndQuery = $parsedPrimary.PathAndQuery
        if ([string]::IsNullOrWhiteSpace($pathAndQuery)) {
            $pathAndQuery = "/realms/hidbridge-dev"
        }

        $fallbackHosts = [System.Collections.Generic.List[string]]::new()
        if ([string]::Equals($parsedPrimary.Host, "host.docker.internal", [StringComparison]::OrdinalIgnoreCase)) {
            $fallbackHosts.Add("127.0.0.1") | Out-Null
            $fallbackHosts.Add("localhost") | Out-Null
        }
        elseif ([string]::Equals($parsedPrimary.Host, "127.0.0.1", [StringComparison]::OrdinalIgnoreCase) -or [string]::Equals($parsedPrimary.Host, "localhost", [StringComparison]::OrdinalIgnoreCase)) {
            $fallbackHosts.Add("host.docker.internal") | Out-Null
        }

        # Avoid `$Host` (PowerShell built-in read-only variable) by using `$candidateHost`.
        foreach ($candidateHost in $fallbackHosts) {
            $fallbackBuilder = [UriBuilder]::new($parsedPrimary)
            $fallbackBuilder.Host = $candidateHost
            $fallbackBuilder.Path = $pathAndQuery.TrimStart('/')
            $candidate = $fallbackBuilder.Uri.AbsoluteUri.TrimEnd('/')
            if ($candidates -notcontains $candidate) {
                $candidates.Add($candidate) | Out-Null
            }
        }
    }

    if ($candidates.Count -eq 0) {
        $candidates.Add("http://127.0.0.1:18096/realms/hidbridge-dev") | Out-Null
    }

    $script:tokenAuthorityCandidates = $candidates
    $script:tokenAuthorityCursor = 0
}

function Get-CurrentTokenAuthority {
    if ($script:tokenAuthorityCandidates.Count -eq 0) {
        Initialize-TokenAuthorityCandidates -PrimaryAuthority $AuthAuthority
    }

    $index = [Math]::Max(0, [Math]::Min($script:tokenAuthorityCursor, $script:tokenAuthorityCandidates.Count - 1))
    return $script:tokenAuthorityCandidates[$index]
}

function Move-NextTokenAuthority {
    if ($script:tokenAuthorityCandidates.Count -eq 0) {
        Initialize-TokenAuthorityCandidates -PrimaryAuthority $AuthAuthority
    }

    if ($script:tokenAuthorityCursor -lt ($script:tokenAuthorityCandidates.Count - 1)) {
        $script:tokenAuthorityCursor++
        return $true
    }

    return $false
}

function Get-ResolvedAccessToken {
    if (-not [string]::IsNullOrWhiteSpace($script:resolvedAccessToken)) {
        return $script:resolvedAccessToken
    }

    if ([string]::Equals($AccessToken, "none", [StringComparison]::OrdinalIgnoreCase)) {
        throw "Access token is required for this endpoint, but AccessToken=none disables automatic token acquisition."
    }

    if (
        -not [string]::Equals($AccessToken, "auto", [StringComparison]::OrdinalIgnoreCase) -and
        -not [string]::IsNullOrWhiteSpace($AccessToken)
    ) {
        $script:resolvedAccessToken = $AccessToken
        return $script:resolvedAccessToken
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

    $lastErrorMessage = ""
    do {
        $authorityBase = Get-CurrentTokenAuthority
        $tokenEndpoint = "$authorityBase/protocol/openid-connect/token"

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
                $tokenResponse = Invoke-RestMethod -Method Post -Uri $tokenEndpoint -ContentType "application/x-www-form-urlencoded" -Body $tokenBody -TimeoutSec 30
                if ([string]::IsNullOrWhiteSpace($tokenResponse.access_token)) {
                    throw "Token response did not contain access_token."
                }

                $script:resolvedAccessToken = [string]$tokenResponse.access_token
                return $script:resolvedAccessToken
            }
            catch {
                $errorResponse = ""
                $responseProperty = $_.Exception.PSObject.Properties["Response"]
                if ($null -ne $responseProperty -and $null -ne $responseProperty.Value) {
                    try {
                        $stream = $responseProperty.Value.GetResponseStream()
                        if ($null -ne $stream) {
                            $reader = New-Object System.IO.StreamReader($stream)
                            try {
                                $errorResponse = $reader.ReadToEnd()
                            }
                            finally {
                                $reader.Dispose()
                                $stream.Dispose()
                            }
                        }
                    }
                    catch {
                        # Best-effort error payload extraction.
                    }
                }

                $lastErrorMessage = if ([string]::IsNullOrWhiteSpace($errorResponse)) {
                    "$($_.Exception.Message)"
                }
                else {
                    "$($_.Exception.Message)`n$errorResponse"
                }

                $statusCode = Get-HttpStatusCodeFromException -Exception $_.Exception
                $hasNextScope = -not [string]::Equals($scopeCandidate, $scopeCandidates[$scopeCandidates.Count - 1], [StringComparison]::Ordinal)
                $invalidScope = $lastErrorMessage -match "invalid_scope"
                if ($hasNextScope -and ($invalidScope -or $statusCode -eq 400)) {
                    continue
                }

                break
            }
        }
    }
    while (Move-NextTokenAuthority)

    if ([string]::IsNullOrWhiteSpace($lastErrorMessage)) {
        throw "Keycloak token request failed."
    }

    throw "Keycloak token request failed.`n$lastErrorMessage"
}

function Invoke-ApiJson {
    param(
        [string]$RelativePath
    )

    $uri = "$($BaseUrl.TrimEnd('/'))$RelativePath"
    $tokenAttempted = $false

    while ($true) {
        try {
            if (-not [string]::IsNullOrWhiteSpace($script:resolvedAccessToken)) {
                $headers = @{ Authorization = "Bearer $script:resolvedAccessToken" }
                return Invoke-RestMethod -Method Get -Uri $uri -Headers $headers -TimeoutSec 30
            }

            return Invoke-RestMethod -Method Get -Uri $uri -TimeoutSec 30
        }
        catch {
            $statusCode = Get-HttpStatusCodeFromException -Exception $_.Exception
            $isUnauthorized = $statusCode -eq 401 -or $statusCode -eq 403
            if (-not $isUnauthorized -or $tokenAttempted) {
                if ($tokenAttempted -and (Move-NextTokenAuthority)) {
                    $script:resolvedAccessToken = $null
                    $tokenAttempted = $false
                    continue
                }

                throw
            }

            $null = Get-ResolvedAccessToken
            $tokenAttempted = $true
        }
    }
}

function To-Boolean {
    param([object]$Value)
    return $null -ne $Value -and [System.Convert]::ToBoolean($Value)
}

function Get-ClampedInt {
    param(
        [int]$Value,
        [int]$Min,
        [int]$Max
    )

    return [Math]::Max($Min, [Math]::Min($Max, $Value))
}

$health = $null
$runtime = $null
$slo = $null
$auditEvents = @()

try {
    Initialize-TokenAuthorityCandidates -PrimaryAuthority $AuthAuthority

    $parsedBaseUrl = $null
    if (-not [Uri]::TryCreate($BaseUrl, [UriKind]::Absolute, [ref]$parsedBaseUrl)) {
        throw "BaseUrl must be an absolute URL."
    }

    $effectiveWindowMinutes = Get-ClampedInt -Value $SloWindowMinutes -Min 5 -Max (24 * 60)

    $health = Invoke-RestMethod -Method Get -Uri "$($BaseUrl.TrimEnd('/'))/health" -TimeoutSec 15
    $isHealthy = [string]::Equals([string]$health.status, "ok", [StringComparison]::OrdinalIgnoreCase)
    Add-VerificationCheck `
        -Name "api.health" `
        -Status ($(if ($isHealthy) { "PASS" } else { "FAIL" })) `
        -Message ($(if ($isHealthy) { "API health endpoint is reachable." } else { "API health endpoint returned non-ok status." })) `
        -Observed $health.status `
        -Expected "ok"

    $runtime = Invoke-ApiJson -RelativePath "/api/v1/runtime/uart"
    $slo = Invoke-ApiJson -RelativePath "/api/v1/diagnostics/transport/slo?windowMinutes=$effectiveWindowMinutes"

    $authEnabled = To-Boolean -Value $runtime.authentication.enabled
    if ($authEnabled) {
        Add-VerificationCheck -Name "security.auth.enabled" -Status "PASS" -Message "API authentication is enabled." -Observed $runtime.authentication.enabled -Expected $true
    }
    elseif ($AllowAuthDisabled) {
        Add-VerificationCheck -Name "security.auth.enabled" -Status "WARN" -Message "API authentication is disabled by runtime configuration." -Observed $runtime.authentication.enabled -Expected $true
    }
    else {
        Add-VerificationCheck -Name "security.auth.enabled" -Status "FAIL" -Message "API authentication is disabled and AllowAuthDisabled was not provided." -Observed $runtime.authentication.enabled -Expected $true
    }

    $headerFallbackEnabled = To-Boolean -Value $runtime.authentication.allowHeaderFallback
    if (-not $headerFallbackEnabled) {
        Add-VerificationCheck -Name "security.header-fallback" -Status "PASS" -Message "Header fallback is disabled for API auth." -Observed $runtime.authentication.allowHeaderFallback -Expected $false
    }
    else {
        $status = if ($FailOnSecurityWarning) { "FAIL" } else { "WARN" }
        Add-VerificationCheck -Name "security.header-fallback" -Status $status -Message "Header fallback is enabled; strict bearer posture is not fully enforced." -Observed $runtime.authentication.allowHeaderFallback -Expected $false
    }

    $bearerPrefixes = @($runtime.authentication.bearerOnlyPrefixes)
    $requiredBearerPrefixes = @("/api/v1/sessions", "/api/v1/diagnostics")
    $missingBearerPrefixes = @($requiredBearerPrefixes | Where-Object { $bearerPrefixes -notcontains $_ })
    if ($missingBearerPrefixes.Count -eq 0) {
        Add-VerificationCheck -Name "security.bearer-only-coverage" -Status "PASS" -Message "Bearer-only coverage includes required prefixes." -Observed ($bearerPrefixes -join ", ") -Expected ($requiredBearerPrefixes -join ", ")
    }
    else {
        $status = if ($FailOnSecurityWarning) { "FAIL" } else { "WARN" }
        Add-VerificationCheck -Name "security.bearer-only-coverage" -Status $status -Message "Missing bearer-only prefixes: $($missingBearerPrefixes -join ", ")." -Observed ($bearerPrefixes -join ", ") -Expected ($requiredBearerPrefixes -join ", ")
    }

    $callerContextPrefixes = @($runtime.authentication.callerContextRequiredPrefixes)
    $hasSessionCallerContext = $callerContextPrefixes -contains "/api/v1/sessions"
    if ($hasSessionCallerContext) {
        Add-VerificationCheck -Name "security.caller-context-coverage" -Status "PASS" -Message "Caller-context coverage includes /api/v1/sessions." -Observed ($callerContextPrefixes -join ", ") -Expected "/api/v1/sessions"
    }
    else {
        $status = if ($FailOnSecurityWarning) { "FAIL" } else { "WARN" }
        Add-VerificationCheck -Name "security.caller-context-coverage" -Status $status -Message "Caller-context coverage does not include /api/v1/sessions." -Observed ($callerContextPrefixes -join ", ") -Expected "/api/v1/sessions"
    }

    $fallbackDisabledPatterns = @($runtime.authentication.headerFallbackDisabledPatterns)
    $hasCommandsFallbackPattern = @($fallbackDisabledPatterns | Where-Object { $_ -match "commands" }).Count -gt 0
    $hasControlFallbackPattern = @($fallbackDisabledPatterns | Where-Object { $_ -match "control" }).Count -gt 0
    $hasInvitationsFallbackPattern = @($fallbackDisabledPatterns | Where-Object { $_ -match "invitations" }).Count -gt 0
    if ($hasCommandsFallbackPattern -and $hasControlFallbackPattern -and $hasInvitationsFallbackPattern) {
        Add-VerificationCheck `
            -Name "security.fallback-disabled-patterns" `
            -Status "PASS" `
            -Message "Header-fallback disabled patterns cover commands/control/invitations mutation surfaces." `
            -Observed ($fallbackDisabledPatterns -join ", ") `
            -Expected "contains commands + control + invitations patterns"
    }
    else {
        $status = if ($FailOnSecurityWarning) { "FAIL" } else { "WARN" }
        $missingFallbackPatterns = @()
        if (-not $hasCommandsFallbackPattern) { $missingFallbackPatterns += "commands" }
        if (-not $hasControlFallbackPattern) { $missingFallbackPatterns += "control" }
        if (-not $hasInvitationsFallbackPattern) { $missingFallbackPatterns += "invitations" }
        Add-VerificationCheck `
            -Name "security.fallback-disabled-patterns" `
            -Status $status `
            -Message "Missing fallback-disabled pattern coverage: $($missingFallbackPatterns -join ", ")." `
            -Observed ($fallbackDisabledPatterns -join ", ") `
            -Expected "contains commands + control + invitations patterns"
    }

    $sloStatus = [string]$slo.status
    if ([string]::Equals($sloStatus, "critical", [StringComparison]::OrdinalIgnoreCase)) {
        Add-VerificationCheck -Name "slo.overall-status" -Status "FAIL" -Message "Transport SLO status is critical." -Observed $sloStatus -Expected "healthy|warning"
    }
    elseif ([string]::Equals($sloStatus, "warning", [StringComparison]::OrdinalIgnoreCase)) {
        $status = if ($FailOnSloWarning) { "FAIL" } else { "WARN" }
        Add-VerificationCheck -Name "slo.overall-status" -Status $status -Message "Transport SLO status is warning." -Observed $sloStatus -Expected "healthy"
    }
    else {
        Add-VerificationCheck -Name "slo.overall-status" -Status "PASS" -Message "Transport SLO status is healthy." -Observed $sloStatus -Expected "healthy"
    }

    $ackTimeoutCritical = To-Boolean -Value $slo.breaches.ackTimeoutRateCritical
    if ($ackTimeoutCritical) {
        Add-VerificationCheck -Name "slo.ack-timeout-rate" -Status "FAIL" -Message "ACK timeout rate crossed critical threshold." -Observed $slo.ackTimeoutRate -Expected "< $($slo.thresholds.ackTimeoutRateCritical)"
    }
    else {
        $ackWarn = To-Boolean -Value $slo.breaches.ackTimeoutRateWarn
        if ($ackWarn) {
            $status = if ($FailOnSloWarning) { "FAIL" } else { "WARN" }
            Add-VerificationCheck -Name "slo.ack-timeout-rate" -Status $status -Message "ACK timeout rate crossed warning threshold." -Observed $slo.ackTimeoutRate -Expected "< $($slo.thresholds.ackTimeoutRateWarn)"
        }
        else {
            Add-VerificationCheck -Name "slo.ack-timeout-rate" -Status "PASS" -Message "ACK timeout rate is within threshold." -Observed $slo.ackTimeoutRate -Expected "< $($slo.thresholds.ackTimeoutRateWarn)"
        }
    }

    $relayLatencyCritical = To-Boolean -Value $slo.breaches.relayReadyLatencyP95Critical
    if ($relayLatencyCritical) {
        Add-VerificationCheck -Name "slo.relay-ready-latency-p95" -Status "FAIL" -Message "Relay-ready p95 latency crossed critical threshold." -Observed $slo.relayReadyLatencyP95Ms -Expected "< $($slo.thresholds.relayReadyLatencyCriticalMs)"
    }
    else {
        $relayLatencyWarn = To-Boolean -Value $slo.breaches.relayReadyLatencyP95Warn
        if ($relayLatencyWarn) {
            $status = if ($FailOnSloWarning) { "FAIL" } else { "WARN" }
            Add-VerificationCheck -Name "slo.relay-ready-latency-p95" -Status $status -Message "Relay-ready p95 latency crossed warning threshold." -Observed $slo.relayReadyLatencyP95Ms -Expected "< $($slo.thresholds.relayReadyLatencyWarnMs)"
        }
        else {
            Add-VerificationCheck -Name "slo.relay-ready-latency-p95" -Status "PASS" -Message "Relay-ready p95 latency is within threshold." -Observed $slo.relayReadyLatencyP95Ms -Expected "< $($slo.thresholds.relayReadyLatencyWarnMs)"
        }
    }

    $reconnectCritical = To-Boolean -Value $slo.breaches.reconnectFrequencyCriticalPerHour
    if ($reconnectCritical) {
        Add-VerificationCheck -Name "slo.reconnect-frequency" -Status "FAIL" -Message "Reconnect frequency crossed critical threshold." -Observed $slo.reconnectFrequencyPerHour -Expected "< $($slo.thresholds.reconnectFrequencyCriticalPerHour)"
    }
    else {
        $reconnectWarn = To-Boolean -Value $slo.breaches.reconnectFrequencyWarnPerHour
        if ($reconnectWarn) {
            $status = if ($FailOnSloWarning) { "FAIL" } else { "WARN" }
            Add-VerificationCheck -Name "slo.reconnect-frequency" -Status $status -Message "Reconnect frequency crossed warning threshold." -Observed $slo.reconnectFrequencyPerHour -Expected "< $($slo.thresholds.reconnectFrequencyWarnPerHour)"
        }
        else {
            Add-VerificationCheck -Name "slo.reconnect-frequency" -Status "PASS" -Message "Reconnect frequency is within threshold." -Observed $slo.reconnectFrequencyPerHour -Expected "< $($slo.thresholds.reconnectFrequencyWarnPerHour)"
        }
    }

    $auditEvents = @(Invoke-ApiJson -RelativePath "/api/v1/events/audit")
    if ($auditEvents.Count -eq 0) {
        $status = if ($RequireAuditTrailCategories) { "FAIL" } elseif ($FailOnSecurityWarning) { "FAIL" } else { "WARN" }
        Add-VerificationCheck -Name "security.audit-trail-presence" -Status $status -Message "Audit event stream is empty." -Observed 0 -Expected "> 0"
    }
    else {
        $sampleSize = Get-ClampedInt -Value $AuditSampleTake -Min 1 -Max 2000
        $sampledEvents = $auditEvents |
            Sort-Object -Property createdAtUtc -Descending |
            Select-Object -First $sampleSize
        $sampledCategories = @(
            $sampledEvents |
                ForEach-Object { [string]$_.category } |
                Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
                Select-Object -Unique
        )

        $hasCommandCategory = $sampledCategories -contains "session.command"
        $hasAckCategory = $sampledCategories -contains "transport.ack"
        $hasLeaseCategory = @($sampledCategories | Where-Object { $_ -like "session.control*" }).Count -gt 0

        if ($hasCommandCategory -and $hasAckCategory -and $hasLeaseCategory) {
            Add-VerificationCheck `
                -Name "security.audit-trail-categories" `
                -Status "PASS" `
                -Message "Audit stream contains command/lease/ack categories." `
                -Observed ($sampledCategories -join ", ") `
                -Expected "session.command + session.control* + transport.ack"
        }
        else {
            $status = if ($RequireAuditTrailCategories) { "FAIL" } elseif ($FailOnSecurityWarning) { "FAIL" } else { "WARN" }
            $missingParts = @()
            if (-not $hasCommandCategory) { $missingParts += "session.command" }
            if (-not $hasLeaseCategory) { $missingParts += "session.control*" }
            if (-not $hasAckCategory) { $missingParts += "transport.ack" }
            Add-VerificationCheck `
                -Name "security.audit-trail-categories" `
                -Status $status `
                -Message "Audit stream is missing categories: $($missingParts -join ", ")." `
                -Observed ($sampledCategories -join ", ") `
                -Expected "session.command + session.control* + transport.ack"
        }
    }
}
catch {
    Add-VerificationCheck -Name "ops.verify.exception" -Status "FAIL" -Message $_.Exception.Message
}

$failedChecks = @($checks | Where-Object { $_.Status -eq "FAIL" })
$warningChecks = @($checks | Where-Object { $_.Status -eq "WARN" })
$resultStatus = if ($failedChecks.Count -gt 0) { "FAIL" } elseif ($warningChecks.Count -gt 0) { "WARN" } else { "PASS" }

$summary = [pscustomobject]@{
    generatedAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
    baseUrl = $BaseUrl
    status = $resultStatus
    checkCounts = [pscustomobject]@{
        pass = @($checks | Where-Object { $_.Status -eq "PASS" }).Count
        warn = $warningChecks.Count
        fail = $failedChecks.Count
    }
    slo = [pscustomobject]@{
        status = if ($null -ne $slo) { $slo.status } else { $null }
        relayCommandCount = if ($null -ne $slo) { $slo.relayCommandCount } else { $null }
        relayTimeoutCount = if ($null -ne $slo) { $slo.relayTimeoutCount } else { $null }
        ackTimeoutRate = if ($null -ne $slo) { $slo.ackTimeoutRate } else { $null }
        relayReadyLatencyP95Ms = if ($null -ne $slo) { $slo.relayReadyLatencyP95Ms } else { $null }
        reconnectFrequencyPerHour = if ($null -ne $slo) { $slo.reconnectFrequencyPerHour } else { $null }
    }
    security = [pscustomobject]@{
        authEnabled = if ($null -ne $runtime) { $runtime.authentication.enabled } else { $null }
        allowHeaderFallback = if ($null -ne $runtime) { $runtime.authentication.allowHeaderFallback } else { $null }
        bearerOnlyPrefixes = if ($null -ne $runtime) { @($runtime.authentication.bearerOnlyPrefixes) } else { @() }
        callerContextRequiredPrefixes = if ($null -ne $runtime) { @($runtime.authentication.callerContextRequiredPrefixes) } else { @() }
        headerFallbackDisabledPatterns = if ($null -ne $runtime) { @($runtime.authentication.headerFallbackDisabledPatterns) } else { @() }
        auditEventCount = $auditEvents.Count
    }
    checks = $checks
}

if (-not [string]::IsNullOrWhiteSpace($OutputJsonPath)) {
    $parentDirectory = Split-Path -Parent $OutputJsonPath
    if (-not [string]::IsNullOrWhiteSpace($parentDirectory)) {
        New-Item -ItemType Directory -Force -Path $parentDirectory | Out-Null
    }

    $summary | ConvertTo-Json -Depth 12 | Set-Content -Path $OutputJsonPath -Encoding UTF8
}

Write-Host ""
Write-Host "=== Ops SLO + Security Verify ==="
Write-Host "Base URL: $BaseUrl"
Write-Host "Status:   $resultStatus"
if (-not [string]::IsNullOrWhiteSpace($OutputJsonPath)) {
    Write-Host "Summary:  $OutputJsonPath"
}
Write-Host ""
$checks | Select-Object Name, Status, Observed, Expected, Message | Format-Table -AutoSize

if ($failedChecks.Count -gt 0) {
    exit 1
}

exit 0

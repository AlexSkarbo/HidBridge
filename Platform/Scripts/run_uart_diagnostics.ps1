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
    [string]$PrincipalId = "uart-diagnostics",
    [string]$TenantId = "local-tenant",
    [string]$OrganizationId = "local-org",
    [string]$PreferredEndpointId = "endpoint_local_demo",
    [string]$Profile = "UltraLowLatency",
    [int]$LeaseSeconds = 60,
    [int]$CommandTimeoutMs = 500,
    [int[]]$InterfaceSelectors = @(0, 1, 2, 3, 4, 5, 6, 7, 8),
    [string]$InterfaceSelectorsCsv = "",
    [string]$KeyboardShortcut = "CTRL+ALT+M",
    [string]$KeyboardText = "uart-diag",
    [int]$MouseDx = 8,
    [int]$MouseDy = 4,
    [int]$MouseWheel = 0,
    [string]$OutputJsonPath = ""
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

function Invoke-UartDiagJson {
    param(
        [string]$Method,
        [string]$Uri,
        [object]$Body = $null,
        [hashtable]$Headers = @{}
    )

    try {
        if ($null -eq $Body) {
            return Invoke-RestMethod -Method $Method -Uri $Uri -Headers $Headers -TimeoutSec 30
        }

        $json = $Body | ConvertTo-Json -Depth 20
        return Invoke-RestMethod -Method $Method -Uri $Uri -Headers $Headers -ContentType "application/json" -Body $json -TimeoutSec 30
    }
    catch {
        $responseBody = Get-ErrorResponseBody $_.Exception
        if ([string]::IsNullOrWhiteSpace($responseBody)) {
            throw "HTTP $Method $Uri failed: $($_.Exception.Message)"
        }

        throw "HTTP $Method $Uri failed: $($_.Exception.Message)`n$responseBody"
    }
}

function Get-ItemsArray {
    param([object]$Response)

    if ($null -eq $Response) {
        return @()
    }

    $itemsProperty = $Response.PSObject.Properties["items"]
    if ($null -ne $itemsProperty -and $null -ne $itemsProperty.Value) {
        return @($itemsProperty.Value)
    }

    return @($Response)
}

function Get-ObjectPropertyValue {
    param(
        [object]$InputObject,
        [string]$Name
    )

    if ($null -eq $InputObject -or [string]::IsNullOrWhiteSpace($Name)) {
        return $null
    }

    foreach ($property in $InputObject.PSObject.Properties) {
        if ([string]::Equals($property.Name, $Name, [StringComparison]::OrdinalIgnoreCase)) {
            return $property.Value
        }
    }

    return $null
}

function Get-ObjectPropertyString {
    param(
        [object]$InputObject,
        [string]$Name
    )

    $value = Get-ObjectPropertyValue -InputObject $InputObject -Name $Name
    if ($null -eq $value) {
        return ""
    }

    return [string]$value
}

function Get-NormalizedSessionState {
    param([object]$Session)

    return Get-ObjectPropertyString -InputObject $Session -Name "state"
}

function Test-TerminalSessionState {
    param([string]$State)

    return [string]::Equals($State, "Ended", [StringComparison]::OrdinalIgnoreCase) -or
        [string]::Equals($State, "Failed", [StringComparison]::OrdinalIgnoreCase)
}

function Build-SessionId {
    param([string]$EndpointId)

    $endpointSegment = $EndpointId.Replace("_", "-").Replace(" ", "-").ToLowerInvariant()
    $sessionId = "uart-diag-$endpointSegment-$([Guid]::NewGuid().ToString('N'))"
    if ($sessionId.Length -le 64) {
        return $sessionId
    }

    return $sessionId.Substring(0, 64)
}

function Normalize-SelectorList {
    param([int[]]$Candidates)

    $normalized = [System.Collections.Generic.List[int]]::new()
    foreach ($candidate in $Candidates) {
        if ($candidate -ge 0 -and $candidate -le 255) {
            $normalized.Add($candidate) | Out-Null
        }
    }

    return @($normalized.ToArray() | Select-Object -Unique)
}

function Test-EndpointReusable {
    param(
        [object]$EndpointCard,
        [object[]]$Sessions
    )

    if ($null -eq $EndpointCard) {
        return $false
    }

    $activeSessionId = Get-ObjectPropertyString -InputObject $EndpointCard -Name "activeSessionId"
    if ([string]::IsNullOrWhiteSpace($activeSessionId)) {
        return $true
    }

    $linkedSession = @($Sessions | Where-Object {
            $_ -and [string]::Equals((Get-ObjectPropertyString -InputObject $_ -Name "sessionId"), $activeSessionId, [StringComparison]::OrdinalIgnoreCase)
        } | Select-Object -First 1)
    if ($linkedSession.Count -eq 0) {
        return $true
    }

    return Test-TerminalSessionState -State (Get-NormalizedSessionState -Session $linkedSession[0])
}

function Find-ReusableEndpointCard {
    param(
        [object[]]$Endpoints,
        [object[]]$Sessions,
        [string]$PreferredEndpointId
    )

    $ordered = @($Endpoints)
    if (-not [string]::IsNullOrWhiteSpace($PreferredEndpointId)) {
        $preferred = @($ordered | Where-Object { [string]::Equals((Get-ObjectPropertyString -InputObject $_ -Name "endpointId"), $PreferredEndpointId, [StringComparison]::OrdinalIgnoreCase) })
        $others = @($ordered | Where-Object { -not [string]::Equals((Get-ObjectPropertyString -InputObject $_ -Name "endpointId"), $PreferredEndpointId, [StringComparison]::OrdinalIgnoreCase) })
        $ordered = @($preferred + $others)
    }

    foreach ($endpoint in $ordered) {
        if (Test-EndpointReusable -EndpointCard $endpoint -Sessions $Sessions) {
            return $endpoint
        }
    }

    return $null
}

function Convert-SelectorCsv {
    param([string]$Csv)

    if ([string]::IsNullOrWhiteSpace($Csv)) {
        return @()
    }

    $parsed = [System.Collections.Generic.List[int]]::new()
    $tokens = $Csv -split '[,\s;]+' | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    foreach ($token in $tokens) {
        $value = 0
        if (-not [int]::TryParse($token, [ref]$value)) {
            throw "Invalid interface selector token '$token' in InterfaceSelectorsCsv."
        }

        $parsed.Add($value) | Out-Null
    }

    return @($parsed.ToArray())
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
        throw "UART diagnostics token response did not contain access_token."
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

$base = $BaseUrl.TrimEnd("/")
$runtimeInfo = $null
$createdSessionId = $null
$readyEndpoint = $null
$readyAgentId = $null
$effectivePrincipalId = $PrincipalId
$closeResult = $null
$preflightClosedSessionIds = [System.Collections.Generic.List[string]]::new()
$preflightCloseFailures = [System.Collections.Generic.List[string]]::new()
$preflightCleanupAttempted = $false
$probeResults = [System.Collections.Generic.List[object]]::new()
$scenarios = @(
    [pscustomobject]@{
        Name = "keyboard.shortcut"
        Action = "keyboard.shortcut"
        Channel = "Hid"
    },
    [pscustomobject]@{
        Name = "keyboard.text"
        Action = "keyboard.text"
        Channel = "Hid"
    },
    [pscustomobject]@{
        Name = "mouse.move"
        Action = "mouse.move"
        Channel = "Hid"
    },
    [pscustomobject]@{
        Name = "keyboard.reset"
        Action = "keyboard.reset"
        Channel = "Hid"
    }
)
$effectiveInterfaceSelectors = @($InterfaceSelectors)
if (-not [string]::IsNullOrWhiteSpace($InterfaceSelectorsCsv)) {
    $effectiveInterfaceSelectors = Convert-SelectorCsv -Csv $InterfaceSelectorsCsv
}

$selectors = Normalize-SelectorList -Candidates $effectiveInterfaceSelectors
if ($selectors.Count -eq 0) {
    throw "InterfaceSelectors did not contain any valid values between 0 and 255."
}

try {
    $null = Invoke-UartDiagJson -Method "GET" -Uri "$base/health" -Headers $callerHeaders
    $runtimeInfo = Invoke-UartDiagJson -Method "GET" -Uri "$base/api/v1/runtime/uart" -Headers $callerHeaders

    $sessions = Get-ItemsArray -Response (Invoke-UartDiagJson -Method "GET" -Uri "$base/api/v1/sessions" -Headers $callerHeaders)
    $inventory = Invoke-UartDiagJson -Method "GET" -Uri "$base/api/v1/dashboards/inventory" -Headers $callerHeaders
    $endpoints = if ($null -ne $inventory.PSObject.Properties["endpoints"]) { @($inventory.endpoints) } else { @() }

    $readyEndpointCard = Find-ReusableEndpointCard -Endpoints $endpoints -Sessions $sessions -PreferredEndpointId $PreferredEndpointId

    if ($null -eq $readyEndpointCard) {
        $preflightCleanupAttempted = $true
        $busySessions = @($sessions | Where-Object {
                $_ -and
                -not [string]::IsNullOrWhiteSpace((Get-ObjectPropertyString -InputObject $_ -Name "sessionId")) -and
                -not (Test-TerminalSessionState -State (Get-NormalizedSessionState -Session $_))
            })

        foreach ($busySession in $busySessions) {
            $busySessionId = Get-ObjectPropertyString -InputObject $busySession -Name "sessionId"
            try {
                $null = Invoke-UartDiagJson -Method "POST" -Uri "$base/api/v1/sessions/$([Uri]::EscapeDataString($busySessionId))/close" -Headers $callerHeaders -Body @{
                    sessionId = $busySessionId
                    reason = "uart diagnostics preflight cleanup"
                }
                $preflightClosedSessionIds.Add($busySessionId) | Out-Null
            }
            catch {
                $preflightCloseFailures.Add(("{0}: {1}" -f $busySessionId, $_.Exception.Message)) | Out-Null
            }
        }

        Start-Sleep -Milliseconds 300
        $sessions = Get-ItemsArray -Response (Invoke-UartDiagJson -Method "GET" -Uri "$base/api/v1/sessions" -Headers $callerHeaders)
        $inventory = Invoke-UartDiagJson -Method "GET" -Uri "$base/api/v1/dashboards/inventory" -Headers $callerHeaders
        $endpoints = if ($null -ne $inventory.PSObject.Properties["endpoints"]) { @($inventory.endpoints) } else { @() }
        $readyEndpointCard = Find-ReusableEndpointCard -Endpoints $endpoints -Sessions $sessions -PreferredEndpointId $PreferredEndpointId
    }

    if ($null -eq $readyEndpointCard) {
        $endpointStates = @($endpoints | ForEach-Object {
                $eid = Get-ObjectPropertyString -InputObject $_ -Name "endpointId"
                $asid = Get-ObjectPropertyString -InputObject $_ -Name "activeSessionId"
                $status = Get-ObjectPropertyString -InputObject $_ -Name "status"
                if ([string]::IsNullOrWhiteSpace($eid)) { return $null }
                return ("{0} (status={1}, activeSessionId={2})" -f $eid, $status, $asid)
            } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
        throw "UART diagnostics did not find a reusable endpoint. Endpoints: $($endpointStates -join '; '). Preflight closed sessions: $($preflightClosedSessionIds.Count). Preflight close failures: $($preflightCloseFailures.Count)."
    }

    $readyEndpoint = Get-ObjectPropertyString -InputObject $readyEndpointCard -Name "endpointId"
    $readyAgentId = Get-ObjectPropertyString -InputObject $readyEndpointCard -Name "agentId"
    if ([string]::IsNullOrWhiteSpace($readyEndpoint) -or [string]::IsNullOrWhiteSpace($readyAgentId)) {
        throw "UART diagnostics selected endpoint card without endpointId/agentId."
    }

    $createdSessionId = Build-SessionId -EndpointId $readyEndpoint
    $openedSession = Invoke-UartDiagJson -Method "POST" -Uri "$base/api/v1/sessions" -Headers $callerHeaders -Body @{
        sessionId = $createdSessionId
        profile = $Profile
        requestedBy = $PrincipalId
        targetAgentId = $readyAgentId
        targetEndpointId = $readyEndpoint
        shareMode = "Owner"
        tenantId = $TenantId
        organizationId = $OrganizationId
    }

    $openedRequestedBy = Get-ObjectPropertyString -InputObject $openedSession -Name "requestedBy"
    if (-not [string]::IsNullOrWhiteSpace($openedRequestedBy)) {
        $effectivePrincipalId = $openedRequestedBy
    }

    $ownerParticipantId = "owner:$effectivePrincipalId"
    $null = Invoke-UartDiagJson -Method "POST" -Uri "$base/api/v1/sessions/$([Uri]::EscapeDataString($createdSessionId))/control/request" -Headers $callerHeaders -Body @{
        participantId = $ownerParticipantId
        requestedBy = $effectivePrincipalId
        leaseSeconds = $LeaseSeconds
        reason = "uart diagnostics control lease"
        tenantId = $TenantId
        organizationId = $OrganizationId
    }

    foreach ($selector in $selectors) {
        foreach ($scenario in $scenarios) {
            $commandId = "cmd-uartdiag-$([Guid]::NewGuid().ToString('N').Substring(0, 12))"
            $commandArgs = @{
                principalId = $effectivePrincipalId
                participantId = $ownerParticipantId
                itfSel = [int]$selector
            }

            switch ($scenario.Name) {
                "keyboard.shortcut" {
                    $commandArgs["shortcut"] = $KeyboardShortcut
                    $commandArgs["keys"] = $KeyboardShortcut
                }
                "keyboard.text" {
                    $commandArgs["text"] = $KeyboardText
                }
                "mouse.move" {
                    $commandArgs["dx"] = $MouseDx
                    $commandArgs["dy"] = $MouseDy
                    $commandArgs["wheel"] = $MouseWheel
                }
                "keyboard.reset" {
                }
                default {
                    throw "Unsupported diagnostics scenario '$($scenario.Name)'."
                }
            }

            $ackStatus = ""
            $ackErrorCode = ""
            $ackErrorMessage = ""
            $journalStatus = ""
            $journalErrorCode = ""
            $journalErrorMessage = ""
            $httpFailure = $null

            try {
                $ack = Invoke-UartDiagJson -Method "POST" -Uri "$base/api/v1/sessions/$([Uri]::EscapeDataString($createdSessionId))/commands" -Headers $callerHeaders -Body @{
                    commandId = $commandId
                    sessionId = $createdSessionId
                    channel = $scenario.Channel
                    action = $scenario.Action
                    args = $commandArgs
                    timeoutMs = $CommandTimeoutMs
                    idempotencyKey = "idem-uartdiag-$commandId"
                    tenantId = $TenantId
                    organizationId = $OrganizationId
                }

                $ackStatus = Get-ObjectPropertyString -InputObject $ack -Name "status"
                $ackError = Get-ObjectPropertyValue -InputObject $ack -Name "error"
                $ackErrorCode = Get-ObjectPropertyString -InputObject $ackError -Name "code"
                $ackErrorMessage = Get-ObjectPropertyString -InputObject $ackError -Name "message"

                $journalEntry = $null
                for ($attempt = 0; $attempt -lt 12; $attempt++) {
                    $journalResponse = Invoke-UartDiagJson -Method "GET" -Uri "$base/api/v1/sessions/$([Uri]::EscapeDataString($createdSessionId))/commands/journal" -Headers $callerHeaders
                    $journalEntries = Get-ItemsArray -Response $journalResponse
                    $journalEntry = $journalEntries | Where-Object {
                        [string]::Equals((Get-ObjectPropertyString -InputObject $_ -Name "commandId"), $commandId, [StringComparison]::OrdinalIgnoreCase)
                    } | Select-Object -First 1

                    if ($null -ne $journalEntry) {
                        break
                    }

                    Start-Sleep -Milliseconds 200
                }

                if ($null -ne $journalEntry) {
                    $journalStatus = Get-ObjectPropertyString -InputObject $journalEntry -Name "status"
                    $journalError = Get-ObjectPropertyValue -InputObject $journalEntry -Name "error"
                    $journalErrorCode = Get-ObjectPropertyString -InputObject $journalError -Name "code"
                    $journalErrorMessage = Get-ObjectPropertyString -InputObject $journalError -Name "message"
                }
            }
            catch {
                $httpFailure = $_.Exception.Message
            }

            $probeResults.Add([pscustomobject]@{
                InterfaceSelector = [int]$selector
                Scenario = $scenario.Name
                Action = $scenario.Action
                CommandId = $commandId
                AckStatus = if ($null -eq $httpFailure) { $ackStatus } else { "HttpError" }
                AckErrorCode = if ($null -eq $httpFailure) { $ackErrorCode } else { "HTTP_ERROR" }
                AckErrorMessage = if ($null -eq $httpFailure) { $ackErrorMessage } else { $httpFailure }
                JournalStatus = $journalStatus
                JournalErrorCode = $journalErrorCode
                JournalErrorMessage = $journalErrorMessage
            }) | Out-Null
        }
    }
}
finally {
    if (-not [string]::IsNullOrWhiteSpace($createdSessionId)) {
        try {
            $closeResult = Invoke-UartDiagJson -Method "POST" -Uri "$base/api/v1/sessions/$([Uri]::EscapeDataString($createdSessionId))/close" -Headers $callerHeaders -Body @{
                sessionId = $createdSessionId
                reason = "uart diagnostics cleanup"
            }
        }
        catch {
            Write-Warning ("UART diagnostics cleanup failed for session {0}: {1}" -f $createdSessionId, $_.Exception.Message)
        }
    }
}

$total = $probeResults.Count
$successCount = @($probeResults | Where-Object { [string]::Equals($_.AckStatus, "Applied", [StringComparison]::OrdinalIgnoreCase) -or [string]::Equals($_.AckStatus, "Accepted", [StringComparison]::OrdinalIgnoreCase) }).Count
$timeoutCount = @($probeResults | Where-Object { [string]::Equals($_.AckStatus, "Timeout", [StringComparison]::OrdinalIgnoreCase) }).Count
$rejectedCount = @($probeResults | Where-Object { [string]::Equals($_.AckStatus, "Rejected", [StringComparison]::OrdinalIgnoreCase) }).Count
$httpErrorCount = @($probeResults | Where-Object { [string]::Equals($_.AckStatus, "HttpError", [StringComparison]::OrdinalIgnoreCase) }).Count
$selectorSummary = @(
    $probeResults |
        Group-Object InterfaceSelector |
        ForEach-Object {
            $groupItems = @($_.Group)
            [pscustomobject]@{
                InterfaceSelector = [int]$_.Name
                Probes = $groupItems.Count
                Success = @($groupItems | Where-Object { [string]::Equals($_.AckStatus, "Applied", [StringComparison]::OrdinalIgnoreCase) -or [string]::Equals($_.AckStatus, "Accepted", [StringComparison]::OrdinalIgnoreCase) }).Count
                Timeouts = @($groupItems | Where-Object { [string]::Equals($_.AckStatus, "Timeout", [StringComparison]::OrdinalIgnoreCase) }).Count
                Rejected = @($groupItems | Where-Object { [string]::Equals($_.AckStatus, "Rejected", [StringComparison]::OrdinalIgnoreCase) }).Count
                HttpErrors = @($groupItems | Where-Object { [string]::Equals($_.AckStatus, "HttpError", [StringComparison]::OrdinalIgnoreCase) }).Count
            }
        } |
        Sort-Object `
            @{ Expression = { $_.Success }; Descending = $true }, `
            @{ Expression = { $_.InterfaceSelector }; Descending = $false }
)
$bestSelector = $selectorSummary | Select-Object -First 1

$summary = [ordered]@{
    baseUrl = $BaseUrl
    runtime = $runtimeInfo
    readyEndpoint = $readyEndpoint
    readyAgentId = $readyAgentId
    sessionId = $createdSessionId
    selectors = $selectors
    preferredEndpointId = $PreferredEndpointId
    scenarios = @($scenarios | ForEach-Object { $_.Name })
    totalProbes = $total
    successProbes = $successCount
    timeoutProbes = $timeoutCount
    rejectedProbes = $rejectedCount
    httpErrorProbes = $httpErrorCount
    bestSelector = if ($null -ne $bestSelector) { $bestSelector.InterfaceSelector } else { $null }
    preflightCleanupAttempted = $preflightCleanupAttempted
    preflightClosedSessions = @($preflightClosedSessionIds)
    preflightCloseFailures = @($preflightCloseFailures)
    closeCompleted = $null -ne $closeResult
    selectorSummary = $selectorSummary
    probes = @($probeResults)
}

if (-not [string]::IsNullOrWhiteSpace($OutputJsonPath)) {
    $directory = Split-Path -Parent $OutputJsonPath
    if (-not [string]::IsNullOrWhiteSpace($directory)) {
        New-Item -ItemType Directory -Force -Path $directory | Out-Null
    }

    $summary | ConvertTo-Json -Depth 20 | Set-Content -Path $OutputJsonPath -Encoding UTF8
}

Write-Host ""
Write-Host "=== UART Diagnostics Summary ==="
Write-Host "BaseUrl: $BaseUrl"
Write-Host "Runtime port/baud: $($runtimeInfo.port) / $($runtimeInfo.baudRate)"
Write-Host "Runtime keyboard selector: $($runtimeInfo.keyboardSelector)"
Write-Host "Runtime timeouts/retries: command=$($runtimeInfo.uartCommandTimeoutMs), inject=$($runtimeInfo.uartInjectTimeoutMs), retries=$($runtimeInfo.uartInjectRetries)"
Write-Host "Ready endpoint: $readyEndpoint"
Write-Host "Ready endpoint agent: $readyAgentId"
Write-Host "Diagnostics session: $createdSessionId"
Write-Host "Preflight cleanup attempted: $($summary.preflightCleanupAttempted)"
Write-Host "Preflight closed sessions: $(@($summary.preflightClosedSessions).Count)"
Write-Host "Preflight close failures: $(@($summary.preflightCloseFailures).Count)"
Write-Host "Selectors: $($selectors -join ', ')"
Write-Host "Scenarios: $(@($scenarios | ForEach-Object { $_.Name }) -join ', ')"
Write-Host "Total probes: $total"
Write-Host "Success probes: $successCount"
Write-Host "Timeout probes: $timeoutCount"
Write-Host "Rejected probes: $rejectedCount"
Write-Host "HTTP error probes: $httpErrorCount"
Write-Host "Best selector: $($summary.bestSelector)"
Write-Host "Cleanup close: $($summary.closeCompleted)"
if (-not [string]::IsNullOrWhiteSpace($OutputJsonPath)) {
    Write-Host "Summary JSON: $OutputJsonPath"
}

Write-Host ""
Write-Host "Selector summary:"
$selectorSummary | Format-Table InterfaceSelector, Probes, Success, Timeouts, Rejected, HttpErrors -AutoSize

Write-Host ""
Write-Host "Probe matrix:"
$probeResults | Select-Object InterfaceSelector, Scenario, AckStatus, AckErrorCode | Format-Table -AutoSize

if ($successCount -eq 0) {
    exit 1
}

exit 0

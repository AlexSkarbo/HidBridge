param(
    [string]$BaseUrl = "http://127.0.0.1:18093",
    [ValidateSet("uart", "webrtc-datachannel")]
    [string]$TransportProvider = "uart",
    [string]$KeycloakBaseUrl = "http://127.0.0.1:18096",
    [string]$RealmName = "hidbridge-dev",
    [string]$TokenClientId = "controlplane-smoke",
    [string]$TokenClientSecret = "",
    [string]$TokenScope = "openid profile email",
    [string]$TokenUsername = "operator.smoke.admin",
    [string]$TokenPassword = "ChangeMe123!",
    [string]$AccessToken = "",
    [string]$PrincipalId = "demo-gate-admin",
    [string]$TenantId = "local-tenant",
    [string]$OrganizationId = "local-org",
    [string]$Profile = "UltraLowLatency",
    [int]$LeaseSeconds = 30,
    [int]$CommandTimeoutMs = 500,
    [switch]$RequireDeviceAck,
    [int]$KeyboardInterfaceSelector = -1,
    [int[]]$KeyboardInterfaceCandidates = @(0, 1, 2, 3, 4, 5, 6, 7, 8),
    [string]$ControlHealthUrl = "http://127.0.0.1:28092/health",
    [int]$RequestTimeoutSec = 15,
    [int]$ControlHealthAttempts = 20,
    [switch]$SkipTransportHealthCheck,
    [int]$TransportHealthAttempts = 20,
    [int]$TransportHealthDelayMs = 500,
    [int]$ReadyEndpointTimeoutSec = 60,
    [int]$ReadyEndpointPollIntervalMs = 500,
    [string]$OutputJsonPath = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$scriptsRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $scriptsRoot "Common/KeycloakCommon.ps1")

if ([string]::Equals($TransportProvider, "webrtc-datachannel", [StringComparison]::OrdinalIgnoreCase)) {
    if ($RequireDeviceAck) {
        Write-Warning "RequireDeviceAck is ignored for TransportProvider=webrtc-datachannel."
    }

    $webrtcSmokeScript = Join-Path $scriptsRoot "run_webrtc_edge_agent_smoke.ps1"
    if (-not (Test-Path $webrtcSmokeScript)) {
        throw "WebRTC gate helper was not found: $webrtcSmokeScript"
    }

    $webrtcSmokeArgs = @{
        ApiBaseUrl = $BaseUrl
        KeycloakBaseUrl = $KeycloakBaseUrl
        RealmName = $RealmName
        ControlHealthUrl = $ControlHealthUrl
        ControlHealthAttempts = [Math]::Max(1, $ControlHealthAttempts)
        RequestTimeoutSec = [Math]::Max(1, $RequestTimeoutSec)
        TransportHealthAttempts = [Math]::Max(1, $TransportHealthAttempts)
        TransportHealthDelayMs = [Math]::Max(100, $TransportHealthDelayMs)
        TokenClientId = $TokenClientId
        TokenUsername = $TokenUsername
        TokenPassword = $TokenPassword
        TimeoutMs = [Math]::Max(8000, $CommandTimeoutMs)
        OutputJsonPath = $OutputJsonPath
    }
    if ($PSBoundParameters.ContainsKey("PrincipalId") -and -not [string]::IsNullOrWhiteSpace($PrincipalId)) {
        $webrtcSmokeArgs["PrincipalId"] = $PrincipalId
    }
    if ($PSBoundParameters.ContainsKey("TenantId") -and -not [string]::IsNullOrWhiteSpace($TenantId)) {
        $webrtcSmokeArgs["TenantId"] = $TenantId
    }
    if ($PSBoundParameters.ContainsKey("OrganizationId") -and -not [string]::IsNullOrWhiteSpace($OrganizationId)) {
        $webrtcSmokeArgs["OrganizationId"] = $OrganizationId
    }
    if ($SkipTransportHealthCheck) {
        $webrtcSmokeArgs["SkipTransportHealthCheck"] = $true
    }

    & $webrtcSmokeScript @webrtcSmokeArgs

    $summaryResult = $null
    if (-not [string]::IsNullOrWhiteSpace($OutputJsonPath) -and (Test-Path $OutputJsonPath)) {
        $summaryResult = Get-Content -Raw -Path $OutputJsonPath | ConvertFrom-Json
    }

    Write-Host ""
    Write-Host "=== Demo Gate Summary ==="
    Write-Host "BaseUrl: $BaseUrl"
    Write-Host "Transport provider: webrtc-datachannel"
    if ($null -ne $summaryResult) {
        Write-Host "Gate session: $($summaryResult.sessionId)"
        Write-Host "Gate command action: $($summaryResult.commandAction)"
        Write-Host "Gate command status: $($summaryResult.commandStatus)"
    }
    if (-not [string]::IsNullOrWhiteSpace($OutputJsonPath)) {
        Write-Host "Summary JSON: $OutputJsonPath"
    }

    exit 0
}

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

function Invoke-DemoGateJson {
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

function Get-SessionsArray {
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

    if ($null -eq $Session) {
        return ""
    }

    $stateProperty = $Session.PSObject.Properties["state"]
    if ($null -eq $stateProperty -or $null -eq $stateProperty.Value) {
        return ""
    }

    return [string]$stateProperty.Value
}

function Test-TerminalSessionState {
    param([string]$State)

    return [string]::Equals($State, "Ended", [StringComparison]::OrdinalIgnoreCase) -or
        [string]::Equals($State, "Failed", [StringComparison]::OrdinalIgnoreCase)
}

function Build-SessionId {
    param([string]$EndpointId)

    $endpointSegment = $EndpointId.Replace("_", "-").Replace(" ", "-").ToLowerInvariant()
    $sessionId = "room-$endpointSegment-$([Guid]::NewGuid().ToString('N'))"
    if ($sessionId.Length -le 64) {
        return $sessionId
    }

    return $sessionId.Substring(0, 64)
}

function Test-SuccessStatus {
    param([string]$Status)

    return [string]::Equals($Status, "Applied", [StringComparison]::OrdinalIgnoreCase) -or
        [string]::Equals($Status, "Accepted", [StringComparison]::OrdinalIgnoreCase)
}

function Test-SelectorResolutionFailure {
    param([object]$Ack)

    if ($null -eq $Ack -or $null -eq $Ack.PSObject.Properties["status"] -or -not [string]::Equals([string]$Ack.status, "Rejected", [StringComparison]::OrdinalIgnoreCase)) {
        return $false
    }

    if ($null -eq $Ack.PSObject.Properties["error"] -or $null -eq $Ack.error) {
        return $false
    }

    $errorCode = if ($null -ne $Ack.error.code) { [string]$Ack.error.code } else { "" }
    $errorMessage = if ($null -ne $Ack.error.message) { [string]$Ack.error.message } else { "" }

    if (-not [string]::Equals($errorCode, "E_COMMAND_EXECUTION_FAILED", [StringComparison]::OrdinalIgnoreCase)) {
        return $false
    }

    return $errorMessage.IndexOf("selector could not be resolved", [StringComparison]::OrdinalIgnoreCase) -ge 0
}

function Test-NoopDeterministicRejection {
    param([object]$Payload)

    if ($null -eq $Payload -or $null -eq $Payload.PSObject.Properties["status"] -or -not [string]::Equals([string]$Payload.status, "Rejected", [StringComparison]::OrdinalIgnoreCase)) {
        return $false
    }

    if ($null -eq $Payload.PSObject.Properties["error"] -or $null -eq $Payload.error) {
        return $false
    }

    $errorCode = if ($null -ne $Payload.error.code) { [string]$Payload.error.code } else { "" }
    $errorMessage = if ($null -ne $Payload.error.message) { [string]$Payload.error.message } else { "" }
    return [string]::Equals($errorCode, "E_COMMAND_EXECUTION_FAILED", [StringComparison]::OrdinalIgnoreCase) -and
        ($errorMessage.IndexOf("Unsupported HID action", [StringComparison]::OrdinalIgnoreCase) -ge 0)
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
        throw "Demo gate token response did not contain access_token."
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
$createdSessionId = $null
$readyEndpoint = $null
$readyAgentId = $null
$effectivePrincipalId = $PrincipalId
$commandAck = $null
$selectedKeyboardItfSel = $null
$closeResult = $null
$usedDeterministicNoopRejection = $false

try {
    $null = Invoke-DemoGateJson -Method "GET" -Uri "$base/health" -Headers $callerHeaders
}
catch {
    throw "Demo gate requires a running API at $BaseUrl. Start API first (for example via 'dotnet run --project Platform/Tools/HidBridge.RuntimeCtl/HidBridge.RuntimeCtl.csproj -- --platform-root Platform demo-flow -SkipIdentityReset'). $($_.Exception.Message)"
}

try {
    $deadlineUtc = [DateTimeOffset]::UtcNow.AddSeconds([Math]::Max(1, $ReadyEndpointTimeoutSec))
    $pollMs = [Math]::Max(100, $ReadyEndpointPollIntervalMs)
    $seenEndpoints = @()

    while ([DateTimeOffset]::UtcNow -lt $deadlineUtc) {
        $sessions = Get-SessionsArray -Response (Invoke-DemoGateJson -Method "GET" -Uri "$base/api/v1/sessions" -Headers $callerHeaders)
        $inventory = Invoke-DemoGateJson -Method "GET" -Uri "$base/api/v1/dashboards/inventory" -Headers $callerHeaders
        $endpoints = if ($null -ne $inventory.PSObject.Properties["endpoints"]) { @($inventory.endpoints) } else { @() }

        $seenEndpoints = @($endpoints | ForEach-Object { [string]$_.endpointId })

        $readyEndpointCard = $endpoints | Where-Object {
            if ($null -eq $_) {
                return $false
            }

            $activeSessionId = if ($null -ne $_.PSObject.Properties["activeSessionId"]) { [string]$_.activeSessionId } else { "" }
            if ([string]::IsNullOrWhiteSpace($activeSessionId)) {
                return $true
            }

            $linkedSession = $sessions | Where-Object {
                $_ -and $_.PSObject.Properties["sessionId"] -and [string]$_.sessionId -eq $activeSessionId
            } | Select-Object -First 1

            if ($null -eq $linkedSession) {
                return $true
            }

            return Test-TerminalSessionState -State (Get-NormalizedSessionState -Session $linkedSession)
        } | Select-Object -First 1

        if ($null -ne $readyEndpointCard) {
            $readyEndpoint = [string]$readyEndpointCard.endpointId
            $readyAgentId = [string]$readyEndpointCard.agentId
            break
        }

        Start-Sleep -Milliseconds $pollMs
    }

    if ([string]::IsNullOrWhiteSpace($readyEndpoint) -or [string]::IsNullOrWhiteSpace($readyAgentId)) {
        throw "Demo gate did not find a reusable endpoint in time. Endpoints seen: $($seenEndpoints -join ', ')."
    }

    $createdSessionId = Build-SessionId -EndpointId $readyEndpoint
    $openedSession = Invoke-DemoGateJson -Method "POST" -Uri "$base/api/v1/sessions" -Headers $callerHeaders -Body @{
        sessionId = $createdSessionId
        profile = $Profile
        requestedBy = $PrincipalId
        targetAgentId = $readyAgentId
        targetEndpointId = $readyEndpoint
        shareMode = "Owner"
        tenantId = $TenantId
        organizationId = $OrganizationId
    }

    if ($null -ne $openedSession -and $null -ne $openedSession.PSObject.Properties["requestedBy"] -and -not [string]::IsNullOrWhiteSpace([string]$openedSession.requestedBy)) {
        $effectivePrincipalId = [string]$openedSession.requestedBy
    }

    $ownerParticipantId = "owner:$effectivePrincipalId"
    $null = Invoke-DemoGateJson -Method "POST" -Uri "$base/api/v1/sessions/$([Uri]::EscapeDataString($createdSessionId))/control/request" -Headers $callerHeaders -Body @{
        participantId = $ownerParticipantId
        requestedBy = $effectivePrincipalId
        leaseSeconds = $LeaseSeconds
        reason = "demo gate control lease"
        tenantId = $TenantId
        organizationId = $OrganizationId
    }

    $commandId = "cmd-gate-$([Guid]::NewGuid().ToString('N').Substring(0, 12))"
    $commandAction = if ($RequireDeviceAck) { "keyboard.shortcut" } else { "noop" }
    $commandArgs = @{
        principalId = $effectivePrincipalId
        participantId = $ownerParticipantId
    }
    if ($RequireDeviceAck) {
        $commandArgs["shortcut"] = "CTRL+ALT+M"
        $commandArgs["keys"] = "CTRL+ALT+M"
    }

    $commandAck = Invoke-DemoGateJson -Method "POST" -Uri "$base/api/v1/sessions/$([Uri]::EscapeDataString($createdSessionId))/commands" -Headers $callerHeaders -Body @{
        commandId = $commandId
        sessionId = $createdSessionId
        channel = "Hid"
        action = $commandAction
        args = $commandArgs
        timeoutMs = $CommandTimeoutMs
        idempotencyKey = "idem-gate-$commandId"
        tenantId = $TenantId
        organizationId = $OrganizationId
    }

    $status = if ($null -ne $commandAck.PSObject.Properties["status"]) { [string]$commandAck.status } else { "" }
    if ($RequireDeviceAck -and -not (Test-SuccessStatus -Status $status) -and (Test-SelectorResolutionFailure -Ack $commandAck)) {
        $candidateSelectors = [System.Collections.Generic.List[int]]::new()
        if ($KeyboardInterfaceSelector -ge 0 -and $KeyboardInterfaceSelector -le 255) {
            # Explicit selector means "try only this selector".
            $candidateSelectors.Add($KeyboardInterfaceSelector) | Out-Null
        }
        else {
            foreach ($candidate in $KeyboardInterfaceCandidates) {
                if ($candidate -ge 0 -and $candidate -le 255) {
                    $candidateSelectors.Add($candidate) | Out-Null
                }
            }
        }

        $uniqueCandidateSelectors = @($candidateSelectors.ToArray() | Select-Object -Unique)
        foreach ($candidateSelector in $uniqueCandidateSelectors) {
            $candidateCommandId = "cmd-gate-$([Guid]::NewGuid().ToString('N').Substring(0, 12))"
            $candidateArgs = @{
                shortcut = "CTRL+ALT+M"
                keys = "CTRL+ALT+M"
                principalId = $effectivePrincipalId
                participantId = $ownerParticipantId
                itfSel = [int]$candidateSelector
            }

            $candidateAck = Invoke-DemoGateJson -Method "POST" -Uri "$base/api/v1/sessions/$([Uri]::EscapeDataString($createdSessionId))/commands" -Headers $callerHeaders -Body @{
                commandId = $candidateCommandId
                sessionId = $createdSessionId
                channel = "Hid"
                action = "keyboard.shortcut"
                args = $candidateArgs
                timeoutMs = $CommandTimeoutMs
                idempotencyKey = "idem-gate-$candidateCommandId"
                tenantId = $TenantId
                organizationId = $OrganizationId
            }

            $candidateStatus = if ($null -ne $candidateAck.PSObject.Properties["status"]) { [string]$candidateAck.status } else { "" }
            if (Test-SuccessStatus -Status $candidateStatus) {
                $commandAck = $candidateAck
                $commandId = $candidateCommandId
                $selectedKeyboardItfSel = [int]$candidateSelector
                $status = $candidateStatus
                break
            }

            $commandAck = $candidateAck
            $status = $candidateStatus
        }
    }

    $isDeterministicNoopRejection = (-not $RequireDeviceAck) -and (Test-NoopDeterministicRejection -Payload $commandAck)

    if (-not (Test-SuccessStatus -Status $status) -and -not $isDeterministicNoopRejection) {
        $errorCode = if ($null -ne $commandAck.PSObject.Properties["error"] -and $null -ne $commandAck.error -and $null -ne $commandAck.error.code) { [string]$commandAck.error.code } else { "<none>" }
        $errorMessage = if ($null -ne $commandAck.PSObject.Properties["error"] -and $null -ne $commandAck.error -and $null -ne $commandAck.error.message) { [string]$commandAck.error.message } else { "<none>" }
        throw "Demo gate command was not applied. Action=$commandAction, RequireDeviceAck=$($RequireDeviceAck.IsPresent), Status=$status, ErrorCode=$errorCode, ErrorMessage=$errorMessage."
    }
    $usedDeterministicNoopRejection = $isDeterministicNoopRejection

    $effectiveCommandId = $commandId
    $ackCommandId = Get-ObjectPropertyString -InputObject $commandAck -Name "commandId"
    if (-not [string]::IsNullOrWhiteSpace($ackCommandId)) {
        $effectiveCommandId = $ackCommandId
    }

    $journalEntries = @()
    $journalEntry = $null
    for ($attempt = 0; $attempt -lt 20; $attempt++) {
        $journalResponse = Invoke-DemoGateJson -Method "GET" -Uri "$base/api/v1/sessions/$([Uri]::EscapeDataString($createdSessionId))/commands/journal" -Headers $callerHeaders
        $journalEntries = @(Get-SessionsArray -Response $journalResponse)
        $journalEntry = $journalEntries | Where-Object {
            if ($null -eq $_) {
                return $false
            }

            $entryCommandId = Get-ObjectPropertyString -InputObject $_ -Name "commandId"
            return -not [string]::IsNullOrWhiteSpace($entryCommandId) -and [string]::Equals($entryCommandId, $effectiveCommandId, [StringComparison]::OrdinalIgnoreCase)
        } | Select-Object -First 1

        if ($null -ne $journalEntry) {
            break
        }

        Start-Sleep -Milliseconds 250
    }

    if ($null -eq $journalEntry) {
        $journalKnownCommandIds = @($journalEntries | ForEach-Object { Get-ObjectPropertyString -InputObject $_ -Name "commandId" } | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
        throw "Demo gate command journal entry was not found for $effectiveCommandId. Journal count=$($journalEntries.Count). Known commandIds=$($journalKnownCommandIds -join ', ')."
    }

    $journalStatus = Get-ObjectPropertyString -InputObject $journalEntry -Name "status"
    $journalDeterministicNoopRejection = (-not $RequireDeviceAck) -and (Test-NoopDeterministicRejection -Payload $journalEntry)
    if (-not (Test-SuccessStatus -Status $journalStatus) -and -not $journalDeterministicNoopRejection) {
        throw "Demo gate journal status is not successful. Action=$commandAction, RequireDeviceAck=$($RequireDeviceAck.IsPresent), Status=$journalStatus."
    }
}
finally {
    if (-not [string]::IsNullOrWhiteSpace($createdSessionId)) {
        try {
            $closeResult = Invoke-DemoGateJson -Method "POST" -Uri "$base/api/v1/sessions/$([Uri]::EscapeDataString($createdSessionId))/close" -Headers $callerHeaders -Body @{
                sessionId = $createdSessionId
                reason = "demo gate cleanup"
            }
        }
        catch {
            Write-Warning ("Demo gate cleanup failed for session {0}: {1}" -f $createdSessionId, $_.Exception.Message)
        }
    }
}

$summary = [ordered]@{
    baseUrl = $BaseUrl
    readyEndpoint = $readyEndpoint
    readyAgentId = $readyAgentId
    sessionId = $createdSessionId
    requireDeviceAck = $RequireDeviceAck.IsPresent
    commandAction = $commandAction
    commandStatus = if ($null -ne $commandAck -and $null -ne $commandAck.PSObject.Properties["status"]) { [string]$commandAck.status } else { "" }
    deterministicNoopRejection = $usedDeterministicNoopRejection
    selectedKeyboardItfSel = $selectedKeyboardItfSel
    closeCompleted = $null -ne $closeResult
}

if (-not [string]::IsNullOrWhiteSpace($OutputJsonPath)) {
    $directory = Split-Path -Parent $OutputJsonPath
    if (-not [string]::IsNullOrWhiteSpace($directory)) {
        New-Item -ItemType Directory -Force -Path $directory | Out-Null
    }

    $summary | ConvertTo-Json -Depth 20 | Set-Content -Path $OutputJsonPath -Encoding UTF8
}

Write-Host ""
Write-Host "=== Demo Gate Summary ==="
Write-Host "BaseUrl: $BaseUrl"
Write-Host "Ready endpoint: $readyEndpoint"
Write-Host "Ready endpoint agent: $readyAgentId"
Write-Host "Gate session: $createdSessionId"
Write-Host "Gate mode requireDeviceAck: $($summary.requireDeviceAck)"
Write-Host "Gate command action: $($summary.commandAction)"
Write-Host "Gate command status: $($summary.commandStatus)"
Write-Host "Gate deterministic noop rejection: $($summary.deterministicNoopRejection)"
Write-Host "Gate keyboard itfSel: $($summary.selectedKeyboardItfSel)"
Write-Host "Gate cleanup close: $($summary.closeCompleted)"
if (-not [string]::IsNullOrWhiteSpace($OutputJsonPath)) {
    Write-Host "Summary JSON: $OutputJsonPath"
}

exit 0

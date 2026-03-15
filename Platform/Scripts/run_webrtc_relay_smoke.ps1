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
    [string]$PrincipalId = "webrtc-relay-smoke",
    [string]$TenantId = "local-tenant",
    [string]$OrganizationId = "local-org",
    [string]$Profile = "UltraLowLatency",
    [int]$LeaseSeconds = 30,
    [int]$CommandTimeoutMs = 8000,
    [int]$CommandPollIntervalMs = 250,
    [int]$CommandPollAttempts = 30,
    [string]$OutputJsonPath = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$scriptsRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $scriptsRoot "Common/KeycloakCommon.ps1")

try {
    # Windows PowerShell can require explicit load before [System.Net.Http.HttpClient].
    Add-Type -AssemblyName System.Net.Http -ErrorAction Stop
}
catch {
    throw "Failed to load System.Net.Http assembly required by WebRTC relay smoke script: $($_.Exception.Message)"
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

function Invoke-RelayJson {
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

        $json = $Body | ConvertTo-Json -Depth 25
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

function Get-ObjectPropertyValue {
    param(
        [object]$InputObject,
        [string]$Name
    )

    if ($null -eq $InputObject -or [string]::IsNullOrWhiteSpace($Name)) {
        return $null
    }

    foreach ($property in $InputObject.PSObject.Properties) {
        if ([string]::Equals($property.Name, $Name, [System.StringComparison]::OrdinalIgnoreCase)) {
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

function Get-MetricValue {
    param(
        [object]$Metrics,
        [string]$Name
    )

    if ($null -eq $Metrics -or [string]::IsNullOrWhiteSpace($Name)) {
        return $null
    }

    if ($Metrics -is [System.Collections.IDictionary]) {
        foreach ($key in $Metrics.Keys) {
            if ([string]::Equals([string]$key, $Name, [System.StringComparison]::OrdinalIgnoreCase)) {
                return $Metrics[$key]
            }
        }

        return $null
    }

    return Get-ObjectPropertyValue -InputObject $Metrics -Name $Name
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

function Get-CollectionItems {
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

function Get-TerminalSessionState {
    param([string]$State)

    return [string]::Equals($State, "Ended", [System.StringComparison]::OrdinalIgnoreCase) -or
        [string]::Equals($State, "Failed", [System.StringComparison]::OrdinalIgnoreCase)
}

function Build-SessionId {
    param([string]$EndpointId)

    $segment = $EndpointId.Replace("_", "-").Replace(" ", "-").ToLowerInvariant()
    $sessionId = "room-webrtc-relay-$segment-$([Guid]::NewGuid().ToString('N').Substring(0, 12))"
    if ($sessionId.Length -le 64) {
        return $sessionId
    }

    return $sessionId.Substring(0, 64)
}

function New-DispatchClient {
    param([hashtable]$Headers)

    $client = [System.Net.Http.HttpClient]::new()
    foreach ($key in $Headers.Keys) {
        if ([string]::IsNullOrWhiteSpace($key)) {
            continue
        }

        $value = [string]$Headers[$key]
        if ([string]::IsNullOrWhiteSpace($value)) {
            continue
        }

        $null = $client.DefaultRequestHeaders.TryAddWithoutValidation($key, $value)
    }

    return $client
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
        throw "WebRTC relay smoke token response did not contain access_token."
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
$peerId = "peer-relay-$([Guid]::NewGuid().ToString('N').Substring(0, 8))"
$createdSessionId = $null
$readyEndpoint = $null
$readyAgentId = $null
$dispatchAck = $null
$dispatchCommandId = $null
$queuedSequence = $null
$transportHealth = $null
$offlineMarked = $false
$closeResult = $null

try {
    $null = Invoke-RelayJson -Method "GET" -Uri "$base/health" -Headers $callerHeaders
}
catch {
    throw "WebRTC relay smoke requires running API at $BaseUrl. $($_.Exception.Message)"
}

try {
    $sessions = Get-SessionsArray -Response (Invoke-RelayJson -Method "GET" -Uri "$base/api/v1/sessions" -Headers $callerHeaders)
    $inventory = Invoke-RelayJson -Method "GET" -Uri "$base/api/v1/dashboards/inventory" -Headers $callerHeaders
    $endpoints = if ($null -ne $inventory -and $null -ne $inventory.PSObject.Properties["endpoints"]) { @($inventory.endpoints) } else { @() }

    $readyEndpointCard = $endpoints | Where-Object {
        if ($null -eq $_) {
            return $false
        }

        $activeSessionId = Get-ObjectPropertyString -InputObject $_ -Name "activeSessionId"
        if ([string]::IsNullOrWhiteSpace($activeSessionId)) {
            return $true
        }

        $linkedSession = $sessions | Where-Object {
            $_ -and [string]::Equals((Get-ObjectPropertyString -InputObject $_ -Name "sessionId"), $activeSessionId, [System.StringComparison]::OrdinalIgnoreCase)
        } | Select-Object -First 1

        $linkedState = Get-ObjectPropertyString -InputObject $linkedSession -Name "state"
        return Get-TerminalSessionState -State $linkedState
    } | Select-Object -First 1

    if ($null -eq $readyEndpointCard) {
        throw "WebRTC relay smoke did not find a reusable endpoint."
    }

    $readyEndpoint = Get-ObjectPropertyString -InputObject $readyEndpointCard -Name "endpointId"
    $readyAgentId = Get-ObjectPropertyString -InputObject $readyEndpointCard -Name "agentId"
    if ([string]::IsNullOrWhiteSpace($readyEndpoint) -or [string]::IsNullOrWhiteSpace($readyAgentId)) {
        throw "Ready endpoint card is missing endpointId or agentId."
    }

    $createdSessionId = Build-SessionId -EndpointId $readyEndpoint
    $openedSession = Invoke-RelayJson -Method "POST" -Uri "$base/api/v1/sessions" -Headers $callerHeaders -Body @{
        sessionId = $createdSessionId
        profile = $Profile
        requestedBy = $PrincipalId
        targetAgentId = $readyAgentId
        targetEndpointId = $readyEndpoint
        shareMode = "Owner"
        tenantId = $TenantId
        organizationId = $OrganizationId
        transportProvider = "webrtc-datachannel"
    }

    $effectivePrincipalId = if ($null -ne $openedSession -and $null -ne $openedSession.PSObject.Properties["requestedBy"] -and -not [string]::IsNullOrWhiteSpace([string]$openedSession.requestedBy)) {
        [string]$openedSession.requestedBy
    }
    else {
        $PrincipalId
    }

    $ownerParticipantId = "owner:$effectivePrincipalId"
    $null = Invoke-RelayJson -Method "POST" -Uri "$base/api/v1/sessions/$([Uri]::EscapeDataString($createdSessionId))/control/request" -Headers $callerHeaders -Body @{
        participantId = $ownerParticipantId
        requestedBy = $effectivePrincipalId
        leaseSeconds = $LeaseSeconds
        reason = "webrtc relay smoke lease"
        tenantId = $TenantId
        organizationId = $OrganizationId
    }

    $null = Invoke-RelayJson -Method "POST" -Uri "$base/api/v1/sessions/$([Uri]::EscapeDataString($createdSessionId))/transport/webrtc/peers/$([Uri]::EscapeDataString($peerId))/online" -Headers $callerHeaders -Body @{
        endpointId = $readyEndpoint
        metadata = @{
            source = "run_webrtc_relay_smoke"
            principalId = $effectivePrincipalId
        }
    }

    $preDispatchHealth = Invoke-RelayJson -Method "GET" -Uri "$base/api/v1/sessions/$([Uri]::EscapeDataString($createdSessionId))/transport/health?provider=webrtc-datachannel" -Headers $callerHeaders
    $preDispatchMetrics = Get-ObjectPropertyValue -InputObject $preDispatchHealth -Name "metrics"
    $preDispatchEndpointSupports = Get-MetricValue -Metrics $preDispatchMetrics -Name "endpointSupportsWebRtc"
    if ($null -ne $preDispatchEndpointSupports -and -not [System.Convert]::ToBoolean($preDispatchEndpointSupports)) {
        throw "Endpoint $readyEndpoint does not advertise WebRTC transport capability (endpointSupportsWebRtc=false). Relay smoke cannot force webrtc route."
    }

    $dispatchCommandId = "cmd-webrtc-relay-$([Guid]::NewGuid().ToString('N').Substring(0, 10))"
    $dispatchBody = @{
        commandId = $dispatchCommandId
        sessionId = $createdSessionId
        channel = "Hid"
        action = "noop"
        args = @{
            transportProvider = "webrtc"
            principalId = $effectivePrincipalId
            participantId = $ownerParticipantId
            reason = "webrtc relay smoke"
        }
        timeoutMs = [Math]::Max(1000, $CommandTimeoutMs)
        idempotencyKey = "idem-$dispatchCommandId"
        tenantId = $TenantId
        organizationId = $OrganizationId
    } | ConvertTo-Json -Depth 25

    $dispatchClient = New-DispatchClient -Headers $callerHeaders
    try {
        $dispatchUri = "$base/api/v1/sessions/$([Uri]::EscapeDataString($createdSessionId))/commands"
        $dispatchContent = [System.Net.Http.StringContent]::new($dispatchBody, [System.Text.Encoding]::UTF8, "application/json")
        $dispatchTask = $dispatchClient.PostAsync($dispatchUri, $dispatchContent)

        $matchedEnvelope = $null
        $lastQueuedCommandIds = @()
        $lastQueuedCount = 0
        for ($attempt = 1; $attempt -le [Math]::Max(1, $CommandPollAttempts); $attempt++) {
            if ($dispatchTask.IsCompleted) {
                $dispatchResponseEarly = $dispatchTask.GetAwaiter().GetResult()
                $dispatchResponseEarlyContent = $dispatchResponseEarly.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                if (-not $dispatchResponseEarly.IsSuccessStatusCode) {
                    throw "Dispatch endpoint returned HTTP $([int]$dispatchResponseEarly.StatusCode): $dispatchResponseEarlyContent"
                }

                if (-not [string]::IsNullOrWhiteSpace($dispatchResponseEarlyContent)) {
                    $dispatchAck = $dispatchResponseEarlyContent | ConvertFrom-Json
                }

                $earlyStatus = Get-ObjectPropertyString -InputObject $dispatchAck -Name "status"
                $earlyError = Get-ObjectPropertyValue -InputObject $dispatchAck -Name "error"
                $earlyMetrics = Get-ObjectPropertyValue -InputObject $dispatchAck -Name "metrics"
                $fallbackMetric = Get-MetricValue -Metrics $earlyMetrics -Name "transportFallbackFromWebRtc"
                $errorCode = Get-ObjectPropertyString -InputObject $earlyError -Name "code"
                $errorMessage = Get-ObjectPropertyString -InputObject $earlyError -Name "message"
                throw "Dispatch completed before relay envelope became visible. Status=$earlyStatus, FallbackFromWebRtc=$fallbackMetric, ErrorCode=$errorCode, ErrorMessage=$errorMessage."
            }

            Start-Sleep -Milliseconds ([Math]::Max(100, $CommandPollIntervalMs))
            $commandRoute = "$base/api/v1/sessions/$([Uri]::EscapeDataString($createdSessionId))/transport/webrtc/commands?peerId=$([Uri]::EscapeDataString($peerId))&limit=50"
            $queuedCommands = Get-CollectionItems -Response (Invoke-RelayJson -Method "GET" -Uri $commandRoute -Headers $callerHeaders)
            $lastQueuedCount = @($queuedCommands).Count
            $lastQueuedCommandIds = @(
                $queuedCommands |
                    ForEach-Object {
                        if ($_ -and $_.PSObject.Properties["command"] -and $_.command) {
                            Get-ObjectPropertyString -InputObject $_.command -Name "commandId"
                        }
                    } |
                    Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
            )

            $matchedEnvelope = $queuedCommands | Where-Object {
                $_ -and $_.PSObject.Properties["command"] -and $_.command -and $_.command.PSObject.Properties["commandId"] -and
                    [string]::Equals([string]$_.command.commandId, $dispatchCommandId, [System.StringComparison]::OrdinalIgnoreCase)
            } | Select-Object -First 1

            if ($null -ne $matchedEnvelope) {
                break
            }
        }

        if ($null -eq $matchedEnvelope) {
            $dispatchStatus = ""
            $dispatchErrorCode = ""
            $dispatchErrorMessage = ""
            if ($dispatchTask.IsCompleted) {
                $dispatchResponseLate = $dispatchTask.GetAwaiter().GetResult()
                $dispatchResponseLateContent = $dispatchResponseLate.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                if (-not [string]::IsNullOrWhiteSpace($dispatchResponseLateContent)) {
                    $dispatchAck = $dispatchResponseLateContent | ConvertFrom-Json
                }

                $dispatchStatus = Get-ObjectPropertyString -InputObject $dispatchAck -Name "status"
                $dispatchError = Get-ObjectPropertyValue -InputObject $dispatchAck -Name "error"
                $dispatchErrorCode = Get-ObjectPropertyString -InputObject $dispatchError -Name "code"
                $dispatchErrorMessage = Get-ObjectPropertyString -InputObject $dispatchError -Name "message"
            }

            $peers = Get-CollectionItems -Response (Invoke-RelayJson -Method "GET" -Uri "$base/api/v1/sessions/$([Uri]::EscapeDataString($createdSessionId))/transport/webrtc/peers" -Headers $callerHeaders)
            $healthSnapshot = Invoke-RelayJson -Method "GET" -Uri "$base/api/v1/sessions/$([Uri]::EscapeDataString($createdSessionId))/transport/health?provider=webrtc-datachannel" -Headers $callerHeaders
            $healthMetrics = Get-ObjectPropertyValue -InputObject $healthSnapshot -Name "metrics"
            $onlinePeerCount = Get-MetricValue -Metrics $healthMetrics -Name "onlinePeerCount"
            $bridgeMode = Get-MetricValue -Metrics $healthMetrics -Name "bridgeMode"

            throw "Relay queue did not expose command envelope for $dispatchCommandId. DispatchStatus=$dispatchStatus, ErrorCode=$dispatchErrorCode, ErrorMessage=$dispatchErrorMessage, OnlinePeerCount=$onlinePeerCount, BridgeMode=$bridgeMode, VisiblePeers=$(@($peers).Count), LastQueueCount=$lastQueuedCount, LastQueueCommandIds=$($lastQueuedCommandIds -join ',')."
        }

        $queuedSequence = if ($null -ne $matchedEnvelope.PSObject.Properties["sequence"]) { [int]$matchedEnvelope.sequence } else { 0 }

        $null = Invoke-RelayJson -Method "POST" -Uri "$base/api/v1/sessions/$([Uri]::EscapeDataString($createdSessionId))/transport/webrtc/commands/$([Uri]::EscapeDataString($dispatchCommandId))/ack" -Headers $callerHeaders -Body @{
            status = "Applied"
            metrics = @{
                relaySmoked = 1
                relayQueuedSequence = [double]$queuedSequence
            }
        }

        $dispatchResponse = $dispatchTask.GetAwaiter().GetResult()
        $dispatchResponseContent = $dispatchResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult()
        if (-not $dispatchResponse.IsSuccessStatusCode) {
            throw "Dispatch endpoint returned HTTP $([int]$dispatchResponse.StatusCode): $dispatchResponseContent"
        }

        if ([string]::IsNullOrWhiteSpace($dispatchResponseContent)) {
            throw "Dispatch endpoint returned empty response body."
        }

        $dispatchAck = $dispatchResponseContent | ConvertFrom-Json
    }
    finally {
        $dispatchClient.Dispose()
    }

    $ackStatus = Get-ObjectPropertyString -InputObject $dispatchAck -Name "status"
    if (-not [string]::Equals($ackStatus, "Applied", [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Dispatch acknowledgment status expected Applied but received '$ackStatus'."
    }

    $transportHealth = Invoke-RelayJson -Method "GET" -Uri "$base/api/v1/sessions/$([Uri]::EscapeDataString($createdSessionId))/transport/health?provider=webrtc-datachannel" -Headers $callerHeaders
    $provider = Get-ObjectPropertyString -InputObject $transportHealth -Name "provider"
    if (-not [string]::Equals($provider, "WebRtcDataChannel", [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Transport health provider expected WebRtcDataChannel but received '$provider'."
    }

    $status = Get-ObjectPropertyString -InputObject $transportHealth -Name "status"
    if ([string]::IsNullOrWhiteSpace($status)) {
        throw "Transport health did not return status."
    }

    $summary = [ordered]@{
        baseUrl = $BaseUrl
        sessionId = $createdSessionId
        endpointId = $readyEndpoint
        agentId = $readyAgentId
        peerId = $peerId
        commandId = $dispatchCommandId
        commandStatus = $ackStatus
        queuedSequence = $queuedSequence
        healthProvider = $provider
        healthStatus = $status
        healthConnected = if ($null -ne $transportHealth.PSObject.Properties["connected"]) { [bool]$transportHealth.connected } else { $false }
        healthMetrics = if ($null -ne $transportHealth.PSObject.Properties["metrics"]) { $transportHealth.metrics } else { $null }
    }

    if (-not [string]::IsNullOrWhiteSpace($OutputJsonPath)) {
        $outputPath = $OutputJsonPath
        if (-not [System.IO.Path]::IsPathRooted($outputPath)) {
            $outputPath = Join-Path (Get-Location).Path $outputPath
        }

        $outputDirectory = Split-Path -Parent $outputPath
        if (-not [string]::IsNullOrWhiteSpace($outputDirectory) -and -not (Test-Path $outputDirectory)) {
            New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
        }

        ($summary | ConvertTo-Json -Depth 30) | Set-Content -Path $outputPath -Encoding UTF8
    }

    Write-Host ""
    Write-Host "=== WebRTC Relay Smoke Summary ==="
    Write-Host "BaseUrl: $BaseUrl"
    Write-Host "Ready endpoint: $readyEndpoint"
    Write-Host "Ready endpoint agent: $readyAgentId"
    Write-Host "Session: $createdSessionId"
    Write-Host "Peer: $peerId"
    Write-Host "Command: $dispatchCommandId -> $ackStatus"
    Write-Host "Queued sequence: $queuedSequence"
    Write-Host "Transport health: $provider / $status / connected=$($summary.healthConnected)"
    if (-not [string]::IsNullOrWhiteSpace($OutputJsonPath)) {
        Write-Host "Summary JSON: $OutputJsonPath"
    }
}
finally {
    if (-not [string]::IsNullOrWhiteSpace($createdSessionId)) {
        try {
            $null = Invoke-RelayJson -Method "POST" -Uri "$base/api/v1/sessions/$([Uri]::EscapeDataString($createdSessionId))/transport/webrtc/peers/$([Uri]::EscapeDataString($peerId))/offline" -Headers $callerHeaders
            $offlineMarked = $true
        }
        catch {
            Write-Warning "WebRTC relay smoke failed to mark peer offline: $($_.Exception.Message)"
        }

        try {
            $closeResult = Invoke-RelayJson -Method "POST" -Uri "$base/api/v1/sessions/$([Uri]::EscapeDataString($createdSessionId))/close" -Headers $callerHeaders -Body @{
                sessionId = $createdSessionId
                reason = "webrtc relay smoke cleanup"
                tenantId = $TenantId
                organizationId = $OrganizationId
            }
        }
        catch {
            Write-Warning "WebRTC relay smoke cleanup failed for session ${createdSessionId}: $($_.Exception.Message)"
        }
    }
}

if ($null -eq $dispatchAck) {
    exit 1
}

if (-not $offlineMarked) {
    Write-Warning "Peer offline marker was not confirmed during cleanup."
}

if ($null -eq $closeResult) {
    Write-Warning "Session close result was not confirmed during cleanup."
}

exit 0

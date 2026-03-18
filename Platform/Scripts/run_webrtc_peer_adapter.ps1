param(
    [Parameter(Mandatory = $true)]
    [string]$SessionId,
    [string]$BaseUrl = "http://127.0.0.1:18093",
    [string]$PeerId = "",
    [string]$EndpointId = "",
    [bool]$EnsureSession = $true,
    [string]$Profile = "UltraLowLatency",
    [int]$LeaseSeconds = 120,
    [string]$ControlWsUrl = "ws://127.0.0.1:18092/ws/control",
    [int]$PollIntervalMs = 250,
    [int]$BatchLimit = 50,
    [int]$CommandTimeoutMs = 10000,
    [int]$HeartbeatIntervalSec = 10,
    [int]$DurationSec = 0,
    [switch]$SingleRun,
    [switch]$StrictAck,
    [switch]$StartExp022,
    [string]$Exp022StartScriptPath = "",
    [string]$UartPort = "",
    [int]$UartBaud = 3000000,
    [string]$UartHmacKey = "",
    [string]$KeycloakBaseUrl = "http://127.0.0.1:18096",
    [string]$RealmName = "hidbridge-dev",
    [string]$TokenClientId = "controlplane-smoke",
    [string]$TokenClientSecret = "",
    [string]$TokenScope = "",
    [string]$TokenUsername = "operator.smoke.admin",
    [string]$TokenPassword = "ChangeMe123!",
    [string]$AccessToken = "",
    [string]$PrincipalId = "webrtc.peer.adapter",
    [string]$TenantId = "local-tenant",
    [string]$OrganizationId = "local-org",
    [string]$OutputJsonPath = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$scriptsRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $scriptsRoot "Common/KeycloakCommon.ps1")

try {
    Add-Type -AssemblyName System.Net.Http -ErrorAction Stop
}
catch {
    throw "Failed to load required .NET networking assemblies: $($_.Exception.Message)"
}

# Windows PowerShell 5.1 does not always expose System.Net.WebSockets.Client as a
# standalone assembly name. The ClientWebSocket type is available via the runtime.
try {
    Add-Type -AssemblyName System.Net.WebSockets.Client -ErrorAction Stop
}
catch {
    # Best-effort load; continue when the assembly name is not resolvable.
}

try {
    $null = [System.Net.WebSockets.ClientWebSocket]
}
catch {
    throw "ClientWebSocket type is unavailable in this PowerShell runtime. Use PowerShell 7+ (`pwsh`) or a .NET-capable Windows PowerShell host."
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

# Extracts HTTP status code from a web exception when possible.
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

function Request-AdapterAccessTokenFromCredentials {
    param()

    if ([string]::IsNullOrWhiteSpace($TokenUsername) -or [string]::IsNullOrWhiteSpace($TokenPassword)) {
        return $null
    }

    $tokenEndpoint = "$($KeycloakBaseUrl.TrimEnd('/'))/realms/$RealmName/protocol/openid-connect/token"
    $scopeCandidates = @()
    if (-not [string]::IsNullOrWhiteSpace($TokenScope)) {
        $scopeCandidates += $TokenScope
    }
    foreach ($candidate in @("", "openid")) {
        if ($scopeCandidates -notcontains $candidate) {
            $scopeCandidates += $candidate
        }
    }

    $lastError = $null
    foreach ($scopeCandidate in $scopeCandidates) {
        $body = @{
            grant_type = "password"
            client_id = $TokenClientId
            username = $TokenUsername
            password = $TokenPassword
        }
        if (-not [string]::IsNullOrWhiteSpace($TokenClientSecret)) {
            $body.client_secret = $TokenClientSecret
        }
        if (-not [string]::IsNullOrWhiteSpace($scopeCandidate)) {
            $body.scope = $scopeCandidate
        }

        try {
            $response = Invoke-RestMethod -Method Post -Uri $tokenEndpoint -ContentType "application/x-www-form-urlencoded" -Body $body -TimeoutSec 30
            $token = Get-ObjectPropertyString -InputObject $response -Name "access_token"
            if (-not [string]::IsNullOrWhiteSpace($token)) {
                return $token
            }
        }
        catch {
            $responseBody = Get-ErrorResponseBody $_.Exception
            $lastError = if ([string]::IsNullOrWhiteSpace($responseBody)) {
                "Token refresh failed for ${TokenUsername}/${TokenClientId}: $($_.Exception.Message)"
            }
            else {
                "Token refresh failed for ${TokenUsername}/${TokenClientId}: $($_.Exception.Message)`n$responseBody"
            }
        }
    }

    if (-not [string]::IsNullOrWhiteSpace($lastError)) {
        throw $lastError
    }

    throw "Token refresh failed for ${TokenUsername}/${TokenClientId}: access_token was not returned."
}

function Try-RefreshAdapterBearerToken {
    param([hashtable]$Headers)

    try {
        $token = Request-AdapterAccessTokenFromCredentials
        if ([string]::IsNullOrWhiteSpace($token)) {
            return $false
        }

        $Headers["Authorization"] = "Bearer $token"
        $script:resolvedToken = $token
        return $true
    }
    catch {
        Write-Warning "WebRTC peer adapter bearer refresh failed: $($_.Exception.Message)"
        return $false
    }
}

function Invoke-AdapterJson {
    param(
        [string]$Method,
        [string]$Uri,
        [hashtable]$Headers,
        [object]$Body = $null,
        [int]$TimeoutSec = 30,
        [bool]$AllowAuthRefresh = $true
    )

    try {
        if ($null -eq $Body) {
            return Invoke-RestMethod -Method $Method -Uri $Uri -Headers $Headers -TimeoutSec $TimeoutSec
        }

        $json = $Body | ConvertTo-Json -Depth 25
        return Invoke-RestMethod -Method $Method -Uri $Uri -Headers $Headers -ContentType "application/json" -Body $json -TimeoutSec $TimeoutSec
    }
    catch {
        $exceptionMessage = $_.Exception.Message
        $responseBody = Get-ErrorResponseBody $_.Exception

        $isUnauthorized = $exceptionMessage -match '\(401\)' `
            -or $exceptionMessage -match '401 \(Unauthorized\)' `
            -or $responseBody -match '"status"\s*:\s*401' `
            -or $responseBody -match '"code"\s*:\s*"authentication_required"' `
            -or $responseBody -match '"error"\s*:\s*"invalid_token"'

        if ($AllowAuthRefresh -and $isUnauthorized -and (Try-RefreshAdapterBearerToken -Headers $Headers)) {
            return Invoke-AdapterJson -Method $Method -Uri $Uri -Headers $Headers -Body $Body -TimeoutSec $TimeoutSec -AllowAuthRefresh:$false
        }

        if ([string]::IsNullOrWhiteSpace($responseBody)) {
            throw "HTTP $Method $Uri failed: $exceptionMessage"
        }

        throw "HTTP $Method $Uri failed: $exceptionMessage`n$responseBody"
    }
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

    if ([string]::IsNullOrWhiteSpace($State)) {
        return $false
    }

    switch ($State.Trim().ToLowerInvariant()) {
        "closed" { return $true }
        "failed" { return $true }
        "expired" { return $true }
        default { return $false }
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

    if ($InputObject -is [System.Collections.IDictionary]) {
        foreach ($key in $InputObject.Keys) {
            if ([string]::Equals([string]$key, $Name, [System.StringComparison]::OrdinalIgnoreCase)) {
                return $InputObject[$key]
            }
        }

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

function Find-ReadyEndpointCard {
    param(
        [string]$BaseAddress,
        [hashtable]$Headers,
        [string]$PreferredEndpointId
    )

    $sessionsResponse = Invoke-AdapterJson -Method "GET" -Uri "$BaseAddress/api/v1/sessions" -Headers $Headers
    $sessions = Get-CollectionItems -Response $sessionsResponse
    $inventory = Invoke-AdapterJson -Method "GET" -Uri "$BaseAddress/api/v1/dashboards/inventory" -Headers $Headers
    $endpoints = if ($null -ne $inventory -and $null -ne $inventory.PSObject.Properties["endpoints"]) { @($inventory.endpoints) } else { @() }

    if (-not [string]::IsNullOrWhiteSpace($PreferredEndpointId)) {
        $selected = $endpoints | Where-Object {
            [string]::Equals((Get-ObjectPropertyString -InputObject $_ -Name "endpointId"), $PreferredEndpointId, [System.StringComparison]::OrdinalIgnoreCase)
        } | Select-Object -First 1

        if ($null -ne $selected) {
            return $selected
        }
    }

    return $endpoints | Where-Object {
        if ($null -eq $_) {
            return $false
        }

        $activeSessionId = Get-ObjectPropertyString -InputObject $_ -Name "activeSessionId"
        if ([string]::IsNullOrWhiteSpace($activeSessionId)) {
            return $true
        }

        $linkedSession = $sessions | Where-Object {
            [string]::Equals((Get-ObjectPropertyString -InputObject $_ -Name "sessionId"), $activeSessionId, [System.StringComparison]::OrdinalIgnoreCase)
        } | Select-Object -First 1

        $linkedState = Get-ObjectPropertyString -InputObject $linkedSession -Name "state"
        return Get-TerminalSessionState -State $linkedState
    } | Select-Object -First 1
}

function Get-CommandArgsMap {
    param([object]$Command)

    $argsValue = Get-ObjectPropertyValue -InputObject $Command -Name "args"
    if ($null -eq $argsValue) {
        return @{}
    }

    if ($argsValue -is [System.Collections.IDictionary]) {
        $map = @{}
        foreach ($key in $argsValue.Keys) {
            $map[[string]$key] = $argsValue[$key]
        }
        return $map
    }

    $fromObject = @{}
    foreach ($property in $argsValue.PSObject.Properties) {
        $fromObject[[string]$property.Name] = $property.Value
    }
    return $fromObject
}

function Convert-RelayCommandToExp022Payload {
    param([object]$Command)

    $action = Get-ObjectPropertyString -InputObject $Command -Name "action"
    $commandId = Get-ObjectPropertyString -InputObject $Command -Name "commandId"
    $args = Get-CommandArgsMap -Command $Command

    $payload = [ordered]@{
        id = $commandId
        type = $action
    }

    switch ($action.ToLowerInvariant()) {
        "keyboard.text" {
            $payload["text"] = if ($args.ContainsKey("text")) { [string]$args["text"] } else { "" }
            break
        }
        "keyboard.shortcut" {
            $payload["shortcut"] = if ($args.ContainsKey("shortcut")) { [string]$args["shortcut"] } else { "CTRL+ALT+M" }
            break
        }
        "keyboard.press" {
            $payload["usage"] = if ($args.ContainsKey("usage")) { [int]$args["usage"] } else { 0 }
            if ($args.ContainsKey("modifiers")) {
                $payload["modifiers"] = [int]$args["modifiers"]
            }
            break
        }
        "keyboard.reset" {
            break
        }
        "mouse.move" {
            $payload["dx"] = if ($args.ContainsKey("dx")) { [int]$args["dx"] } else { 0 }
            $payload["dy"] = if ($args.ContainsKey("dy")) { [int]$args["dy"] } else { 0 }
            if ($args.ContainsKey("wheel")) {
                $payload["wheel"] = [int]$args["wheel"]
            }
            break
        }
        "mouse.button" {
            $payload["button"] = if ($args.ContainsKey("button")) { [string]$args["button"] } else { "left" }
            $payload["down"] = if ($args.ContainsKey("down")) { [bool]$args["down"] } else { $false }
            break
        }
        default {
            foreach ($entry in $args.GetEnumerator()) {
                if (-not $payload.Contains($entry.Key)) {
                    $payload[$entry.Key] = $entry.Value
                }
            }
            break
        }
    }

    return $payload
}

function Invoke-Exp022ControlCommand {
    param(
        [string]$WsUrl,
        [object]$Payload,
        [int]$TimeoutMs
    )

    $socket = [System.Net.WebSockets.ClientWebSocket]::new()
    $cts = [System.Threading.CancellationTokenSource]::new([Math]::Max(1000, $TimeoutMs))
    $responseText = $null
    $expectedId = Get-ObjectPropertyString -InputObject $Payload -Name "id"

    try {
        $socket.ConnectAsync([Uri]$WsUrl, $cts.Token).GetAwaiter().GetResult()

        $payloadJson = $Payload | ConvertTo-Json -Depth 20 -Compress
        $sendBytes = [System.Text.Encoding]::UTF8.GetBytes($payloadJson)
        $sendSegment = [ArraySegment[byte]]::new($sendBytes)
        $socket.SendAsync($sendSegment, [System.Net.WebSockets.WebSocketMessageType]::Text, $true, $cts.Token).GetAwaiter().GetResult()

        $buffer = New-Object byte[] 8192
        do {
            $stream = New-Object System.IO.MemoryStream
            try {
                do {
                    $receiveSegment = [ArraySegment[byte]]::new($buffer)
                    $receiveResult = $socket.ReceiveAsync($receiveSegment, $cts.Token).GetAwaiter().GetResult()
                    if ($receiveResult.MessageType -eq [System.Net.WebSockets.WebSocketMessageType]::Close) {
                        throw "exp-022 websocket closed before ACK."
                    }

                    $stream.Write($buffer, 0, $receiveResult.Count)
                } while (-not $receiveResult.EndOfMessage)

                $responseText = [System.Text.Encoding]::UTF8.GetString($stream.ToArray())
            }
            finally {
                $stream.Dispose()
            }

            $response = $responseText | ConvertFrom-Json

            $queue = [System.Collections.Generic.Queue[object]]::new()
            $queue.Enqueue($response)
            $matchedAckObject = $null

            while ($queue.Count -gt 0 -and $null -eq $matchedAckObject) {
                $candidate = $queue.Dequeue()
                if ($null -eq $candidate) {
                    continue
                }

                if ($candidate -is [System.Collections.IEnumerable] -and -not ($candidate -is [string]) -and -not ($candidate -is [System.Collections.IDictionary])) {
                    foreach ($entry in @($candidate)) {
                        if ($null -ne $entry) {
                            $queue.Enqueue($entry)
                        }
                    }
                    continue
                }

                $candidateId = Get-ObjectPropertyString -InputObject $candidate -Name "id"
                $candidateOk = Get-ObjectPropertyValue -InputObject $candidate -Name "ok"
                $idMatches = [string]::IsNullOrWhiteSpace($expectedId) `
                    -or [string]::IsNullOrWhiteSpace($candidateId) `
                    -or [string]::Equals($expectedId, $candidateId, [System.StringComparison]::OrdinalIgnoreCase)

                if ($null -ne $candidateOk -and $idMatches) {
                    $matchedAckObject = $candidate
                    break
                }

                foreach ($containerName in @("result", "payload", "data", "ack", "response", "message")) {
                    $container = Get-ObjectPropertyValue -InputObject $candidate -Name $containerName
                    if ($null -ne $container) {
                        $queue.Enqueue($container)
                    }
                }
            }

            if ($null -eq $matchedAckObject) {
                continue
            }

            $okValue = Get-ObjectPropertyValue -InputObject $matchedAckObject -Name "ok"
            $errorMessage = Get-ObjectPropertyString -InputObject $matchedAckObject -Name "error"

            # Ignore intermediate/placeholder payloads that carry no actionable ACK fields yet.
            if (($null -eq $okValue -or ([string]$okValue).Trim().Length -eq 0) -and [string]::IsNullOrWhiteSpace($errorMessage)) {
                continue
            }

            $ok = $false
            $okParsed = $false
            try {
                $ok = [System.Convert]::ToBoolean($okValue)
                $okParsed = $true
            }
            catch {
                $ok = $false
                $okParsed = $false
            }

            if (-not $okParsed -and [string]::IsNullOrWhiteSpace($errorMessage)) {
                continue
            }

            return [pscustomobject]@{
                ok = $ok
                error = $errorMessage
                raw = $responseText
            }
        } while ($true)
    }
    finally {
        if ($socket.State -eq [System.Net.WebSockets.WebSocketState]::Open -or $socket.State -eq [System.Net.WebSockets.WebSocketState]::CloseReceived) {
            try {
                $socket.CloseAsync([System.Net.WebSockets.WebSocketCloseStatus]::NormalClosure, "done", [System.Threading.CancellationToken]::None).GetAwaiter().GetResult()
            }
            catch {
            }
        }

        $socket.Dispose()
        $cts.Dispose()
    }
}

function Resolve-Exp022StartScriptPath {
    param([string]$ExplicitPath)

    if (-not [string]::IsNullOrWhiteSpace($ExplicitPath)) {
        return (Resolve-Path $ExplicitPath).Path
    }

    $candidate = Join-Path $scriptsRoot "..\..\WebRtcTests\exp-022-datachanneldotnet\start.ps1"
    return (Resolve-Path $candidate).Path
}

function Start-Exp022Process {
    param(
        [string]$ScriptPath,
        [string]$Port,
        [int]$Baud,
        [string]$HmacKey,
        [string]$ControlBind,
        [int]$ControlPort,
        [int]$DurationSec,
        [string]$LogsDir
    )

    if (-not (Test-Path $LogsDir)) {
        New-Item -ItemType Directory -Path $LogsDir -Force | Out-Null
    }

    $stdoutPath = Join-Path $LogsDir "exp022.stdout.log"
    $stderrPath = Join-Path $LogsDir "exp022.stderr.log"
    $pwsh = Get-Command powershell.exe -ErrorAction SilentlyContinue
    if ($null -eq $pwsh) {
        throw "powershell.exe was not found in PATH."
    }

    $arguments = @(
        "-NoProfile",
        "-ExecutionPolicy",
        "Bypass",
        "-File",
        $ScriptPath,
        "-Mode",
        "dc-hid-poc",
        "-UartPort",
        $Port,
        "-UartBaud",
        [string]$Baud,
        "-UartHmacKey",
        $HmacKey,
        "-ControlWsBind",
        $ControlBind,
        "-ControlWsPort",
        [string]$ControlPort,
        "-DurationSec",
        [string]$DurationSec
    )

    $process = Start-Process -FilePath $pwsh.Source -ArgumentList $arguments -PassThru -WindowStyle Hidden -RedirectStandardOutput $stdoutPath -RedirectStandardError $stderrPath
    return [pscustomobject]@{
        Process = $process
        Stdout = $stdoutPath
        Stderr = $stderrPath
    }
}

function Get-FileTailText {
    param(
        [string]$Path,
        [int]$LineCount = 40
    )

    if ([string]::IsNullOrWhiteSpace($Path) -or -not (Test-Path $Path)) {
        return ""
    }

    try {
        return (Get-Content -Path $Path -Tail $LineCount -ErrorAction Stop) -join [Environment]::NewLine
    }
    catch {
        return ""
    }
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
        throw "WebRTC peer adapter token response did not contain access_token."
    }

    $resolvedToken = $tokenResponse.access_token
}

if ([string]::IsNullOrWhiteSpace($PeerId)) {
    $PeerId = "peer-exp022-$($env:COMPUTERNAME)-$([Guid]::NewGuid().ToString('N').Substring(0, 6))".ToLowerInvariant()
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
$exp022Process = $null
$exp022Stdout = ""
$exp022Stderr = ""
$lastCommandSequence = $null
$lastSignalSequence = $null
$processedCount = 0
$appliedCount = 0
$rejectedCount = 0
$timeoutCount = 0
$effectivePrincipalId = $PrincipalId
$lastHeartbeatAtUtc = [DateTimeOffset]::MinValue
$startedAtUtc = [DateTimeOffset]::UtcNow
$sessionCreated = $false
$transientLoopErrorCount = 0
$maxTransientLoopErrors = 20

try {
    $null = Invoke-AdapterJson -Method "GET" -Uri "$base/health" -Headers $callerHeaders

    $sessionSnapshot = $null
    $sessionExists = $false
    try {
        $sessionSnapshot = Invoke-AdapterJson -Method "GET" -Uri "$base/api/v1/sessions/$([Uri]::EscapeDataString($SessionId))" -Headers $callerHeaders
        $sessionExists = $true
    }
    catch {
        $statusCode = Get-HttpStatusCode -Exception $_.Exception
        if ($statusCode -eq 404) {
            $sessionExists = $false
        }
        else {
            throw
        }
    }

    if (-not $sessionExists) {
        if (-not $EnsureSession) {
            throw "Session '$SessionId' was not found and EnsureSession=false."
        }

        $readyEndpointCard = Find-ReadyEndpointCard -BaseAddress $base -Headers $callerHeaders -PreferredEndpointId $EndpointId
        if ($null -eq $readyEndpointCard) {
            throw "Session '$SessionId' was not found and no reusable endpoint is available for auto-create."
        }

        $resolvedEndpointId = Get-ObjectPropertyString -InputObject $readyEndpointCard -Name "endpointId"
        $resolvedAgentId = Get-ObjectPropertyString -InputObject $readyEndpointCard -Name "agentId"
        if ([string]::IsNullOrWhiteSpace($resolvedEndpointId) -or [string]::IsNullOrWhiteSpace($resolvedAgentId)) {
            throw "Ready endpoint card is missing endpointId or agentId."
        }

        if ([string]::IsNullOrWhiteSpace($EndpointId)) {
            $EndpointId = $resolvedEndpointId
        }

        $sessionSnapshot = Invoke-AdapterJson -Method "POST" -Uri "$base/api/v1/sessions" -Headers $callerHeaders -Body @{
            sessionId = $SessionId
            profile = $Profile
            requestedBy = $PrincipalId
            targetAgentId = $resolvedAgentId
            targetEndpointId = $EndpointId
            shareMode = "Owner"
            tenantId = $TenantId
            organizationId = $OrganizationId
            transportProvider = "webrtc-datachannel"
        }

        $requestedByFromOpen = Get-ObjectPropertyString -InputObject $sessionSnapshot -Name "requestedBy"
        if (-not [string]::IsNullOrWhiteSpace($requestedByFromOpen)) {
            $effectivePrincipalId = $requestedByFromOpen
        }

        try {
            $null = Invoke-AdapterJson -Method "POST" -Uri "$base/api/v1/sessions/$([Uri]::EscapeDataString($SessionId))/control/request" -Headers $callerHeaders -Body @{
                participantId = "owner:$effectivePrincipalId"
                requestedBy = $effectivePrincipalId
                leaseSeconds = [Math]::Max(30, $LeaseSeconds)
                reason = "webrtc peer adapter auto-create"
                tenantId = $TenantId
                organizationId = $OrganizationId
            }
        }
        catch {
            Write-Warning "Auto-created session but failed to pre-request control lease: $($_.Exception.Message)"
        }

        $sessionCreated = $true
    }

    if ([string]::IsNullOrWhiteSpace($EndpointId)) {
        $EndpointId = Get-ObjectPropertyString -InputObject $sessionSnapshot -Name "endpointId"
    }
    $requestedByFromSession = Get-ObjectPropertyString -InputObject $sessionSnapshot -Name "requestedBy"
    if (-not [string]::IsNullOrWhiteSpace($requestedByFromSession)) {
        $effectivePrincipalId = $requestedByFromSession
    }
    if ([string]::IsNullOrWhiteSpace($EndpointId)) {
        throw "Session '$SessionId' does not have endpointId and no EndpointId was provided."
    }

    if ($StartExp022) {
        $runtime = Invoke-AdapterJson -Method "GET" -Uri "$base/api/v1/runtime/uart" -Headers $callerHeaders
        if ([string]::IsNullOrWhiteSpace($UartPort)) {
            $UartPort = Get-ObjectPropertyString -InputObject $runtime -Name "port"
        }

        if ($UartBaud -le 0) {
            $runtimeBaud = Get-ObjectPropertyValue -InputObject $runtime -Name "baudRate"
            if ($null -ne $runtimeBaud) {
                $UartBaud = [int]$runtimeBaud
            }
        }

        if ([string]::IsNullOrWhiteSpace($UartHmacKey)) {
            $UartHmacKey = if (-not [string]::IsNullOrWhiteSpace($env:HIDBRIDGE_UART_HMAC_KEY)) { $env:HIDBRIDGE_UART_HMAC_KEY } else { "changeme" }
        }

        if ([string]::IsNullOrWhiteSpace($UartPort)) {
            throw "StartExp022 requires UART port. Provide -UartPort or ensure /api/v1/runtime/uart returns one."
        }

        $wsUri = [Uri]$ControlWsUrl
        $controlPort = $wsUri.Port
        $controlBind = if ($wsUri.Host -eq "localhost") { "127.0.0.1" } else { $wsUri.Host }
        $exp022ScriptPath = Resolve-Exp022StartScriptPath -ExplicitPath $Exp022StartScriptPath
        $logsDir = Join-Path (Join-Path $scriptsRoot "..\.logs") ("webrtc-peer-adapter\" + (Get-Date -Format "yyyyMMdd-HHmmss"))
        $exp022DurationSec = if ($DurationSec -gt 0) { [Math]::Max($DurationSec + 30, 120) } else { 3600 }
        $started = Start-Exp022Process `
            -ScriptPath $exp022ScriptPath `
            -Port $UartPort `
            -Baud $UartBaud `
            -HmacKey $UartHmacKey `
            -ControlBind $controlBind `
            -ControlPort $controlPort `
            -DurationSec $exp022DurationSec `
            -LogsDir $logsDir

        $exp022Process = $started.Process
        $exp022Stdout = $started.Stdout
        $exp022Stderr = $started.Stderr
        Start-Sleep -Seconds 2
        if ($exp022Process.HasExited) {
            $stderrTail = Get-FileTailText -Path $exp022Stderr -LineCount 40
            if ([string]::IsNullOrWhiteSpace($stderrTail)) {
                throw "exp-022 process exited immediately with code $($exp022Process.ExitCode)."
            }

            throw "exp-022 process exited immediately with code $($exp022Process.ExitCode). Last stderr lines:`n$stderrTail"
        }
    }

    $null = Invoke-AdapterJson -Method "POST" -Uri "$base/api/v1/sessions/$([Uri]::EscapeDataString($SessionId))/transport/webrtc/peers/$([Uri]::EscapeDataString($PeerId))/online" -Headers $callerHeaders -Body @{
        endpointId = $EndpointId
        metadata = @{
            adapter = "exp022-peer-adapter"
            controlWsUrl = $ControlWsUrl
            principalId = $effectivePrincipalId
        }
    }

    do {
        if ($null -ne $exp022Process -and $exp022Process.HasExited) {
            $stderrTail = Get-FileTailText -Path $exp022Stderr -LineCount 40
            if ([string]::IsNullOrWhiteSpace($stderrTail)) {
                throw "exp-022 process exited unexpectedly with code $($exp022Process.ExitCode)."
            }

            throw "exp-022 process exited unexpectedly with code $($exp022Process.ExitCode). Last stderr lines:`n$stderrTail"
        }

        $queued = @()
        try {
            $nowUtc = [DateTimeOffset]::UtcNow
            if (($nowUtc - $lastHeartbeatAtUtc).TotalSeconds -ge [Math]::Max(3, $HeartbeatIntervalSec)) {
                $signalPayload = @{
                    utc = $nowUtc.ToString("O")
                    adapter = "exp022-peer-adapter"
                    peerId = $PeerId
                    endpointId = $EndpointId
                    principalId = $effectivePrincipalId
                } | ConvertTo-Json -Compress

                $null = Invoke-AdapterJson -Method "POST" -Uri "$base/api/v1/sessions/$([Uri]::EscapeDataString($SessionId))/transport/webrtc/signals" -Headers $callerHeaders -Body @{
                    kind = "Heartbeat"
                    senderPeerId = $PeerId
                    payload = $signalPayload
                }

                $signalQuery = "$base/api/v1/sessions/$([Uri]::EscapeDataString($SessionId))/transport/webrtc/signals?recipientPeerId=$([Uri]::EscapeDataString($PeerId))&limit=20"
                if ($null -ne $lastSignalSequence) {
                    $signalQuery += "&afterSequence=$lastSignalSequence"
                }

                $signals = Get-CollectionItems -Response (Invoke-AdapterJson -Method "GET" -Uri $signalQuery -Headers $callerHeaders)
                foreach ($signal in $signals) {
                    $sequence = Get-ObjectPropertyValue -InputObject $signal -Name "sequence"
                    if ($null -ne $sequence) {
                        $lastSignalSequence = [int]$sequence
                    }
                }

                $lastHeartbeatAtUtc = $nowUtc
            }

            $commandsQuery = "$base/api/v1/sessions/$([Uri]::EscapeDataString($SessionId))/transport/webrtc/commands?peerId=$([Uri]::EscapeDataString($PeerId))&limit=$([Math]::Max(1, $BatchLimit))"
            if ($null -ne $lastCommandSequence) {
                $commandsQuery += "&afterSequence=$lastCommandSequence"
            }

            $queued = Get-CollectionItems -Response (Invoke-AdapterJson -Method "GET" -Uri $commandsQuery -Headers $callerHeaders)
            $transientLoopErrorCount = 0
        }
        catch {
            $transientLoopErrorCount++
            Write-Warning "Adapter loop transient error ($transientLoopErrorCount/$maxTransientLoopErrors): $($_.Exception.Message)"
            if ($transientLoopErrorCount -ge $maxTransientLoopErrors) {
                throw "Adapter loop failed $transientLoopErrorCount times in a row. Last error: $($_.Exception.Message)"
            }

            Start-Sleep -Milliseconds ([Math]::Max(500, $PollIntervalMs))
            continue
        }

        foreach ($envelope in $queued | Sort-Object { [int](Get-ObjectPropertyValue -InputObject $_ -Name "sequence") }) {
            $sequence = [int](Get-ObjectPropertyValue -InputObject $envelope -Name "sequence")
            $command = Get-ObjectPropertyValue -InputObject $envelope -Name "command"
            if ($null -eq $command) {
                $lastCommandSequence = $sequence
                continue
            }

            $commandId = Get-ObjectPropertyString -InputObject $command -Name "commandId"
            if ([string]::IsNullOrWhiteSpace($commandId)) {
                $lastCommandSequence = $sequence
                continue
            }

            $action = Get-ObjectPropertyString -InputObject $command -Name "action"
            $timeoutMs = Get-ObjectPropertyValue -InputObject $command -Name "timeoutMs"
            $effectiveTimeoutMs = if ($null -eq $timeoutMs) { [Math]::Max(1000, $CommandTimeoutMs) } else { [Math]::Max(1000, [int]$timeoutMs) }
            $processedCount++
            $ackStatus = "Applied"
            $ackError = $null
            $metrics = @{
                relayAdapterSequence = [double]$sequence
            }

            try {
                $sw = [System.Diagnostics.Stopwatch]::StartNew()
                $payload = Convert-RelayCommandToExp022Payload -Command $command
                $exec = Invoke-Exp022ControlCommand -WsUrl $ControlWsUrl -Payload $payload -TimeoutMs $effectiveTimeoutMs
                $sw.Stop()
                $metrics["relayAdapterWsRoundtripMs"] = [double]$sw.Elapsed.TotalMilliseconds

                $execOkValue = Get-ObjectPropertyValue -InputObject $exec -Name "ok"
                $execOk = $false
                if ($null -ne $execOkValue) {
                    try {
                        $execOk = [System.Convert]::ToBoolean($execOkValue)
                    }
                    catch {
                        $execOk = $false
                    }
                }

                $execError = Get-ObjectPropertyString -InputObject $exec -Name "error"
                $execRaw = Get-ObjectPropertyString -InputObject $exec -Name "raw"

                if ($execOk) {
                    $appliedCount++
                    $ackStatus = "Applied"
                }
                else {
                    $rejectedCount++
                    $ackStatus = "Rejected"
                    $okValueType = if ($null -eq $execOkValue) { "<null>" } else { $execOkValue.GetType().FullName }
                    Write-Warning "WebRTC peer non-success command '$commandId': okValue='$execOkValue' okValueType='$okValueType' error='$execError' raw='$execRaw'"
                    $ackError = @{
                        domain = "Command"
                        code = "E_WEBRTC_PEER_EXECUTION_FAILED"
                        message = if ([string]::IsNullOrWhiteSpace($execError)) {
                            if ([string]::IsNullOrWhiteSpace($execRaw)) {
                                "Peer returned non-success response."
                            }
                            else {
                                "Peer returned non-success response. Raw: $execRaw"
                            }
                        } else { $execError }
                        retryable = $false
                    }
                }
            }
            catch [System.OperationCanceledException] {
                $timeoutCount++
                $ackStatus = "Timeout"
                $ackError = @{
                    domain = "Transport"
                    code = "E_TRANSPORT_TIMEOUT"
                    message = "WebRTC peer adapter timeout while executing command '$action'."
                    retryable = $true
                }
            }
            catch {
                $rejectedCount++
                $ackStatus = "Rejected"
                $ackError = @{
                    domain = "Command"
                    code = "E_WEBRTC_PEER_ADAPTER_FAILURE"
                    message = $_.Exception.Message
                    retryable = $false
                }
            }

            $ackBody = @{
                status = $ackStatus
                metrics = $metrics
            }

            if ($null -ne $ackError) {
                $ackBody["error"] = $ackError
            }

            $null = Invoke-AdapterJson `
                -Method "POST" `
                -Uri "$base/api/v1/sessions/$([Uri]::EscapeDataString($SessionId))/transport/webrtc/commands/$([Uri]::EscapeDataString($commandId))/ack" `
                -Headers $callerHeaders `
                -Body $ackBody

            $lastCommandSequence = $sequence
        }

        if ($SingleRun) {
            break
        }

        if ($DurationSec -gt 0 -and ([DateTimeOffset]::UtcNow - $startedAtUtc).TotalSeconds -ge $DurationSec) {
            break
        }

        Start-Sleep -Milliseconds ([Math]::Max(100, $PollIntervalMs))
    } while ($true)
}
finally {
    try {
        if (-not [string]::IsNullOrWhiteSpace($SessionId) -and -not [string]::IsNullOrWhiteSpace($PeerId)) {
            $null = Invoke-AdapterJson -Method "POST" -Uri "$base/api/v1/sessions/$([Uri]::EscapeDataString($SessionId))/transport/webrtc/peers/$([Uri]::EscapeDataString($PeerId))/offline" -Headers $callerHeaders
        }
    }
    catch {
        Write-Warning "Failed to mark peer offline: $($_.Exception.Message)"
    }

    if ($null -ne $exp022Process -and -not $exp022Process.HasExited) {
        try {
            Stop-Process -Id $exp022Process.Id -Force -ErrorAction Stop
        }
        catch {
            Write-Warning "Failed to stop exp-022 process $($exp022Process.Id): $($_.Exception.Message)"
        }
    }
}

$summary = [ordered]@{
    baseUrl = $BaseUrl
    sessionId = $SessionId
    ensureSession = $EnsureSession
    sessionCreated = $sessionCreated
    peerId = $PeerId
    effectivePrincipalId = $effectivePrincipalId
    endpointId = $EndpointId
    controlWsUrl = $ControlWsUrl
    processedCommands = $processedCount
    applied = $appliedCount
    rejected = $rejectedCount
    timedOut = $timeoutCount
    startedAtUtc = $startedAtUtc
    finishedAtUtc = [DateTimeOffset]::UtcNow
    exp022ProcessId = if ($null -ne $exp022Process) { $exp022Process.Id } else { $null }
    exp022Stdout = $exp022Stdout
    exp022Stderr = $exp022Stderr
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

    ($summary | ConvertTo-Json -Depth 20) | Set-Content -Path $outputPath -Encoding UTF8
}

Write-Host ""
Write-Host "=== WebRTC Peer Adapter Summary ==="
Write-Host "BaseUrl: $BaseUrl"
Write-Host "Session: $SessionId"
Write-Host "Session auto-create enabled: $EnsureSession"
Write-Host "Session created by adapter: $sessionCreated"
Write-Host "Peer: $PeerId"
Write-Host "Effective principal: $effectivePrincipalId"
Write-Host "Endpoint: $EndpointId"
Write-Host "Control WS: $ControlWsUrl"
Write-Host "Commands: processed=$processedCount, applied=$appliedCount, rejected=$rejectedCount, timedOut=$timeoutCount"
if ($null -ne $exp022Process) {
    Write-Host "exp-022 process: $($exp022Process.Id)"
}
if (-not [string]::IsNullOrWhiteSpace($exp022Stdout)) {
    Write-Host "exp-022 stdout: $exp022Stdout"
}
if (-not [string]::IsNullOrWhiteSpace($exp022Stderr)) {
    Write-Host "exp-022 stderr: $exp022Stderr"
}
if (-not [string]::IsNullOrWhiteSpace($OutputJsonPath)) {
    Write-Host "Summary JSON: $OutputJsonPath"
}

exit 0

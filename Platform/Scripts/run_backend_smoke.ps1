param(
    [ValidateSet("File", "Sql")]
    [string]$Provider = "File",
    [string]$Configuration = "Debug",
    [string]$ConnectionString = "Host=127.0.0.1;Port=5434;Database=hidbridge;Username=hidbridge;Password=hidbridge",
    [string]$Schema = "hidbridge",
    [string]$BaseUrl = "http://127.0.0.1:18093",
    [string]$DataRoot = "",
    [string]$AccessToken = "",
    [switch]$EnableApiAuth,
    [string]$AuthAuthority = "",
    [string]$AuthAudience = "",
    [string]$TokenClientId = "controlplane-smoke",
    [string]$TokenClientSecret = "",
    [string]$TokenScope = "openid profile email",
    [string]$TokenUsername = "operator.smoke.admin",
    [string]$TokenPassword = "ChangeMe123!",
    [string]$ViewerTokenUsername = "operator.smoke.viewer",
    [string]$ViewerTokenPassword = "ChangeMe123!",
    [string]$ForeignTokenUsername = "operator.smoke.foreign",
    [string]$ForeignTokenPassword = "ChangeMe123!",
    [switch]$DisableHeaderFallback,
    [switch]$BearerOnly,
    [switch]$NoBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$scriptsRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$platformRoot = Split-Path -Parent $scriptsRoot
$repoRoot = Split-Path -Parent $platformRoot
$projectPath = Join-Path $repoRoot "Platform/Platform/HidBridge.ControlPlane.Api/HidBridge.ControlPlane.Api.csproj"
$lastStep = "initialization"

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

function Invoke-Json {
    param(
        [string]$Method,
        [string]$Uri,
        [object]$Body = $null,
        [hashtable]$Headers = @{}
    )

    if ($null -eq $Body) {
        try {
            return Invoke-RestMethod -Method $Method -Uri $Uri -Headers $Headers -TimeoutSec 20
        }
        catch {
            $responseBody = Get-ErrorResponseBody $_.Exception
            if ([string]::IsNullOrWhiteSpace($responseBody)) {
                throw "HTTP $Method $Uri failed: $($_.Exception.Message)"
            }

            throw "HTTP $Method $Uri failed: $($_.Exception.Message)`n$responseBody"
        }
    }

    $json = $Body | ConvertTo-Json -Depth 20
    try {
        return Invoke-RestMethod -Method $Method -Uri $Uri -Body $json -Headers $Headers -ContentType "application/json" -TimeoutSec 20
    }
    catch {
        $responseBody = Get-ErrorResponseBody $_.Exception
        if ([string]::IsNullOrWhiteSpace($responseBody)) {
            throw "HTTP $Method $Uri failed: $($_.Exception.Message)`nRequest body: $json"
        }

        throw "HTTP $Method $Uri failed: $($_.Exception.Message)`nRequest body: $json`n$responseBody"
    }
}

function Invoke-ExpectedJsonFailure {
    param(
        [string]$Method,
        [string]$Uri,
        [int]$ExpectedStatusCode,
        [object]$Body = $null,
        [hashtable]$Headers = @{}
    )

    try {
        if ($null -eq $Body) {
            $null = Invoke-RestMethod -Method $Method -Uri $Uri -Headers $Headers -TimeoutSec 20
        }
        else {
            $json = $Body | ConvertTo-Json -Depth 20
            $null = Invoke-RestMethod -Method $Method -Uri $Uri -Body $json -Headers $Headers -ContentType "application/json" -TimeoutSec 20
        }

        throw "Expected HTTP $ExpectedStatusCode for $Method $Uri but the request succeeded."
    }
    catch {
        $responseProperty = $_.Exception.PSObject.Properties["Response"]
        $response = if ($null -ne $responseProperty) { $responseProperty.Value } else { $null }
        if ($null -eq $response) {
            throw
        }

        $actualStatusCode = [int]$response.StatusCode
        if ($actualStatusCode -ne $ExpectedStatusCode) {
            $responseBody = Get-ErrorResponseBody $_.Exception
            throw "Expected HTTP $ExpectedStatusCode for $Method $Uri, got $actualStatusCode.`n$responseBody"
        }

        $responseBody = Get-ErrorResponseBody $_.Exception
        if ([string]::IsNullOrWhiteSpace($responseBody)) {
            throw "Expected a JSON error payload for $Method $Uri."
        }

        return $responseBody | ConvertFrom-Json
    }
}

function Assert-True {
    param(
        [bool]$Condition,
        [string]$Message
    )

    if (-not $Condition) {
        throw $Message
    }
}

function Test-IsJwtToken {
    param([string]$Value)

    return $Value -match '^[A-Za-z0-9\-_]+\.[A-Za-z0-9\-_]+\.[A-Za-z0-9\-_]+$'
}

function Get-TokenMode {
    param([string]$Value)

    if ([string]::IsNullOrWhiteSpace($Value) -or $Value -eq "<token>" -or $Value -eq "auto") {
        return "auto"
    }

    if (Test-IsJwtToken $Value) {
        return "provided"
    }

    return "auto"
}

function Get-OidcAccessToken {
    param(
        [string]$Authority,
        [string]$ClientId,
        [string]$ClientSecret,
        [string]$Scope,
        [string]$Username,
        [string]$Password
    )

    $tokenEndpoint = "$($Authority.TrimEnd('/'))/protocol/openid-connect/token"
    $tokenRequest = @{
        grant_type = "password"
        client_id = $ClientId
        username = $Username
        password = $Password
    }

    if (-not [string]::IsNullOrWhiteSpace($Scope)) {
        $tokenRequest.scope = $Scope
    }

    if (-not [string]::IsNullOrWhiteSpace($ClientSecret)) {
        $tokenRequest.client_secret = $ClientSecret
    }

    try {
        $response = Invoke-RestMethod -Method Post -Uri $tokenEndpoint -ContentType "application/x-www-form-urlencoded" -Body $tokenRequest -TimeoutSec 20
    }
    catch {
        $responseBody = Get-ErrorResponseBody $_.Exception
        if ([string]::IsNullOrWhiteSpace($responseBody)) {
            throw "Failed to acquire access token for $Username from ${tokenEndpoint}: $($_.Exception.Message)"
        }

        throw "Failed to acquire access token for $Username from ${tokenEndpoint}: $($_.Exception.Message)`n$responseBody"
    }

    if ([string]::IsNullOrWhiteSpace($response.access_token)) {
        throw "OIDC token response for $Username did not contain access_token."
    }

    return $response.access_token
}

function Get-Count {
    param([object]$Value)

    if ($null -eq $Value) {
        return 0
    }

    if ($Value -is [System.Array]) {
        return $Value.Length
    }

    if ($Value -is [System.Collections.ICollection]) {
        return $Value.Count
    }

    $enumerable = $Value -as [System.Collections.IEnumerable]
    if ($null -eq $enumerable) {
        return 1
    }

    $count = 0
    foreach ($item in $enumerable) {
        $count++
    }

    return $count
}

function Get-ObjectPropertyString {
    param(
        [object]$InputObject,
        [string]$Name
    )

    if ($null -eq $InputObject -or [string]::IsNullOrWhiteSpace($Name)) {
        return ""
    }

    $property = $InputObject.PSObject.Properties[$Name]
    if ($null -eq $property -or $null -eq $property.Value) {
        return ""
    }

    return [string]$property.Value
}

function Get-SessionIdValue {
    param([object]$InputObject)

    $sessionId = Get-ObjectPropertyString -InputObject $InputObject -Name "sessionId"
    if (-not [string]::IsNullOrWhiteSpace($sessionId)) {
        return $sessionId
    }

    return Get-ObjectPropertyString -InputObject $InputObject -Name "SessionId"
}

function New-CallerHeaders {
    param(
        [string]$SubjectId,
        [string]$PrincipalId,
        [string[]]$Roles,
        [string]$TenantId = "local-tenant",
        [string]$OrganizationId = "local-org"
    )

    $headers = @{}

    if (-not $DisableHeaderFallback) {
        $headers["X-HidBridge-UserId"] = $SubjectId
        $headers["X-HidBridge-PrincipalId"] = $PrincipalId
        $headers["X-HidBridge-TenantId"] = $TenantId
        $headers["X-HidBridge-OrganizationId"] = $OrganizationId

        if ($Roles.Count -gt 0) {
            $headers["X-HidBridge-Role"] = ($Roles -join ",")
        }
    }

    if (-not [string]::IsNullOrWhiteSpace($AccessToken) -and (Test-IsJwtToken $AccessToken)) {
        $headers["Authorization"] = "Bearer $AccessToken"
    }

    return $headers
}

if (-not $NoBuild) {
    & dotnet build "Platform/HidBridge.Platform.sln" -c $Configuration -v minimal -m:1 -nodeReuse:false
    if ($LASTEXITCODE -ne 0) {
        throw "Build failed."
    }
}

if ([string]::IsNullOrWhiteSpace($DataRoot)) {
    $DataRoot = Join-Path $platformRoot ".smoke-data/$Provider/$([DateTimeOffset]::UtcNow.ToString('yyyyMMdd-HHmmss'))"
}

New-Item -ItemType Directory -Force -Path $DataRoot | Out-Null
$tokenMode = Get-TokenMode $AccessToken
$multiActorBearerMode = $false
$ownerAccessToken = $null
$viewerAccessToken = $null
$foreignAccessToken = $null

if ($BearerOnly) {
    if ($tokenMode -eq "provided") {
        $ownerAccessToken = $AccessToken
    }
    else {
        if ([string]::IsNullOrWhiteSpace($AuthAuthority)) {
            throw "BearerOnly smoke in auto-token mode requires -AuthAuthority."
        }

        $ownerAccessToken = Get-OidcAccessToken -Authority $AuthAuthority -ClientId $TokenClientId -ClientSecret $TokenClientSecret -Username $TokenUsername -Password $TokenPassword
        $viewerAccessToken = Get-OidcAccessToken -Authority $AuthAuthority -ClientId $TokenClientId -ClientSecret $TokenClientSecret -Username $ViewerTokenUsername -Password $ViewerTokenPassword
        $foreignAccessToken = Get-OidcAccessToken -Authority $AuthAuthority -ClientId $TokenClientId -ClientSecret $TokenClientSecret -Username $ForeignTokenUsername -Password $ForeignTokenPassword
        $multiActorBearerMode = $true
    }
}

$env:HIDBRIDGE_PERSISTENCE_PROVIDER = $Provider
$env:HIDBRIDGE_DATA_ROOT = $DataRoot
$env:HIDBRIDGE_SQL_CONNECTION = $ConnectionString
$env:HIDBRIDGE_SQL_SCHEMA = $Schema
$env:HIDBRIDGE_SQL_APPLY_MIGRATIONS = "true"
$env:HIDBRIDGE_AUTH_ENABLED = if ($EnableApiAuth.IsPresent) { "true" } else { "false" }
$env:HIDBRIDGE_AUTH_AUTHORITY = $AuthAuthority
$env:HIDBRIDGE_AUTH_AUDIENCE = $AuthAudience
$env:HIDBRIDGE_AUTH_ALLOW_HEADER_FALLBACK = if ($DisableHeaderFallback.IsPresent) { "false" } else { "true" }
$env:HIDBRIDGE_AUTH_REQUIRE_HTTPS_METADATA = if ($EnableApiAuth.IsPresent -and $AuthAuthority -like "http://*") { "false" } else { "true" }

$runId = [Guid]::NewGuid()
$sessionId = "smoke-$($Provider.ToLowerInvariant())-$($runId.ToString('N').Substring(0, 8))"
$process = $null
$smokeExitCode = 0
$stdoutLog = Join-Path $DataRoot "controlplane.stdout.log"
$stderrLog = Join-Path $DataRoot "controlplane.stderr.log"
$resultLog = Join-Path $DataRoot "smoke.result.json"
$failureLog = Join-Path $DataRoot "smoke.failure.log"
$summaryLog = Join-Path $DataRoot "smoke.summary.txt"

try {
    $lastStep = "starting API process"
    $arguments = @(
        "run",
        "--project", $projectPath,
        "-c", $Configuration,
        "--no-build",
        "--no-launch-profile"
    )

    $process = Start-Process -FilePath "dotnet" -ArgumentList $arguments -WorkingDirectory $repoRoot -PassThru -RedirectStandardOutput $stdoutLog -RedirectStandardError $stderrLog

    $health = $null
    for ($attempt = 0; $attempt -lt 40; $attempt++) {
        Start-Sleep -Milliseconds 500
        try {
            $lastStep = "health probe"
            $health = Invoke-Json -Method "GET" -Uri "$BaseUrl/health"
            break
        }
        catch {
            if ($process.HasExited) {
                throw "ControlPlane API terminated before health probe succeeded."
            }
        }
    }

    if ($null -eq $health) {
        throw "Health probe did not succeed within the timeout window."
    }

    if ($BearerOnly) {
        if (-not $DisableHeaderFallback) {
            $ownerHeaders = New-CallerHeaders -SubjectId "user-smoke-owner" -PrincipalId "smoke-runner" -Roles @("operator.admin")
            $controllerHeaders = New-CallerHeaders -SubjectId "user-smoke-controller" -PrincipalId "controller-user" -Roles @("operator.viewer")
            $observerHeaders = New-CallerHeaders -SubjectId "user-smoke-observer" -PrincipalId "observer-user" -Roles @("operator.viewer")
            $foreignHeaders = New-CallerHeaders -SubjectId "user-foreign-viewer" -PrincipalId "foreign-user" -TenantId "foreign-tenant" -OrganizationId "foreign-org" -Roles @("operator.viewer")

            if ($multiActorBearerMode) {
                $ownerHeaders["Authorization"] = "Bearer $ownerAccessToken"
                $controllerHeaders["Authorization"] = "Bearer $viewerAccessToken"
                $observerHeaders["Authorization"] = "Bearer $viewerAccessToken"
                $foreignHeaders["Authorization"] = "Bearer $foreignAccessToken"
            }
            else {
                $ownerHeaders["Authorization"] = "Bearer $ownerAccessToken"
                $controllerHeaders = @{} + $ownerHeaders
                $observerHeaders = @{} + $ownerHeaders
                $foreignHeaders = @{} + $ownerHeaders
            }
        }
        elseif ($multiActorBearerMode) {
            $ownerHeaders = @{ Authorization = "Bearer $ownerAccessToken" }
            $controllerHeaders = @{ Authorization = "Bearer $viewerAccessToken" }
            $observerHeaders = @{ Authorization = "Bearer $viewerAccessToken" }
            $foreignHeaders = @{ Authorization = "Bearer $foreignAccessToken" }
        }
        else {
            $ownerHeaders = @{ Authorization = "Bearer $ownerAccessToken" }
            $controllerHeaders = $ownerHeaders
            $observerHeaders = $ownerHeaders
            $foreignHeaders = $ownerHeaders
        }
    }
    else {
        $ownerHeaders = New-CallerHeaders -SubjectId "user-smoke-owner" -PrincipalId "smoke-runner" -Roles @("operator.admin")
        $controllerHeaders = New-CallerHeaders -SubjectId "user-smoke-controller" -PrincipalId "controller-user" -Roles @("operator.viewer")
        $observerHeaders = New-CallerHeaders -SubjectId "user-smoke-observer" -PrincipalId "observer-user" -Roles @("operator.viewer")
        $foreignHeaders = New-CallerHeaders -SubjectId "user-foreign-viewer" -PrincipalId "foreign-user" -TenantId "foreign-tenant" -OrganizationId "foreign-org" -Roles @("operator.viewer")
    }

    $lastStep = "runtime probe"
    $runtime = Invoke-Json -Method "GET" -Uri "$BaseUrl/api/v1/runtime/uart" -Headers $ownerHeaders
    Assert-True ($runtime.persistenceProvider -eq $Provider) "Runtime provider mismatch. Expected '$Provider', got '$($runtime.persistenceProvider)'."

    $lastStep = "agent inventory"
    $agents = Invoke-Json -Method "GET" -Uri "$BaseUrl/api/v1/agents" -Headers $ownerHeaders
    $lastStep = "endpoint inventory"
    $endpoints = Invoke-Json -Method "GET" -Uri "$BaseUrl/api/v1/endpoints" -Headers $ownerHeaders
    $lastStep = "initial audit stream"
    $auditBefore = Invoke-Json -Method "GET" -Uri "$BaseUrl/api/v1/events/audit" -Headers $ownerHeaders

    Assert-True ((Get-Count $agents) -ge 1) "Expected at least one registered agent."
    Assert-True ((Get-Count $endpoints) -ge 1) "Expected at least one endpoint snapshot."
    Assert-True ((Get-Count $auditBefore) -ge 1) "Expected at least one audit record after startup."

    $participantId = "participant-observer-$($runId.ToString('N').Substring(0, 8))"
    $shareId = "share-observer-$($runId.ToString('N').Substring(0, 8))"
    $invitationShareId = "invite-controller-$($runId.ToString('N').Substring(0, 8))"
    $commandId = "cmd-noop-$($runId.ToString('N').Substring(0, 8))"
    $lastStep = "open session"
    $session = Invoke-Json -Method "POST" -Uri "$BaseUrl/api/v1/sessions" -Headers $ownerHeaders -Body @{
        sessionId = $sessionId
        profile = "UltraLowLatency"
        requestedBy = "smoke-runner"
        targetAgentId = "agent_hidbridge_uart_local"
        targetEndpointId = "endpoint_local_demo"
        shareMode = "Owner"
        tenantId = "local-tenant"
        organizationId = "local-org"
    }

    $lastStep = "upsert participant"
    $participant = Invoke-Json -Method "POST" -Uri "$BaseUrl/api/v1/sessions/$sessionId/participants" -Headers $ownerHeaders -Body @{
        participantId = $participantId
        principalId = "observer-user"
        role = "Observer"
        addedBy = "smoke-runner"
        tenantId = "local-tenant"
        organizationId = "local-org"
    }

    $lastStep = "grant share"
    $share = Invoke-Json -Method "POST" -Uri "$BaseUrl/api/v1/sessions/$sessionId/shares" -Headers $ownerHeaders -Body @{
        shareId = $shareId
        principalId = "observer-user"
        grantedBy = "smoke-runner"
        role = "Observer"
        tenantId = "local-tenant"
        organizationId = "local-org"
    }

    $lastStep = "request invitation"
    $requestedInvitation = Invoke-Json -Method "POST" -Uri "$BaseUrl/api/v1/sessions/$sessionId/invitations/requests" -Headers $controllerHeaders -Body @{
        shareId = $invitationShareId
        principalId = "controller-user"
        requestedBy = "controller-user"
        requestedRole = "Controller"
        message = "Need temporary control"
        tenantId = "local-tenant"
        organizationId = "local-org"
    }

    $viewerDeniedApproval = $null
    if (-not $BearerOnly -or $multiActorBearerMode) {
        $lastStep = "viewer moderation denial"
        $viewerDeniedApproval = Invoke-ExpectedJsonFailure -Method "POST" -Uri "$BaseUrl/api/v1/sessions/$sessionId/invitations/$invitationShareId/approve" -ExpectedStatusCode 403 -Headers $controllerHeaders -Body @{
            shareId = $invitationShareId
            actedBy = "controller-user"
            grantedRole = "Controller"
            reason = "viewer should be denied"
            tenantId = "local-tenant"
            organizationId = "local-org"
        }
    }

    $lastStep = "approve invitation"
    $approvedInvitation = Invoke-Json -Method "POST" -Uri "$BaseUrl/api/v1/sessions/$sessionId/invitations/$invitationShareId/approve" -Headers $ownerHeaders -Body @{
        shareId = $invitationShareId
        actedBy = "smoke-runner"
        grantedRole = "Controller"
        reason = "approved by smoke runner"
        tenantId = "local-tenant"
        organizationId = "local-org"
    }

    $lastStep = "accept approved invitation"
    $acceptedInvitation = Invoke-Json -Method "POST" -Uri "$BaseUrl/api/v1/sessions/$sessionId/shares/$invitationShareId/accept" -Headers $controllerHeaders -Body @{
        shareId = $invitationShareId
        actedBy = "controller-user"
        reason = "joining approved invitation"
        tenantId = "local-tenant"
        organizationId = "local-org"
    }

    $controllerParticipantId = "share:$invitationShareId"

    $lastStep = "request control"
    $controlLease = Invoke-Json -Method "POST" -Uri "$BaseUrl/api/v1/sessions/$sessionId/control/request" -Headers $controllerHeaders -Body @{
        participantId = $controllerParticipantId
        requestedBy = "controller-user"
        leaseSeconds = 30
        reason = "smoke control request"
        tenantId = "local-tenant"
        organizationId = "local-org"
    }

    $ownerParticipantId = "owner:smoke-runner"

    $lastStep = "request control conflict"
    $requestControlConflict = Invoke-ExpectedJsonFailure -Method "POST" -Uri "$BaseUrl/api/v1/sessions/$sessionId/control/request" -ExpectedStatusCode 409 -Headers $ownerHeaders -Body @{
        participantId = $ownerParticipantId
        requestedBy = "smoke-runner"
        leaseSeconds = 30
        reason = "owner should wait for active lease"
        tenantId = "local-tenant"
        organizationId = "local-org"
    }

    $lastStep = "grant control not eligible conflict"
    $notEligibleControlConflict = Invoke-ExpectedJsonFailure -Method "POST" -Uri "$BaseUrl/api/v1/sessions/$sessionId/control/grant" -ExpectedStatusCode 409 -Headers $ownerHeaders -Body @{
        participantId = $participantId
        grantedBy = "smoke-runner"
        leaseSeconds = 30
        reason = "observer should be ineligible"
        tenantId = "local-tenant"
        organizationId = "local-org"
    }

    $lastStep = "release control mismatch conflict"
    $releaseMismatchConflict = Invoke-ExpectedJsonFailure -Method "POST" -Uri "$BaseUrl/api/v1/sessions/$sessionId/control/release" -ExpectedStatusCode 409 -Headers $ownerHeaders -Body @{
        participantId = $ownerParticipantId
        actedBy = "smoke-runner"
        reason = "release target mismatch"
        tenantId = "local-tenant"
        organizationId = "local-org"
    }

    $lastStep = "read active control lease"
    $activeControl = Invoke-Json -Method "GET" -Uri "$BaseUrl/api/v1/sessions/$sessionId/control" -Headers $controllerHeaders

    $lastStep = "dispatch command"
    $commandAck = Invoke-Json -Method "POST" -Uri "$BaseUrl/api/v1/sessions/$sessionId/commands" -Headers $controllerHeaders -Body @{
        commandId = $commandId
        sessionId = $sessionId
        channel = "Hid"
        action = "noop"
        args = @{
            principalId = "controller-user"
            participantId = $controllerParticipantId
            shareId = $invitationShareId
        }
        timeoutMs = 250
        idempotencyKey = $commandId
        tenantId = "local-tenant"
        organizationId = "local-org"
    }

    $lastStep = "release control"
    $releasedControl = Invoke-Json -Method "POST" -Uri "$BaseUrl/api/v1/sessions/$sessionId/control/release" -Headers $controllerHeaders -Body @{
        participantId = $controllerParticipantId
        actedBy = "controller-user"
        reason = "smoke release"
        tenantId = "local-tenant"
        organizationId = "local-org"
    }

    $lastStep = "force takeover control"
    $takeoverControl = Invoke-Json -Method "POST" -Uri "$BaseUrl/api/v1/sessions/$sessionId/control/force-takeover" -Headers $ownerHeaders -Body @{
        participantId = $ownerParticipantId
        grantedBy = "smoke-runner"
        leaseSeconds = 30
        reason = "owner takeover"
        tenantId = "local-tenant"
        organizationId = "local-org"
    }

    $foreignDeniedSummary = $null
    if (-not $BearerOnly -or $multiActorBearerMode) {
        $lastStep = "foreign tenant denial"
        $foreignDeniedSummary = Invoke-ExpectedJsonFailure -Method "GET" -Uri "$BaseUrl/api/v1/collaboration/sessions/$sessionId/summary" -ExpectedStatusCode 403 -Headers $foreignHeaders
    }

    $lastStep = "list sessions"
    $sessions = Invoke-Json -Method "GET" -Uri "$BaseUrl/api/v1/sessions" -Headers $ownerHeaders
    $lastStep = "list participants"
    $participants = Invoke-Json -Method "GET" -Uri "$BaseUrl/api/v1/sessions/$sessionId/participants" -Headers $ownerHeaders
    $lastStep = "list shares"
    $shares = Invoke-Json -Method "GET" -Uri "$BaseUrl/api/v1/sessions/$sessionId/shares" -Headers $ownerHeaders
    $lastStep = "list invitations"
    $invitations = Invoke-Json -Method "GET" -Uri "$BaseUrl/api/v1/sessions/$sessionId/invitations" -Headers $ownerHeaders
    $lastStep = "read collaboration summary"
    $summary = Invoke-Json -Method "GET" -Uri "$BaseUrl/api/v1/collaboration/sessions/$sessionId/summary" -Headers $ownerHeaders
    $lastStep = "read collaboration dashboard"
    $dashboard = Invoke-Json -Method "GET" -Uri "$BaseUrl/api/v1/collaboration/sessions/$sessionId/dashboard" -Headers $ownerHeaders
    $lastStep = "read lobby"
    $lobby = Invoke-Json -Method "GET" -Uri "$BaseUrl/api/v1/collaboration/sessions/$sessionId/lobby" -Headers $ownerHeaders
    $lastStep = "read control dashboard"
    $controlDashboard = Invoke-Json -Method "GET" -Uri "$BaseUrl/api/v1/collaboration/sessions/$sessionId/control/dashboard" -Headers $ownerHeaders
    $lastStep = "read inventory dashboard"
    $inventoryDashboard = Invoke-Json -Method "GET" -Uri "$BaseUrl/api/v1/dashboards/inventory" -Headers $ownerHeaders
    $lastStep = "read audit dashboard"
    $auditDashboard = Invoke-Json -Method "GET" -Uri "$BaseUrl/api/v1/dashboards/audit?take=20" -Headers $ownerHeaders
    $lastStep = "read telemetry dashboard"
    $telemetryDashboard = Invoke-Json -Method "GET" -Uri "$BaseUrl/api/v1/dashboards/telemetry?take=20" -Headers $ownerHeaders
    $lastStep = "query session projections"
    # Do not pin to state=Active here: with failure/recovery hardening, a just-tested
    # room can legitimately be Recovering/Degraded during smoke execution.
    $sessionProjection = Invoke-Json -Method "GET" -Uri "$BaseUrl/api/v1/projections/sessions?principalId=smoke-runner&take=50" -Headers $ownerHeaders
    $lastStep = "query endpoint projections"
    # Do not constrain endpoint status to Active: during recovery hardening an endpoint
    # can be temporarily Recovering/Degraded while still being the correct smoke target.
    $endpointProjection = Invoke-Json -Method "GET" -Uri "$BaseUrl/api/v1/projections/endpoints?take=50" -Headers $ownerHeaders
    $lastStep = "query audit projections"
    $auditProjection = Invoke-Json -Method "GET" -Uri "$BaseUrl/api/v1/projections/audit?sessionId=${sessionId}&take=20" -Headers $ownerHeaders
    $lastStep = "query telemetry projections"
    $telemetryProjection = Invoke-Json -Method "GET" -Uri "$BaseUrl/api/v1/projections/telemetry?sessionId=${sessionId}&scope=command&take=20" -Headers $ownerHeaders
    $lastStep = "read replay bundle"
    $replayBundle = Invoke-Json -Method "GET" -Uri "$BaseUrl/api/v1/diagnostics/replay/sessions/${sessionId}?take=20" -Headers $ownerHeaders
    $lastStep = "read archive summary"
    $archiveSummary = Invoke-Json -Method "GET" -Uri "$BaseUrl/api/v1/diagnostics/archive/summary?sessionId=${sessionId}" -Headers $ownerHeaders
    $lastStep = "read archive audit slice"
    $archiveAudit = Invoke-Json -Method "GET" -Uri "$BaseUrl/api/v1/diagnostics/archive/audit?sessionId=${sessionId}&take=20" -Headers $ownerHeaders
    $lastStep = "read archive telemetry slice"
    $archiveTelemetry = Invoke-Json -Method "GET" -Uri "$BaseUrl/api/v1/diagnostics/archive/telemetry?sessionId=${sessionId}&scope=command&take=20" -Headers $ownerHeaders
    $lastStep = "read archive command slice"
    $archiveCommands = Invoke-Json -Method "GET" -Uri "$BaseUrl/api/v1/diagnostics/archive/commands?sessionId=${sessionId}&take=20" -Headers $ownerHeaders
    $lastStep = "read command journal"
    $commandJournal = Invoke-Json -Method "GET" -Uri "$BaseUrl/api/v1/sessions/$sessionId/commands/journal" -Headers $ownerHeaders
    $lastStep = "read timeline"
    $timeline = Invoke-Json -Method "GET" -Uri "$BaseUrl/api/v1/events/timeline/${sessionId}?take=20" -Headers $ownerHeaders
    $lastStep = "read final audit stream"
    $auditAfter = Invoke-Json -Method "GET" -Uri "$BaseUrl/api/v1/events/audit" -Headers $ownerHeaders

    Assert-True ((Get-Count $sessions) -ge 1) "Expected at least one persisted session."
    Assert-True ((Get-Count $participants) -ge 2) "Expected owner plus one additional participant."
    Assert-True ((Get-Count $shares) -ge 2) "Expected at least one share and one invitation-backed share."
    Assert-True ((Get-Count $invitations) -ge 1) "Expected at least one invitation entry."
    Assert-True ((Get-Count $commandJournal) -ge 1) "Expected at least one command journal entry."
    Assert-True ((Get-Count $timeline) -ge 1) "Expected at least one timeline entry."
    Assert-True ((Get-Count $auditAfter) -gt (Get-Count $auditBefore)) "Expected audit stream growth after session actions."
    Assert-True ($null -ne $activeControl) "Expected an active control lease after request."
    Assert-True ($activeControl.principalId -eq "controller-user") "Expected controller-user to hold control after request."
    Assert-True ($requestControlConflict.code -eq "E_CONTROL_LEASE_HELD_BY_OTHER") "Expected lease-held-by-other conflict code."
    Assert-True ($requestControlConflict.requestedParticipantId -eq $ownerParticipantId) "Expected requested participant id in lease conflict payload."
    Assert-True ($requestControlConflict.currentController.principalId -eq "controller-user") "Expected current controller principal in lease conflict payload."
    Assert-True ($notEligibleControlConflict.code -eq "E_CONTROL_NOT_ELIGIBLE") "Expected participant-not-eligible conflict code."
    Assert-True ($notEligibleControlConflict.requestedParticipantId -eq $participantId) "Expected observer participant id in not-eligible conflict payload."
    Assert-True ($releaseMismatchConflict.code -eq "E_CONTROL_PARTICIPANT_MISMATCH") "Expected participant-mismatch conflict code."
    Assert-True ($releaseMismatchConflict.requestedParticipantId -eq $ownerParticipantId) "Expected requested participant id in release mismatch payload."
    Assert-True ($releaseMismatchConflict.currentController.participantId -eq $controllerParticipantId) "Expected active participant id in release mismatch payload."
    Assert-True ($takeoverControl.principalId -eq "smoke-runner") "Expected smoke-runner to hold control after takeover."
    Assert-True ($controlDashboard.activeLease.principalId -eq "smoke-runner") "Expected control dashboard to reflect the owner takeover."
    Assert-True ($session.tenantId -eq "local-tenant") "Expected caller tenant scope to be stamped onto the session."
    Assert-True ($session.organizationId -eq "local-org") "Expected caller organization scope to be stamped onto the session."
    if ($null -ne $viewerDeniedApproval) {
        Assert-True ($viewerDeniedApproval.code -eq "moderation_access_required") "Expected viewer moderation denial code."
    }

    if ($null -ne $foreignDeniedSummary) {
        Assert-True ($foreignDeniedSummary.code -eq "tenant_scope_mismatch") "Expected foreign tenant denial code."
    }
    Assert-True ($inventoryDashboard.totalAgents -ge 1) "Expected inventory dashboard to report at least one agent."
    Assert-True ($auditDashboard.totalEvents -ge (Get-Count $auditAfter)) "Expected audit dashboard totals to cover the persisted audit stream."
    Assert-True ($null -ne $telemetryDashboard) "Expected telemetry dashboard response."
    $sessionProjectionItems = @($sessionProjection.items)
    $projectedSmokeSession = @($sessionProjectionItems | Where-Object { (Get-SessionIdValue -InputObject $_) -eq $sessionId } | Select-Object -First 1)[0]
    Assert-True ($null -ne $projectedSmokeSession) "Expected smoke session to appear in session projections."
    $endpointProjectionItems = @($endpointProjection.items)
    $projectedSmokeEndpoint = @($endpointProjectionItems | Where-Object { $_.endpointId -eq "endpoint_local_demo" } | Select-Object -First 1)[0]
    Assert-True ($null -ne $projectedSmokeEndpoint) "Expected smoke endpoint to appear in endpoint projections."
    Assert-True ($auditProjection.total -ge 1) "Expected at least one projected audit item."
    Assert-True ($telemetryProjection.total -ge 1) "Expected at least one projected telemetry item."
    Assert-True ($replayBundle.sessionId -eq $sessionId) "Expected replay bundle to match the smoke session."
    Assert-True ($archiveSummary.commandCount -ge 1) "Expected archive summary to report command data."
    Assert-True ($archiveAudit.total -ge 1) "Expected archive audit slice to contain entries."
    Assert-True ($archiveTelemetry.total -ge 1) "Expected archive telemetry slice to contain entries."
    Assert-True ($archiveCommands.total -ge 1) "Expected archive command slice to contain entries."

    $lastStep = "close smoke session"
    $closedSession = Invoke-Json -Method "POST" -Uri "$BaseUrl/api/v1/sessions/$sessionId/close" -Headers $ownerHeaders -Body @{
        sessionId = $sessionId
        reason = "smoke teardown"
    }

    $lastStep = "read inventory dashboard after close"
    $inventoryDashboardAfterClose = Invoke-Json -Method "GET" -Uri "$BaseUrl/api/v1/dashboards/inventory" -Headers $ownerHeaders
    $closedEndpoint = @($inventoryDashboardAfterClose.endpoints | Where-Object { $_.endpointId -eq "endpoint_local_demo" } | Select-Object -First 1)[0]
    Assert-True ($null -ne $closedEndpoint) "Expected endpoint_local_demo to exist after close."

    $lastStep = "list sessions after close"
    $sessionsAfterClose = @(Invoke-Json -Method "GET" -Uri "$BaseUrl/api/v1/sessions" -Headers $ownerHeaders)
    $closedSessionStillVisible = @($sessionsAfterClose | Where-Object { (Get-SessionIdValue -InputObject $_) -eq $sessionId }).Count -gt 0
    Assert-True (-not $closedSessionStillVisible) "Expected closed smoke session to disappear from the session list."

    if ($closedEndpoint.PSObject.Properties["activeSessionId"] -and -not [string]::IsNullOrWhiteSpace([string]$closedEndpoint.activeSessionId)) {
        Assert-True ($closedEndpoint.activeSessionId -ne $sessionId) "Expected endpoint_local_demo to stop referencing the closed smoke session."
    }

    $result = [pscustomobject]@{
        Provider = $Provider
        TokenMode = $tokenMode
        MultiActorBearerMode = $multiActorBearerMode
        Runtime = $runtime
        Session = $session
        Participant = $participant
        Share = $share
        RequestedInvitation = $requestedInvitation
        ViewerDeniedApproval = $viewerDeniedApproval
        ApprovedInvitation = $approvedInvitation
        AcceptedInvitation = $acceptedInvitation
        ControlLease = $controlLease
        ActiveControl = $activeControl
        RequestControlConflict = $requestControlConflict
        NotEligibleControlConflict = $notEligibleControlConflict
        ReleaseMismatchConflict = $releaseMismatchConflict
        CommandAck = $commandAck
        ReleasedControl = $releasedControl
        TakeoverControl = $takeoverControl
        ForeignDeniedSummary = $foreignDeniedSummary
        Summary = $summary
        Dashboard = $dashboard
        Lobby = $lobby
        ControlDashboard = $controlDashboard
        InventoryDashboard = $inventoryDashboard
        AuditDashboard = $auditDashboard
        TelemetryDashboard = $telemetryDashboard
        SessionProjection = $sessionProjection
        EndpointProjection = $endpointProjection
        AuditProjection = $auditProjection
        TelemetryProjection = $telemetryProjection
        ReplayBundle = $replayBundle
        ArchiveSummary = $archiveSummary
        ArchiveAudit = $archiveAudit
        ArchiveTelemetry = $archiveTelemetry
        ArchiveCommands = $archiveCommands
        ClosedSession = $closedSession
        SessionsAfterClose = $sessionsAfterClose
        InventoryDashboardAfterClose = $inventoryDashboardAfterClose
        SessionCount = Get-Count $sessions
        ParticipantCount = Get-Count $participants
        ShareCount = Get-Count $shares
        InvitationCount = Get-Count $invitations
        CommandJournalCount = Get-Count $commandJournal
        TimelineCount = Get-Count $timeline
        AuditCount = Get-Count $auditAfter
        DataRoot = $DataRoot
    }

    $result | ConvertTo-Json -Depth 20 | Set-Content -Path $resultLog -Encoding UTF8
    @(
        "Smoke status: PASS"
        "Provider: $Provider"
        "Session: $sessionId"
        "Token mode: $tokenMode"
        "Multi-actor bearer mode: $multiActorBearerMode"
        "Data root: $DataRoot"
        "Result log: $resultLog"
        "API stdout log: $stdoutLog"
        "API stderr log: $stderrLog"
    ) | Set-Content -Path $summaryLog -Encoding UTF8

    Write-Host ""
    Write-Host "=== Smoke Summary ==="
    Write-Host "Status: PASS"
    Write-Host "Provider: $Provider"
    Write-Host "Session: $sessionId"
    Write-Host "Token mode: $tokenMode"
    Write-Host "Data root: $DataRoot"
    Write-Host "Summary: $summaryLog"
    Write-Host "Result:  $resultLog"
    Write-Host "Stdout:  $stdoutLog"
    Write-Host "Stderr:  $stderrLog"
}
catch {
    $stdoutTail = if (Test-Path $stdoutLog) { Get-Content $stdoutLog -Tail 80 | Out-String } else { "" }
    $stderrTail = if (Test-Path $stderrLog) { Get-Content $stderrLog -Tail 80 | Out-String } else { "" }
    $details = @(
        "Smoke step failed: $lastStep",
        $_.Exception.Message
    )

    if (-not [string]::IsNullOrWhiteSpace($stdoutTail)) {
        $details += "ControlPlane stdout tail:`n$stdoutTail"
    }

    if (-not [string]::IsNullOrWhiteSpace($stderrTail)) {
        $details += "ControlPlane stderr tail:`n$stderrTail"
    }

    $failureText = $details -join "`n`n"
    $failureText | Set-Content -Path $failureLog -Encoding UTF8
    @(
        "Smoke status: FAIL"
        "Provider: $Provider"
        "Last step: $lastStep"
        "Token mode: $tokenMode"
        "Data root: $DataRoot"
        "Failure log: $failureLog"
        "API stdout log: $stdoutLog"
        "API stderr log: $stderrLog"
    ) | Set-Content -Path $summaryLog -Encoding UTF8

    Write-Host ""
    Write-Host "=== Smoke Summary ==="
    Write-Host "Status: FAIL" -ForegroundColor Red
    Write-Host "Provider: $Provider"
    Write-Host "Last step: $lastStep"
    Write-Host "Token mode: $tokenMode"
    Write-Host "Data root: $DataRoot"
    Write-Host "Summary: $summaryLog"
    Write-Host "Failure: $failureLog"
    Write-Host "Stdout:  $stdoutLog"
    Write-Host "Stderr:  $stderrLog"

    $smokeExitCode = 1
}
finally {
    if ($null -ne $process -and -not $process.HasExited) {
        Stop-Process -Id $process.Id -Force
    }
}

exit $smokeExitCode

param(
    [Alias("BaseUrl")]
    [string]$ApiBaseUrl = "http://127.0.0.1:18093",
    [string]$KeycloakBaseUrl = "http://127.0.0.1:18096",
    [string]$RealmName = "hidbridge-dev",
    [string]$ControlHealthUrl = "http://127.0.0.1:28092/health",
    [int]$ControlHealthAttempts = 30,
    [int]$ControlHealthDelayMs = 500,
    [switch]$SkipControlHealthCheck,
    [int]$RequestTimeoutSec = 15,
    [string]$TokenClientId = "controlplane-smoke",
    [string]$TokenUsername = "operator.smoke.admin",
    [string]$TokenPassword = "ChangeMe123!",
    [string]$PrincipalId = "smoke-runner",
    [string]$TenantId = "local-tenant",
    [string]$OrganizationId = "local-org",
    [string]$EndpointId = "endpoint_local_demo",
    [string]$Profile = "UltraLowLatency",
    [switch]$AutoCreateSessionIfMissing,
    [switch]$SkipControlLeaseRequest,
    [bool]$AllowMissingControlRequestEndpoint = $true,
    [int]$LeaseSeconds = 120,
    [string]$CommandAction = "keyboard.text",
    [string]$CommandText = "",
    [int]$TimeoutMs = 8000,
    [switch]$SkipTransportHealthCheck,
    [int]$TransportHealthAttempts = 20,
    [int]$TransportHealthDelayMs = 500,
    [string]$OutputJsonPath = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$scriptsRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$smokeScript = Join-Path $scriptsRoot "run_webrtc_edge_agent_smoke.ps1"
if (-not (Test-Path -Path $smokeScript -PathType Leaf)) {
    throw "WebRTC smoke script was not found: $smokeScript"
}

# Legacy Terminal-B parameters are now intentionally ignored.
if ($PSBoundParameters.ContainsKey("AutoCreateSessionIfMissing")) {
    Write-Warning "AutoCreateSessionIfMissing is deprecated and ignored. Session creation/lease orchestration is now server-driven."
}
if ($PSBoundParameters.ContainsKey("AllowMissingControlRequestEndpoint")) {
    Write-Warning "AllowMissingControlRequestEndpoint is deprecated and ignored. Use API control/ensure contract instead."
}
if ($PSBoundParameters.ContainsKey("EndpointId")) {
    Write-Warning "EndpointId is deprecated for Terminal-B wrapper and ignored."
}
if ($PSBoundParameters.ContainsKey("Profile")) {
    Write-Warning "Profile is deprecated for Terminal-B wrapper and ignored."
}

$smokeParameters = @{
    ApiBaseUrl = $ApiBaseUrl
    KeycloakBaseUrl = $KeycloakBaseUrl
    RealmName = $RealmName
    ControlHealthUrl = $ControlHealthUrl
    ControlHealthAttempts = [Math]::Max(1, $ControlHealthAttempts)
    ControlHealthDelayMs = [Math]::Max(100, $ControlHealthDelayMs)
    RequestTimeoutSec = [Math]::Max(1, $RequestTimeoutSec)
    TokenClientId = $TokenClientId
    TokenUsername = $TokenUsername
    TokenPassword = $TokenPassword
    PrincipalId = $PrincipalId
    TenantId = $TenantId
    OrganizationId = $OrganizationId
    SkipControlLeaseRequest = $SkipControlLeaseRequest
    LeaseSeconds = [Math]::Max(30, $LeaseSeconds)
    CommandAction = $CommandAction
    CommandText = $CommandText
    TimeoutMs = [Math]::Max(1000, $TimeoutMs)
    TransportHealthAttempts = [Math]::Max(1, $TransportHealthAttempts)
    TransportHealthDelayMs = [Math]::Max(100, $TransportHealthDelayMs)
}

if ($SkipControlHealthCheck) {
    $smokeParameters.SkipControlHealthCheck = $true
}
if ($SkipTransportHealthCheck) {
    $smokeParameters.SkipTransportHealthCheck = $true
}
if (-not [string]::IsNullOrWhiteSpace($OutputJsonPath)) {
    $smokeParameters.OutputJsonPath = $OutputJsonPath
}

# Terminal-B entrypoint is now a thin compatibility wrapper over the canonical smoke flow.
& $smokeScript @smokeParameters
if (Get-Variable -Name LASTEXITCODE -Scope Global -ErrorAction SilentlyContinue) {
    exit $global:LASTEXITCODE
}

exit 0

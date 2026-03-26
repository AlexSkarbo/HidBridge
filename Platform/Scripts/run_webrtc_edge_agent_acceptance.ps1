param(
    [string]$ApiBaseUrl = "http://127.0.0.1:18093",
    [ValidateSet("uart", "controlws")]
    [string]$CommandExecutor = "uart",
    [switch]$AllowLegacyControlWs,
    [string]$ControlHealthUrl = "http://127.0.0.1:28092/health",
    [string]$ControlWsUrl = "",
    [string]$KeycloakBaseUrl = "http://host.docker.internal:18096",
    [string]$RealmName = "hidbridge-dev",
    [string]$TokenClientId = "controlplane-smoke",
    [string]$TokenClientSecret = "",
    [string]$TokenScope = "openid",
    [string]$TokenUsername = "operator.smoke.admin",
    [string]$TokenPassword = "ChangeMe123!",
    [int]$RequestTimeoutSec = 15,
    [int]$ControlHealthAttempts = 20,
    [int]$KeycloakHealthAttempts = 60,
    [int]$KeycloakHealthDelayMs = 500,
    [int]$TokenRequestAttempts = 5,
    [int]$TokenRequestDelayMs = 500,
    [switch]$SkipTransportHealthCheck,
    [int]$TransportHealthAttempts = 20,
    [int]$TransportHealthDelayMs = 500,
    [switch]$RequireMediaReady,
    [switch]$RequireMediaPlaybackUrl,
    [int]$MediaHealthAttempts = 20,
    [int]$MediaHealthDelayMs = 500,
    [string]$UartPort = "COM6",
    [int]$UartBaud = 3000000,
    [string]$UartHmacKey = "your-master-secret",
    [string]$PrincipalId = "smoke-runner",
    [int]$PeerReadyTimeoutSec = 45,
    [switch]$SkipRuntimeBootstrap,
    [switch]$StopExisting,
    [switch]$StopStackAfter,
    [string]$OutputJsonPath = "Platform/.logs/webrtc-edge-agent-acceptance.result.json"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$scriptsRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$platformRoot = Split-Path -Parent $scriptsRoot
$runnerProject = Join-Path $platformRoot "Tools/HidBridge.Acceptance.Runner/HidBridge.Acceptance.Runner.csproj"

if (-not (Test-Path -Path $runnerProject -PathType Leaf)) {
    throw "Acceptance runner project not found: $runnerProject"
}

$runnerArgs = @(
    "--platform-root", $platformRoot,
    "--api-base-url", $ApiBaseUrl,
    "--command-executor", $CommandExecutor,
    "--control-health-url", $ControlHealthUrl,
    "--keycloak-base-url", $KeycloakBaseUrl,
    "--realm-name", $RealmName,
    "--token-client-id", $TokenClientId,
    "--token-username", $TokenUsername,
    "--token-password", $TokenPassword,
    "--request-timeout-sec", [string]$RequestTimeoutSec,
    "--control-health-attempts", [string]$ControlHealthAttempts,
    "--keycloak-health-attempts", [string]$KeycloakHealthAttempts,
    "--keycloak-health-delay-ms", [string]$KeycloakHealthDelayMs,
    "--token-request-attempts", [string]$TokenRequestAttempts,
    "--token-request-delay-ms", [string]$TokenRequestDelayMs,
    "--transport-health-attempts", [string]$TransportHealthAttempts,
    "--transport-health-delay-ms", [string]$TransportHealthDelayMs,
    "--media-health-attempts", [string]$MediaHealthAttempts,
    "--media-health-delay-ms", [string]$MediaHealthDelayMs,
    "--uart-port", $UartPort,
    "--uart-baud", [string]$UartBaud,
    "--uart-hmac-key", $UartHmacKey,
    "--principal-id", $PrincipalId,
    "--peer-ready-timeout-sec", [string]$PeerReadyTimeoutSec,
    "--output-json-path", $OutputJsonPath
)

if ($AllowLegacyControlWs) { $runnerArgs += "--allow-legacy-controlws" }
if ($SkipTransportHealthCheck) { $runnerArgs += "--skip-transport-health-check" }
if ($RequireMediaReady) { $runnerArgs += "--require-media-ready" }
if ($RequireMediaPlaybackUrl) { $runnerArgs += "--require-media-playback-url" }
if (-not [string]::IsNullOrWhiteSpace($TokenClientSecret)) {
    $runnerArgs += "--token-client-secret"
    $runnerArgs += $TokenClientSecret
}
if (-not [string]::IsNullOrWhiteSpace($TokenScope)) {
    $runnerArgs += "--token-scope"
    $runnerArgs += $TokenScope
}
if ($SkipRuntimeBootstrap) { $runnerArgs += "--skip-runtime-bootstrap" }
if ($StopExisting) { $runnerArgs += "--stop-existing" }
if ($StopStackAfter) { $runnerArgs += "--stop-stack-after" }
if (-not [string]::IsNullOrWhiteSpace($ControlWsUrl)) {
    $runnerArgs += "--control-ws-url"
    $runnerArgs += $ControlWsUrl
}

$arguments = @("run", "--project", $runnerProject, "-c", "Debug", "--") + $runnerArgs
& dotnet @arguments

if ($null -eq $LASTEXITCODE) {
    exit 1
}

exit $LASTEXITCODE

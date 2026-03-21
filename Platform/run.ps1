param(
    [ValidateSet("checks", "tests", "smoke", "smoke-file", "smoke-sql", "smoke-bearer", "doctor", "clean-logs", "ci-local", "full", "export-artifacts", "token-debug", "bearer-rollout", "identity-reset", "identity-onboard", "demo-flow", "demo-seed", "demo-gate", "uart-diagnostics", "webrtc-relay-smoke", "webrtc-peer-adapter", "webrtc-stack", "webrtc-stack-terminal-b", "webrtc-edge-agent-smoke", "webrtc-edge-agent-acceptance", "ops-slo-security-verify", "close-failed-rooms", "close-stale-rooms", "platform-runtime")]
    [string]$Task = "checks",
    [string]$BaseUrl,
    [switch]$RequireDeviceAck,
    [string]$TransportProvider,
    [int]$KeyboardInterfaceSelector = -1,
    [int]$StaleAfterMinutes = -1,
    [switch]$IncludeWebRtcEdgeAgentSmoke,
    [switch]$IncludeWebRtcEdgeAgentAcceptance,
    [switch]$SkipWebRtcEdgeAgentAcceptance,
    [string]$WebRtcCommandExecutor,
    [switch]$AllowLegacyControlWs,
    [string]$WebRtcControlHealthUrl,
    [int]$WebRtcRequestTimeoutSec = -1,
    [int]$WebRtcControlHealthAttempts = -1,
    [switch]$SkipControlHealthCheck,
    [string]$ControlHealthUrl,
    [int]$RequestTimeoutSec = -1,
    [int]$ControlHealthAttempts = -1,
    [switch]$SkipTransportHealthCheck,
    [int]$TransportHealthAttempts = -1,
    [int]$TransportHealthDelayMs = -1,
    [int]$SloWindowMinutes = -1,
    [switch]$AllowAuthDisabled,
    [switch]$FailOnSloWarning,
    [switch]$FailOnSecurityWarning,
    [switch]$RequireAuditTrailCategories,
    [string]$CommandExecutor,
    [string]$ControlWsUrl,
    [string]$UartPort,
    [int]$UartBaud = -1,
    [string]$UartHmacKey,
    [int]$Exp022DurationSec = -1,
    [int]$AdapterDurationSec = -1,
    [int]$PeerReadyTimeoutSec = -1,
    [switch]$StopExisting,
    [switch]$StopStackAfter,
    [switch]$SkipRuntimeBootstrap,
    [string]$EndpointId,
    [string]$PrincipalId,
    [ValidateSet("up", "down", "restart", "status", "logs")]
    [string]$Action,
    [string]$ComposeFile,
    [switch]$Build,
    [switch]$Pull,
    [switch]$RemoveVolumes,
    [switch]$RemoveOrphans,
    [switch]$Follow,
    [switch]$SkipReadyWait,
    [int]$ReadyTimeoutSec = -1,
    [string]$OutputJsonPath,
    [string]$InterfaceSelectorsCsv,
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$ForwardArgs
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Test-LegacyExp022Enabled {
    $value = [Environment]::GetEnvironmentVariable("HIDBRIDGE_ENABLE_LEGACY_EXP022", [EnvironmentVariableTarget]::Process)
    if ([string]::IsNullOrWhiteSpace($value)) {
        $value = [Environment]::GetEnvironmentVariable("HIDBRIDGE_ENABLE_LEGACY_EXP022", [EnvironmentVariableTarget]::User)
    }
    if ([string]::IsNullOrWhiteSpace($value)) {
        $value = [Environment]::GetEnvironmentVariable("HIDBRIDGE_ENABLE_LEGACY_EXP022", [EnvironmentVariableTarget]::Machine)
    }

    if ([string]::IsNullOrWhiteSpace($value)) {
        return $false
    }

    switch ($value.Trim().ToLowerInvariant()) {
        "1" { return $true }
        "true" { return $true }
        "yes" { return $true }
        "on" { return $true }
        default { return $false }
    }
}

$scriptMap = @{
    "checks"       = "run_checks.ps1"
    "tests"        = "run_all_tests.ps1"
    "smoke"        = "run_smoke.ps1"
    "smoke-file"   = "run_file_smoke.ps1"
    "smoke-sql"    = "run_sql_smoke.ps1"
    "smoke-bearer" = "run_api_bearer_smoke.ps1"
    "doctor"       = "run_doctor.ps1"
    "clean-logs"   = "run_clean_logs.ps1"
    "ci-local"     = "run_ci_local.ps1"
    "full"         = "run_full.ps1"
    "export-artifacts" = "run_export_artifacts.ps1"
    "token-debug"  = "run_token_debug.ps1"
    "bearer-rollout" = "run_bearer_rollout_phase.ps1"
    "identity-reset" = "..\\run_identity_reset.ps1"
    "identity-onboard" = "..\\run_identity_onboard.ps1"
    "demo-flow"    = "run_demo_flow.ps1"
    "demo-seed"    = "run_demo_seed.ps1"
    "demo-gate"    = "run_demo_gate.ps1"
    "uart-diagnostics" = "run_uart_diagnostics.ps1"
    "webrtc-relay-smoke" = "run_webrtc_relay_smoke.ps1"
    "webrtc-peer-adapter" = "run_webrtc_peer_adapter.ps1"
    "webrtc-stack" = "run_webrtc_stack.ps1"
    "webrtc-stack-terminal-b" = "run_webrtc_stack_terminal_b.ps1"
    "webrtc-edge-agent-smoke" = "run_webrtc_edge_agent_smoke.ps1"
    "webrtc-edge-agent-acceptance" = "run_webrtc_edge_agent_acceptance.ps1"
    "ops-slo-security-verify" = "run_ops_slo_security_verify.ps1"
    "close-failed-rooms" = "run_close_failed_rooms.ps1"
    "close-stale-rooms" = "run_close_stale_rooms.ps1"
    "platform-runtime" = "run_platform_runtime_profile.ps1"
}

$scriptPath = Join-Path $PSScriptRoot (Join-Path "Scripts" $scriptMap[$Task])
if (-not (Test-Path $scriptPath)) {
    throw "Launcher target not found: $scriptPath"
}

$pwsh = Get-Command powershell.exe -ErrorAction SilentlyContinue
if ($null -eq $pwsh) {
    throw "powershell.exe not found in PATH"
}

$effectiveForwardArgs = [System.Collections.Generic.List[string]]::new()
if ($null -ne $ForwardArgs) {
    foreach ($argument in $ForwardArgs) {
        if (-not [string]::IsNullOrWhiteSpace($argument)) {
            $effectiveForwardArgs.Add($argument) | Out-Null
        }
    }
}

if ($PSBoundParameters.ContainsKey("BaseUrl") -and -not [string]::IsNullOrWhiteSpace($BaseUrl)) {
    $tasksWithBaseUrl = @(
        "demo-seed",
        "demo-gate",
        "uart-diagnostics",
        "webrtc-relay-smoke",
        "webrtc-peer-adapter",
        "webrtc-edge-agent-acceptance",
        "ops-slo-security-verify",
        "close-failed-rooms",
        "close-stale-rooms"
    )

    if ($tasksWithBaseUrl -contains $Task) {
        $parsedBaseUrl = $null
        if (-not [Uri]::TryCreate($BaseUrl, [UriKind]::Absolute, [ref]$parsedBaseUrl)) {
            throw "BaseUrl must be an absolute URL for task '$Task'."
        }

        $baseUrlParameterName = if ($Task -eq "webrtc-edge-agent-acceptance") { "-ApiBaseUrl" } else { "-BaseUrl" }
        $effectiveForwardArgs.Add($baseUrlParameterName) | Out-Null
        $effectiveForwardArgs.Add($BaseUrl) | Out-Null
    }
}

if ($RequireDeviceAck) {
    $effectiveForwardArgs.Add("-RequireDeviceAck") | Out-Null
}

if ($PSBoundParameters.ContainsKey("TransportProvider") -and -not [string]::IsNullOrWhiteSpace($TransportProvider)) {
    $effectiveForwardArgs.Add("-TransportProvider") | Out-Null
    $effectiveForwardArgs.Add($TransportProvider) | Out-Null
}

if ($PSBoundParameters.ContainsKey("KeyboardInterfaceSelector") -and $KeyboardInterfaceSelector -ge 0) {
    if ($KeyboardInterfaceSelector -lt 0 -or $KeyboardInterfaceSelector -gt 255) {
        throw "KeyboardInterfaceSelector must be between 0 and 255."
    }

    $effectiveForwardArgs.Add("-KeyboardInterfaceSelector") | Out-Null
    $effectiveForwardArgs.Add([string]$KeyboardInterfaceSelector) | Out-Null
}

if ($PSBoundParameters.ContainsKey("StaleAfterMinutes")) {
    if ($StaleAfterMinutes -lt 1) {
        throw "StaleAfterMinutes must be greater than 0."
    }

    $effectiveForwardArgs.Add("-StaleAfterMinutes") | Out-Null
    $effectiveForwardArgs.Add([string]$StaleAfterMinutes) | Out-Null
}

if ($IncludeWebRtcEdgeAgentSmoke) {
    $effectiveForwardArgs.Add("-IncludeWebRtcEdgeAgentSmoke") | Out-Null
}

if ($IncludeWebRtcEdgeAgentAcceptance) {
    $tasksWithWebRtcAcceptance = @("ci-local", "full")
    if ($tasksWithWebRtcAcceptance -contains $Task) {
        $effectiveForwardArgs.Add("-IncludeWebRtcEdgeAgentAcceptance") | Out-Null
    }
}

if ($SkipWebRtcEdgeAgentAcceptance) {
    $tasksWithWebRtcAcceptanceSkip = @("ci-local", "full")
    if ($tasksWithWebRtcAcceptanceSkip -contains $Task) {
        $effectiveForwardArgs.Add("-SkipWebRtcEdgeAgentAcceptance") | Out-Null
    }
}

if ($PSBoundParameters.ContainsKey("WebRtcCommandExecutor") -and -not [string]::IsNullOrWhiteSpace($WebRtcCommandExecutor)) {
    $tasksWithWebRtcCommandExecutor = @("demo-flow", "ci-local", "full")
    if ($tasksWithWebRtcCommandExecutor -contains $Task) {
        $effectiveForwardArgs.Add("-WebRtcCommandExecutor") | Out-Null
        $effectiveForwardArgs.Add($WebRtcCommandExecutor) | Out-Null
    }
}

if ($PSBoundParameters.ContainsKey("WebRtcControlHealthUrl") -and -not [string]::IsNullOrWhiteSpace($WebRtcControlHealthUrl)) {
    $tasksWithWebRtcControlHealthUrl = @("demo-flow", "ci-local", "full")
    if ($tasksWithWebRtcControlHealthUrl -contains $Task) {
        $effectiveForwardArgs.Add("-WebRtcControlHealthUrl") | Out-Null
        $effectiveForwardArgs.Add($WebRtcControlHealthUrl) | Out-Null
    }
}

if ($PSBoundParameters.ContainsKey("WebRtcRequestTimeoutSec") -and $WebRtcRequestTimeoutSec -gt 0) {
    $effectiveForwardArgs.Add("-WebRtcRequestTimeoutSec") | Out-Null
    $effectiveForwardArgs.Add([string]$WebRtcRequestTimeoutSec) | Out-Null
}

if ($PSBoundParameters.ContainsKey("WebRtcControlHealthAttempts") -and $WebRtcControlHealthAttempts -gt 0) {
    $effectiveForwardArgs.Add("-WebRtcControlHealthAttempts") | Out-Null
    $effectiveForwardArgs.Add([string]$WebRtcControlHealthAttempts) | Out-Null
}

if ($PSBoundParameters.ContainsKey("ControlHealthUrl") -and -not [string]::IsNullOrWhiteSpace($ControlHealthUrl)) {
    $effectiveForwardArgs.Add("-ControlHealthUrl") | Out-Null
    $effectiveForwardArgs.Add($ControlHealthUrl) | Out-Null
}

if ($SkipControlHealthCheck) {
    $tasksWithControlHealthCheckSwitch = @("webrtc-edge-agent-smoke", "webrtc-stack-terminal-b")
    if ($tasksWithControlHealthCheckSwitch -contains $Task) {
        $effectiveForwardArgs.Add("-SkipControlHealthCheck") | Out-Null
    }
}

if ($PSBoundParameters.ContainsKey("RequestTimeoutSec") -and $RequestTimeoutSec -gt 0) {
    $effectiveForwardArgs.Add("-RequestTimeoutSec") | Out-Null
    $effectiveForwardArgs.Add([string]$RequestTimeoutSec) | Out-Null
}

if ($PSBoundParameters.ContainsKey("ControlHealthAttempts") -and $ControlHealthAttempts -gt 0) {
    $effectiveForwardArgs.Add("-ControlHealthAttempts") | Out-Null
    $effectiveForwardArgs.Add([string]$ControlHealthAttempts) | Out-Null
}

if ($SkipTransportHealthCheck) {
    $tasksWithTransportHealthCheckSwitch = @("webrtc-edge-agent-smoke", "webrtc-edge-agent-acceptance", "webrtc-stack-terminal-b", "demo-gate", "demo-flow")
    if ($tasksWithTransportHealthCheckSwitch -contains $Task) {
        $effectiveForwardArgs.Add("-SkipTransportHealthCheck") | Out-Null
    }
}

if ($PSBoundParameters.ContainsKey("TransportHealthAttempts") -and $TransportHealthAttempts -gt 0) {
    $tasksWithTransportHealthAttempts = @("webrtc-edge-agent-smoke", "webrtc-edge-agent-acceptance", "webrtc-stack-terminal-b", "demo-gate", "demo-flow")
    if ($tasksWithTransportHealthAttempts -contains $Task) {
        $effectiveForwardArgs.Add("-TransportHealthAttempts") | Out-Null
        $effectiveForwardArgs.Add([string]$TransportHealthAttempts) | Out-Null
    }
}

if ($PSBoundParameters.ContainsKey("TransportHealthDelayMs") -and $TransportHealthDelayMs -gt 0) {
    $tasksWithTransportHealthDelay = @("webrtc-edge-agent-smoke", "webrtc-edge-agent-acceptance", "webrtc-stack-terminal-b", "demo-gate", "demo-flow")
    if ($tasksWithTransportHealthDelay -contains $Task) {
        $effectiveForwardArgs.Add("-TransportHealthDelayMs") | Out-Null
        $effectiveForwardArgs.Add([string]$TransportHealthDelayMs) | Out-Null
    }
}

if ($PSBoundParameters.ContainsKey("SloWindowMinutes") -and $SloWindowMinutes -gt 0) {
    if ($Task -eq "ops-slo-security-verify") {
        $effectiveForwardArgs.Add("-SloWindowMinutes") | Out-Null
        $effectiveForwardArgs.Add([string]$SloWindowMinutes) | Out-Null
    }
}

if ($AllowAuthDisabled) {
    if ($Task -eq "ops-slo-security-verify") {
        $effectiveForwardArgs.Add("-AllowAuthDisabled") | Out-Null
    }
}

if ($FailOnSloWarning) {
    if ($Task -eq "ops-slo-security-verify") {
        $effectiveForwardArgs.Add("-FailOnSloWarning") | Out-Null
    }
}

if ($FailOnSecurityWarning) {
    if ($Task -eq "ops-slo-security-verify") {
        $effectiveForwardArgs.Add("-FailOnSecurityWarning") | Out-Null
    }
}

if ($RequireAuditTrailCategories) {
    if ($Task -eq "ops-slo-security-verify") {
        $effectiveForwardArgs.Add("-RequireAuditTrailCategories") | Out-Null
    }
}

if ($PSBoundParameters.ContainsKey("ControlWsUrl") -and -not [string]::IsNullOrWhiteSpace($ControlWsUrl)) {
    $tasksWithControlWsUrl = @("webrtc-stack", "webrtc-peer-adapter", "webrtc-edge-agent-acceptance")
    if ($tasksWithControlWsUrl -contains $Task) {
        $effectiveForwardArgs.Add("-ControlWsUrl") | Out-Null
        $effectiveForwardArgs.Add($ControlWsUrl) | Out-Null
    }
}

if ($PSBoundParameters.ContainsKey("CommandExecutor") -and -not [string]::IsNullOrWhiteSpace($CommandExecutor)) {
    if ($Task -eq "webrtc-stack" -or $Task -eq "webrtc-edge-agent-acceptance") {
        $effectiveForwardArgs.Add("-CommandExecutor") | Out-Null
        $effectiveForwardArgs.Add($CommandExecutor) | Out-Null
    }
}

if ($AllowLegacyControlWs) {
    $tasksWithLegacyControlWs = @("webrtc-stack", "webrtc-edge-agent-acceptance", "demo-flow", "ci-local", "full", "webrtc-peer-adapter")
    if ($tasksWithLegacyControlWs -contains $Task) {
        if (-not (Test-LegacyExp022Enabled)) {
            throw "Legacy exp-022 compatibility mode is disabled. Set HIDBRIDGE_ENABLE_LEGACY_EXP022=true in your shell to run controlws/peer-adapter flows."
        }

        $effectiveForwardArgs.Add("-AllowLegacyControlWs") | Out-Null
    }
}

if ($Task -eq "webrtc-peer-adapter" -and -not $AllowLegacyControlWs) {
    throw "Task 'webrtc-peer-adapter' is legacy exp-022 compatibility tooling. Pass -AllowLegacyControlWs explicitly to run it."
}

if ($PSBoundParameters.ContainsKey("UartPort") -and -not [string]::IsNullOrWhiteSpace($UartPort)) {
    if ($Task -eq "webrtc-stack" -or $Task -eq "webrtc-edge-agent-acceptance") {
        $effectiveForwardArgs.Add("-UartPort") | Out-Null
        $effectiveForwardArgs.Add($UartPort) | Out-Null
    }
}

if ($PSBoundParameters.ContainsKey("UartBaud") -and $UartBaud -gt 0) {
    if ($Task -eq "webrtc-stack" -or $Task -eq "webrtc-edge-agent-acceptance") {
        $effectiveForwardArgs.Add("-UartBaud") | Out-Null
        $effectiveForwardArgs.Add([string]$UartBaud) | Out-Null
    }
}

if ($PSBoundParameters.ContainsKey("UartHmacKey") -and -not [string]::IsNullOrWhiteSpace($UartHmacKey)) {
    if ($Task -eq "webrtc-stack" -or $Task -eq "webrtc-edge-agent-acceptance") {
        $effectiveForwardArgs.Add("-UartHmacKey") | Out-Null
        $effectiveForwardArgs.Add($UartHmacKey) | Out-Null
    }
}

if ($PSBoundParameters.ContainsKey("Exp022DurationSec") -and $Exp022DurationSec -gt 0) {
    if ($Task -eq "webrtc-stack") {
        $effectiveForwardArgs.Add("-Exp022DurationSec") | Out-Null
        $effectiveForwardArgs.Add([string]$Exp022DurationSec) | Out-Null
    }
}

if ($PSBoundParameters.ContainsKey("AdapterDurationSec") -and $AdapterDurationSec -gt 0) {
    if ($Task -eq "webrtc-stack") {
        $effectiveForwardArgs.Add("-AdapterDurationSec") | Out-Null
        $effectiveForwardArgs.Add([string]$AdapterDurationSec) | Out-Null
    }
}

if ($PSBoundParameters.ContainsKey("PeerReadyTimeoutSec") -and $PeerReadyTimeoutSec -gt 0) {
    if ($Task -eq "webrtc-stack" -or $Task -eq "webrtc-edge-agent-acceptance") {
        $effectiveForwardArgs.Add("-PeerReadyTimeoutSec") | Out-Null
        $effectiveForwardArgs.Add([string]$PeerReadyTimeoutSec) | Out-Null
    }
    elseif ($Task -eq "ci-local" -or $Task -eq "full") {
        $effectiveForwardArgs.Add("-WebRtcPeerReadyTimeoutSec") | Out-Null
        $effectiveForwardArgs.Add([string]$PeerReadyTimeoutSec) | Out-Null
    }
}

if ($StopExisting) {
    if ($Task -eq "webrtc-stack" -or $Task -eq "webrtc-edge-agent-acceptance") {
        $effectiveForwardArgs.Add("-StopExisting") | Out-Null
    }
}

if ($StopStackAfter) {
    if ($Task -eq "webrtc-edge-agent-acceptance") {
        $effectiveForwardArgs.Add("-StopStackAfter") | Out-Null
    }
}

if ($SkipRuntimeBootstrap) {
    if ($Task -eq "webrtc-stack" -or $Task -eq "webrtc-edge-agent-acceptance") {
        $effectiveForwardArgs.Add("-SkipRuntimeBootstrap") | Out-Null
    }
}

if ($PSBoundParameters.ContainsKey("EndpointId") -and -not [string]::IsNullOrWhiteSpace($EndpointId)) {
    if ($Task -eq "webrtc-stack") {
        $effectiveForwardArgs.Add("-EndpointId") | Out-Null
        $effectiveForwardArgs.Add($EndpointId) | Out-Null
    }
}

if ($PSBoundParameters.ContainsKey("PrincipalId") -and -not [string]::IsNullOrWhiteSpace($PrincipalId)) {
    $tasksWithPrincipalId = @("webrtc-stack", "webrtc-edge-agent-acceptance", "webrtc-peer-adapter", "demo-gate")
    if ($tasksWithPrincipalId -contains $Task) {
        $effectiveForwardArgs.Add("-PrincipalId") | Out-Null
        $effectiveForwardArgs.Add($PrincipalId) | Out-Null
    }
}

if ($PSBoundParameters.ContainsKey("OutputJsonPath") -and -not [string]::IsNullOrWhiteSpace($OutputJsonPath)) {
    $effectiveForwardArgs.Add("-OutputJsonPath") | Out-Null
    $effectiveForwardArgs.Add($OutputJsonPath) | Out-Null
}

if ($Task -eq "platform-runtime") {
    if ($PSBoundParameters.ContainsKey("Action") -and -not [string]::IsNullOrWhiteSpace($Action)) {
        $effectiveForwardArgs.Add("-Action") | Out-Null
        $effectiveForwardArgs.Add($Action) | Out-Null
    }

    if ($PSBoundParameters.ContainsKey("ComposeFile") -and -not [string]::IsNullOrWhiteSpace($ComposeFile)) {
        $effectiveForwardArgs.Add("-ComposeFile") | Out-Null
        $effectiveForwardArgs.Add($ComposeFile) | Out-Null
    }

    if ($Build) {
        $effectiveForwardArgs.Add("-Build") | Out-Null
    }

    if ($Pull) {
        $effectiveForwardArgs.Add("-Pull") | Out-Null
    }

    if ($RemoveVolumes) {
        $effectiveForwardArgs.Add("-RemoveVolumes") | Out-Null
    }

    if ($RemoveOrphans) {
        $effectiveForwardArgs.Add("-RemoveOrphans") | Out-Null
    }

    if ($Follow) {
        $effectiveForwardArgs.Add("-Follow") | Out-Null
    }

    if ($SkipReadyWait) {
        $effectiveForwardArgs.Add("-SkipReadyWait") | Out-Null
    }

    if ($PSBoundParameters.ContainsKey("ReadyTimeoutSec") -and $ReadyTimeoutSec -gt 0) {
        $effectiveForwardArgs.Add("-ReadyTimeoutSec") | Out-Null
        $effectiveForwardArgs.Add([string]$ReadyTimeoutSec) | Out-Null
    }
}

if ($PSBoundParameters.ContainsKey("InterfaceSelectorsCsv") -and -not [string]::IsNullOrWhiteSpace($InterfaceSelectorsCsv)) {
    $effectiveForwardArgs.Add("-InterfaceSelectorsCsv") | Out-Null
    $effectiveForwardArgs.Add($InterfaceSelectorsCsv) | Out-Null
}

& $pwsh.Source -NoProfile -ExecutionPolicy Bypass -File $scriptPath @($effectiveForwardArgs.ToArray())
exit $LASTEXITCODE

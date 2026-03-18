param(
    [ValidateSet("checks", "tests", "smoke", "smoke-file", "smoke-sql", "smoke-bearer", "doctor", "clean-logs", "ci-local", "full", "export-artifacts", "token-debug", "bearer-rollout", "identity-reset", "identity-onboard", "demo-flow", "demo-seed", "demo-gate", "uart-diagnostics", "webrtc-relay-smoke", "webrtc-peer-adapter", "webrtc-stack", "webrtc-stack-terminal-b", "webrtc-edge-agent-smoke", "close-failed-rooms", "close-stale-rooms")]
    [string]$Task = "checks",
    [string]$BaseUrl,
    [switch]$RequireDeviceAck,
    [string]$TransportProvider,
    [int]$KeyboardInterfaceSelector = -1,
    [int]$StaleAfterMinutes = -1,
    [switch]$IncludeWebRtcEdgeAgentSmoke,
    [string]$WebRtcControlHealthUrl,
    [int]$WebRtcRequestTimeoutSec = -1,
    [int]$WebRtcControlHealthAttempts = -1,
    [string]$ControlHealthUrl,
    [int]$RequestTimeoutSec = -1,
    [int]$ControlHealthAttempts = -1,
    [string]$ControlWsUrl,
    [string]$UartPort,
    [int]$UartBaud = -1,
    [string]$UartHmacKey,
    [int]$Exp022DurationSec = -1,
    [int]$AdapterDurationSec = -1,
    [int]$PeerReadyTimeoutSec = -1,
    [string]$EndpointId,
    [string]$PrincipalId,
    [string]$OutputJsonPath,
    [string]$InterfaceSelectorsCsv,
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$ForwardArgs
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

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
    "close-failed-rooms" = "run_close_failed_rooms.ps1"
    "close-stale-rooms" = "run_close_stale_rooms.ps1"
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
        "close-failed-rooms",
        "close-stale-rooms"
    )

    if ($tasksWithBaseUrl -contains $Task) {
        $parsedBaseUrl = $null
        if (-not [Uri]::TryCreate($BaseUrl, [UriKind]::Absolute, [ref]$parsedBaseUrl)) {
            throw "BaseUrl must be an absolute URL for task '$Task'."
        }

        $effectiveForwardArgs.Add("-BaseUrl") | Out-Null
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

if ($PSBoundParameters.ContainsKey("WebRtcControlHealthUrl") -and -not [string]::IsNullOrWhiteSpace($WebRtcControlHealthUrl)) {
    $effectiveForwardArgs.Add("-WebRtcControlHealthUrl") | Out-Null
    $effectiveForwardArgs.Add($WebRtcControlHealthUrl) | Out-Null
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

if ($PSBoundParameters.ContainsKey("RequestTimeoutSec") -and $RequestTimeoutSec -gt 0) {
    $effectiveForwardArgs.Add("-RequestTimeoutSec") | Out-Null
    $effectiveForwardArgs.Add([string]$RequestTimeoutSec) | Out-Null
}

if ($PSBoundParameters.ContainsKey("ControlHealthAttempts") -and $ControlHealthAttempts -gt 0) {
    $effectiveForwardArgs.Add("-ControlHealthAttempts") | Out-Null
    $effectiveForwardArgs.Add([string]$ControlHealthAttempts) | Out-Null
}

if ($PSBoundParameters.ContainsKey("ControlWsUrl") -and -not [string]::IsNullOrWhiteSpace($ControlWsUrl)) {
    $tasksWithControlWsUrl = @("webrtc-stack", "webrtc-peer-adapter")
    if ($tasksWithControlWsUrl -contains $Task) {
        $effectiveForwardArgs.Add("-ControlWsUrl") | Out-Null
        $effectiveForwardArgs.Add($ControlWsUrl) | Out-Null
    }
}

if ($PSBoundParameters.ContainsKey("UartPort") -and -not [string]::IsNullOrWhiteSpace($UartPort)) {
    if ($Task -eq "webrtc-stack") {
        $effectiveForwardArgs.Add("-UartPort") | Out-Null
        $effectiveForwardArgs.Add($UartPort) | Out-Null
    }
}

if ($PSBoundParameters.ContainsKey("UartBaud") -and $UartBaud -gt 0) {
    if ($Task -eq "webrtc-stack") {
        $effectiveForwardArgs.Add("-UartBaud") | Out-Null
        $effectiveForwardArgs.Add([string]$UartBaud) | Out-Null
    }
}

if ($PSBoundParameters.ContainsKey("UartHmacKey") -and -not [string]::IsNullOrWhiteSpace($UartHmacKey)) {
    if ($Task -eq "webrtc-stack") {
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
    if ($Task -eq "webrtc-stack") {
        $effectiveForwardArgs.Add("-PeerReadyTimeoutSec") | Out-Null
        $effectiveForwardArgs.Add([string]$PeerReadyTimeoutSec) | Out-Null
    }
}

if ($PSBoundParameters.ContainsKey("EndpointId") -and -not [string]::IsNullOrWhiteSpace($EndpointId)) {
    if ($Task -eq "webrtc-stack") {
        $effectiveForwardArgs.Add("-EndpointId") | Out-Null
        $effectiveForwardArgs.Add($EndpointId) | Out-Null
    }
}

if ($PSBoundParameters.ContainsKey("PrincipalId") -and -not [string]::IsNullOrWhiteSpace($PrincipalId)) {
    $tasksWithPrincipalId = @("webrtc-stack", "webrtc-peer-adapter", "demo-gate")
    if ($tasksWithPrincipalId -contains $Task) {
        $effectiveForwardArgs.Add("-PrincipalId") | Out-Null
        $effectiveForwardArgs.Add($PrincipalId) | Out-Null
    }
}

if ($PSBoundParameters.ContainsKey("OutputJsonPath") -and -not [string]::IsNullOrWhiteSpace($OutputJsonPath)) {
    $effectiveForwardArgs.Add("-OutputJsonPath") | Out-Null
    $effectiveForwardArgs.Add($OutputJsonPath) | Out-Null
}

if ($PSBoundParameters.ContainsKey("InterfaceSelectorsCsv") -and -not [string]::IsNullOrWhiteSpace($InterfaceSelectorsCsv)) {
    $effectiveForwardArgs.Add("-InterfaceSelectorsCsv") | Out-Null
    $effectiveForwardArgs.Add($InterfaceSelectorsCsv) | Out-Null
}

& $pwsh.Source -NoProfile -ExecutionPolicy Bypass -File $scriptPath @($effectiveForwardArgs.ToArray())
exit $LASTEXITCODE

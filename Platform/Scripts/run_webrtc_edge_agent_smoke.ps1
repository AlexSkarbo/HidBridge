param(
    [Alias("BaseUrl")]
    [string]$ApiBaseUrl = "http://127.0.0.1:18093",
    [string]$ControlHealthUrl = "http://127.0.0.1:28092/health",
    [int]$RequestTimeoutSec = 15,
    [int]$ControlHealthAttempts = 20,
    [string]$OutputJsonPath = "Platform/.logs/webrtc-edge-agent-smoke.result.json"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$scriptsRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$platformRoot = Split-Path -Parent $scriptsRoot
$terminalBScript = Join-Path $scriptsRoot "run_webrtc_stack_terminal_b.ps1"

if (-not (Test-Path $terminalBScript)) {
    throw "Launcher target not found: $terminalBScript"
}

& $terminalBScript `
    -ApiBaseUrl $ApiBaseUrl `
    -ControlHealthUrl $ControlHealthUrl `
    -RequestTimeoutSec $RequestTimeoutSec `
    -ControlHealthAttempts $ControlHealthAttempts `
    -SkipTransportHealthCheck `
    -OutputJsonPath $OutputJsonPath

$resultPath = if ([System.IO.Path]::IsPathRooted($OutputJsonPath)) { $OutputJsonPath } else { Join-Path $platformRoot $OutputJsonPath }
if (-not (Test-Path $resultPath)) {
    throw "Smoke result json not found: $resultPath"
}

$result = Get-Content -Raw -Path $resultPath | ConvertFrom-Json
$status = [string]$result.commandStatus
$commandId = [string]$result.commandId

Write-Host ""
Write-Host "=== WebRTC Edge Agent Smoke ==="
Write-Host "Result:   $status"
Write-Host "Command:  $commandId"
Write-Host "Summary:  $OutputJsonPath"

if (-not [string]::Equals($status, "Applied", [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "WebRTC edge-agent smoke failed: command status is '$status' (expected 'Applied')."
}

Write-Host "PASS"

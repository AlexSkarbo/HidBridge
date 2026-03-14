param(
    [ValidateSet("checks", "tests", "smoke", "smoke-file", "smoke-sql", "smoke-bearer", "doctor", "clean-logs", "ci-local", "full", "export-artifacts", "token-debug", "bearer-rollout", "identity-reset", "identity-onboard", "demo-flow", "demo-seed", "demo-gate", "uart-diagnostics", "close-failed-rooms")]
    [string]$Task = "checks",
    [string]$BaseUrl,
    [switch]$RequireDeviceAck,
    [int]$KeyboardInterfaceSelector = -1,
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
    "close-failed-rooms" = "run_close_failed_rooms.ps1"
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

if ($PSBoundParameters.ContainsKey("BaseUrl")) {
    $effectiveForwardArgs.Add("-BaseUrl") | Out-Null
    $effectiveForwardArgs.Add($BaseUrl) | Out-Null
}

if ($RequireDeviceAck) {
    $effectiveForwardArgs.Add("-RequireDeviceAck") | Out-Null
}

if ($PSBoundParameters.ContainsKey("KeyboardInterfaceSelector")) {
    if ($KeyboardInterfaceSelector -lt 0 -or $KeyboardInterfaceSelector -gt 255) {
        throw "KeyboardInterfaceSelector must be between 0 and 255."
    }

    $effectiveForwardArgs.Add("-KeyboardInterfaceSelector") | Out-Null
    $effectiveForwardArgs.Add([string]$KeyboardInterfaceSelector) | Out-Null
}

if ($PSBoundParameters.ContainsKey("OutputJsonPath")) {
    $effectiveForwardArgs.Add("-OutputJsonPath") | Out-Null
    $effectiveForwardArgs.Add($OutputJsonPath) | Out-Null
}

if ($PSBoundParameters.ContainsKey("InterfaceSelectorsCsv")) {
    $effectiveForwardArgs.Add("-InterfaceSelectorsCsv") | Out-Null
    $effectiveForwardArgs.Add($InterfaceSelectorsCsv) | Out-Null
}

& $pwsh.Source -NoProfile -ExecutionPolicy Bypass -File $scriptPath @($effectiveForwardArgs.ToArray())
exit $LASTEXITCODE

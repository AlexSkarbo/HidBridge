param(
    [ValidateSet("checks", "tests", "smoke", "smoke-file", "smoke-sql", "smoke-bearer", "doctor", "clean-logs", "ci-local", "full", "export-artifacts", "token-debug", "bearer-rollout", "identity-reset")]
    [string]$Task = "checks",
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
}

$scriptPath = Join-Path $PSScriptRoot (Join-Path "Scripts" $scriptMap[$Task])
if (-not (Test-Path $scriptPath)) {
    throw "Launcher target not found: $scriptPath"
}

$pwsh = Get-Command powershell.exe -ErrorAction SilentlyContinue
if ($null -eq $pwsh) {
    throw "powershell.exe not found in PATH"
}

& $pwsh.Source -NoProfile -ExecutionPolicy Bypass -File $scriptPath @ForwardArgs
exit $LASTEXITCODE

param(
    [switch]$PrintOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$runner = Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) "Scripts/run_bearer_rollout_phase.ps1"
if ($PrintOnly) {
    & $runner -Phase 4 -PrintOnly
    return
}

& $runner -Phase 4 -ApplyOnly
Write-Host ""
Write-Host "Recommended verification:"
Write-Host "  powershell -ExecutionPolicy Bypass -File Platform/run.ps1 -Task doctor"
Write-Host "  powershell -ExecutionPolicy Bypass -File Platform/run.ps1 -Task smoke-bearer"
Write-Host "  powershell -ExecutionPolicy Bypass -File Platform/run.ps1 -Task full"

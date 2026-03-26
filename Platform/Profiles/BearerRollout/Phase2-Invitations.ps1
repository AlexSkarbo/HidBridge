Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$scriptPath = Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) "Scripts/run_bearer_rollout_phase.ps1"
& $scriptPath -Phase 2 @args
exit $LASTEXITCODE

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$scriptPath = Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) "run_bearer_rollout_phase.ps1"
& $scriptPath -Phase 3 @args
exit $LASTEXITCODE

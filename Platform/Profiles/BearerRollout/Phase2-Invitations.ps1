Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$runner = Join-Path (Split-Path (Split-Path $PSScriptRoot -Parent) -Parent) "run.ps1"
& $runner -Task bearer-rollout -Phase 2 @args
exit $LASTEXITCODE

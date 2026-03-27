Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$platformRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$runtimeCtlProject = Join-Path $platformRoot "Tools/HidBridge.RuntimeCtl/HidBridge.RuntimeCtl.csproj"
dotnet run --project $runtimeCtlProject -- --platform-root $platformRoot bearer-rollout -Phase 3 @args
exit $LASTEXITCODE

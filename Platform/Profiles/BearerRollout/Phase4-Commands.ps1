param(
    [switch]$PrintOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$platformRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$runtimeCtlProject = Join-Path $platformRoot "Tools/HidBridge.RuntimeCtl/HidBridge.RuntimeCtl.csproj"
if ($PrintOnly) {
    dotnet run --project $runtimeCtlProject -- --platform-root $platformRoot bearer-rollout -Phase 4 -PrintOnly
    return
}

dotnet run --project $runtimeCtlProject -- --platform-root $platformRoot bearer-rollout -Phase 4 -ApplyOnly
Write-Host ""
Write-Host "Recommended verification:"
Write-Host "  dotnet run --project Platform/Tools/HidBridge.RuntimeCtl/HidBridge.RuntimeCtl.csproj -- --platform-root Platform doctor"
Write-Host "  dotnet run --project Platform/Tools/HidBridge.RuntimeCtl/HidBridge.RuntimeCtl.csproj -- --platform-root Platform smoke-bearer"
Write-Host "  dotnet run --project Platform/Tools/HidBridge.RuntimeCtl/HidBridge.RuntimeCtl.csproj -- --platform-root Platform full"

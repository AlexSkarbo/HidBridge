Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$platformRoot = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$runtimeCtlProject = Join-Path $platformRoot "Tools/HidBridge.RuntimeCtl/HidBridge.RuntimeCtl.csproj"
if (-not (Test-Path $runtimeCtlProject)) {
    throw "RuntimeCtl project not found: $runtimeCtlProject"
}

$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if ($null -eq $dotnet) {
    throw "dotnet CLI not found in PATH"
}

$arguments = [System.Collections.Generic.List[string]]::new()
$arguments.Add("run") | Out-Null
$arguments.Add("--project") | Out-Null
$arguments.Add($runtimeCtlProject) | Out-Null
$arguments.Add("--") | Out-Null
$arguments.Add("--platform-root") | Out-Null
$arguments.Add($platformRoot) | Out-Null
$arguments.Add("webrtc-edge-agent-smoke") | Out-Null

if ($null -ne $args) {
    foreach ($argument in $args) {
        if (-not [string]::IsNullOrWhiteSpace($argument)) {
            $arguments.Add($argument) | Out-Null
        }
    }
}

& $dotnet.Source @arguments
$exitCode = $LASTEXITCODE
if ($null -eq $exitCode) {
    $exitCode = 1
}

exit $exitCode

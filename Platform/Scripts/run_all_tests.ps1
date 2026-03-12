param(
    [string]$Configuration = "Debug",
    [ValidateSet("quiet", "minimal", "normal", "detailed", "diagnostic")]
    [string]$DotnetVerbosity = "minimal",
    [switch]$StopOnFailure
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$scriptsRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$platformRoot = Split-Path -Parent $scriptsRoot
$repoRoot = Split-Path -Parent $platformRoot
. (Join-Path $scriptsRoot "Common/ScriptCommon.ps1")
$logRoot = New-OperationalLogRoot -PlatformRoot $platformRoot -Category "tests"
$results = New-OperationalResultList

try {
    if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
        Add-OperationalResult -Results $results -Name "dotnet availability" -Status "FAIL" -Seconds 0 -ExitCode 127 -LogPath ""
        if ($StopOnFailure) {
            throw "dotnet not found in PATH"
        }
    }
    else {
        Invoke-LoggedCommand -Results $results -Name "Platform restore" -LogRoot $logRoot -FilePath "dotnet" -Arguments @("restore", "Platform/HidBridge.Platform.sln") -WorkingDirectory $repoRoot -StopOnFailure:$StopOnFailure
        Invoke-LoggedCommand -Results $results -Name "Platform build" -LogRoot $logRoot -FilePath "dotnet" -Arguments @("build", "Platform/HidBridge.Platform.sln", "-c", $Configuration, "-v", $DotnetVerbosity, "-m:1", "-nodeReuse:false") -WorkingDirectory $repoRoot -StopOnFailure:$StopOnFailure
        Invoke-LoggedCommand -Results $results -Name "HidBridge.Platform.Tests" -LogRoot $logRoot -FilePath "dotnet" -Arguments @("test", "Platform/Tests/HidBridge.Platform.Tests/HidBridge.Platform.Tests.csproj", "-c", $Configuration, "-v", $DotnetVerbosity, "-m:1", "-nodeReuse:false") -WorkingDirectory $repoRoot -StopOnFailure:$StopOnFailure
    }
}
catch {
    Write-Host ""
    Write-Host "Platform test runner aborted: $($_.Exception.Message)" -ForegroundColor Red
}

Write-OperationalSummary -Results $results -LogRoot $logRoot

if ($results | Where-Object { $_.Status -eq "FAIL" }) {
    exit 1
}

exit 0

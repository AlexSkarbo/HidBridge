param(
    [string]$Configuration = "Debug",
    [ValidateSet("quiet", "minimal", "normal", "detailed", "diagnostic")]
    [string]$DotnetVerbosity = "minimal",
    [switch]$SkipDotnet,
    [switch]$SkipGo,
    [switch]$StopOnFailure
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$results = New-Object System.Collections.Generic.List[object]

function Add-Result {
    param(
        [string]$Name,
        [string]$Status,
        [double]$Seconds,
        [int]$ExitCode
    )

    $results.Add([pscustomobject]@{
        Name     = $Name
        Status   = $Status
        Seconds  = [Math]::Round($Seconds, 2)
        ExitCode = $ExitCode
    }) | Out-Null
}

function Invoke-External {
    param(
        [string]$Name,
        [string]$FilePath,
        [string[]]$Arguments,
        [string]$WorkingDirectory
    )

    Write-Host ""
    Write-Host "=== $Name ==="
    Write-Host "$FilePath $($Arguments -join ' ')"

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    Push-Location $WorkingDirectory
    try {
        & $FilePath @Arguments
        $exitCode = $LASTEXITCODE
    }
    finally {
        Pop-Location
        $sw.Stop()
    }

    if ($exitCode -eq 0) {
        Add-Result -Name $Name -Status "PASS" -Seconds $sw.Elapsed.TotalSeconds -ExitCode $exitCode
        return
    }

    Add-Result -Name $Name -Status "FAIL" -Seconds $sw.Elapsed.TotalSeconds -ExitCode $exitCode
    if ($StopOnFailure) {
        throw "$Name failed with exit code $exitCode"
    }
}

try {
    if (-not $SkipDotnet) {
        if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
            Add-Result -Name "dotnet availability" -Status "FAIL" -Seconds 0 -ExitCode 127
            if ($StopOnFailure) {
                throw "dotnet not found in PATH"
            }
        }
        else {
            Invoke-External `
                -Name "HidControlServer.Tests" `
                -FilePath "dotnet" `
                -Arguments @("test", "Tools/Tests/HidControlServer.Tests/HidControlServer.Tests.csproj", "-c", $Configuration, "-v", $DotnetVerbosity) `
                -WorkingDirectory $repoRoot
        }
    }

    if (-not $SkipGo) {
        if (-not (Get-Command go -ErrorAction SilentlyContinue)) {
            Add-Result -Name "go availability" -Status "FAIL" -Seconds 0 -ExitCode 127
            if ($StopOnFailure) {
                throw "go not found in PATH"
            }
        }
        else {
            Invoke-External `
                -Name "WebRtcControlPeer go tests" `
                -FilePath "go" `
                -Arguments @("test", "./...") `
                -WorkingDirectory (Join-Path $repoRoot "Tools/WebRtcControlPeer")

            Invoke-External `
                -Name "WebRtcVideoPeer go tests" `
                -FilePath "go" `
                -Arguments @("test", "./...") `
                -WorkingDirectory (Join-Path $repoRoot "Tools/WebRtcVideoPeer")
        }
    }
}
catch {
    Write-Host ""
    Write-Host "Test runner aborted: $($_.Exception.Message)" -ForegroundColor Red
}

Write-Host ""
Write-Host "=== Summary ==="
$results | Format-Table -AutoSize

$hasFailures = $results | Where-Object { $_.Status -eq "FAIL" }
if ($hasFailures) {
    exit 1
}

exit 0

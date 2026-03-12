param(
    [ValidateSet("File", "Sql")]
    [string]$Provider = "File",
    [string]$Configuration = "Debug",
    [ValidateSet("quiet", "minimal", "normal", "detailed", "diagnostic")]
    [string]$DotnetVerbosity = "minimal",
    [string]$ConnectionString = "Host=127.0.0.1;Port=5434;Database=hidbridge;Username=hidbridge;Password=hidbridge",
    [string]$Schema = "hidbridge",
    [string]$BaseUrl = "http://127.0.0.1:18093",
    [string]$DataRoot = "",
    [string]$AccessToken = "",
    [switch]$EnableApiAuth,
    [string]$AuthAuthority = "",
    [string]$AuthAudience = "",
    [string]$TokenClientId = "controlplane-smoke",
    [string]$TokenClientSecret = "",
    [string]$TokenUsername = "operator.smoke.admin",
    [string]$TokenPassword = "ChangeMe123!",
    [string]$ViewerTokenUsername = "operator.smoke.viewer",
    [string]$ViewerTokenPassword = "ChangeMe123!",
    [string]$ForeignTokenUsername = "operator.smoke.foreign",
    [string]$ForeignTokenPassword = "ChangeMe123!",
    [switch]$DisableHeaderFallback,
    [switch]$BearerOnly,
    [switch]$NoBuild,
    [switch]$SkipUnitTests,
    [switch]$SkipSmoke,
    [switch]$StopOnFailure
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$scriptsRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$platformRoot = Split-Path -Parent $scriptsRoot
. (Join-Path $scriptsRoot "Common/ScriptCommon.ps1")
$logRoot = New-OperationalLogRoot -PlatformRoot $platformRoot -Category "checks"
$results = New-OperationalResultList

try {
    if (-not $SkipUnitTests) {
        $testParameters = @{ Configuration = $Configuration; DotnetVerbosity = $DotnetVerbosity }
        if ($StopOnFailure) { $testParameters.StopOnFailure = $true }
        Invoke-LoggedScript -Results $results -Name "Platform unit tests" -LogRoot $logRoot -ScriptPath (Join-Path $scriptsRoot "run_all_tests.ps1") -Parameters $testParameters -StopOnFailure:$StopOnFailure
    }

    if (-not $SkipSmoke) {
        $smokeParameters = @{ Provider = $Provider; Configuration = $Configuration; BaseUrl = $BaseUrl }
        if ($NoBuild -or -not $SkipUnitTests) { $smokeParameters.NoBuild = $true }
        if ($Provider -eq "Sql") {
            $smokeParameters.ConnectionString = $ConnectionString
            $smokeParameters.Schema = $Schema
        }
        elseif (-not [string]::IsNullOrWhiteSpace($DataRoot)) {
            $smokeParameters.DataRoot = $DataRoot
        }

        if (-not [string]::IsNullOrWhiteSpace($AccessToken)) { $smokeParameters.AccessToken = $AccessToken }
        if ($EnableApiAuth) {
            $smokeParameters.EnableApiAuth = $true
            $smokeParameters.AuthAuthority = $AuthAuthority
            $smokeParameters.AuthAudience = $AuthAudience
            $smokeParameters.TokenClientId = $TokenClientId
            $smokeParameters.TokenClientSecret = $TokenClientSecret
            $smokeParameters.TokenUsername = $TokenUsername
            $smokeParameters.TokenPassword = $TokenPassword
            $smokeParameters.ViewerTokenUsername = $ViewerTokenUsername
            $smokeParameters.ViewerTokenPassword = $ViewerTokenPassword
            $smokeParameters.ForeignTokenUsername = $ForeignTokenUsername
            $smokeParameters.ForeignTokenPassword = $ForeignTokenPassword
            if ($DisableHeaderFallback) { $smokeParameters.DisableHeaderFallback = $true }
            if ($BearerOnly) { $smokeParameters.BearerOnly = $true }
        }

        Invoke-LoggedScript -Results $results -Name "Platform smoke ($Provider)" -LogRoot $logRoot -ScriptPath (Join-Path $scriptsRoot "run_smoke.ps1") -Parameters $smokeParameters -StopOnFailure:$StopOnFailure
    }
}
catch {
    $abortLogPath = Join-Path $logRoot "checks.abort.log"
    Add-Content -Path $abortLogPath -Value $_.Exception.ToString()
    Add-OperationalResult -Results $results -Name "Checks orchestration" -Status "FAIL" -Seconds 0 -ExitCode 1 -LogPath $abortLogPath
    Write-Host ""
    Write-Host "Platform checks aborted: $($_.Exception.Message)" -ForegroundColor Red
}

Write-OperationalSummary -Results $results -LogRoot $logRoot

if ($results | Where-Object { $_.Status -eq "FAIL" }) {
    exit 1
}

exit 0

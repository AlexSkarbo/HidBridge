param(
    [string]$Configuration = "Debug",
    [string]$ConnectionString = "Host=127.0.0.1;Port=5434;Database=hidbridge;Username=hidbridge;Password=hidbridge",
    [string]$Schema = "hidbridge",
    [string]$AuthAuthority = "http://127.0.0.1:18096/realms/hidbridge-dev",
    [switch]$StopOnFailure,
    [switch]$SkipRealmSync,
    [switch]$SkipCiLocal,
    [switch]$IncludeWebRtcEdgeAgentAcceptance,
    [ValidateSet("uart", "controlws")]
    [string]$WebRtcCommandExecutor = "uart",
    [string]$WebRtcControlHealthUrl = "http://127.0.0.1:28092/health",
    [int]$WebRtcPeerReadyTimeoutSec = 45,
    [switch]$ExportArtifactsOnFailure = $true,
    [switch]$IncludeSmokeDataOnFailure = $true,
    [switch]$IncludeBackupsOnFailure = $true
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$scriptsRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$platformRoot = Split-Path -Parent $scriptsRoot
. (Join-Path $scriptsRoot "Common/ScriptCommon.ps1")

$logRoot = New-OperationalLogRoot -PlatformRoot $platformRoot -Category "full"
$results = New-OperationalResultList
$artifactExportPath = ""

try {
    if (-not $SkipRealmSync) {
        Invoke-LoggedScript -Results $results -Name "Realm Sync" -LogRoot $logRoot -ScriptPath (Join-Path $platformRoot "Identity/Keycloak/Sync-HidBridgeDevRealm.ps1") -Parameters @{} -StopOnFailure:$StopOnFailure
    }

    if (-not $SkipCiLocal) {
        Invoke-LoggedScript -Results $results -Name "CI Local" -LogRoot $logRoot -ScriptPath (Join-Path $scriptsRoot "run_ci_local.ps1") -Parameters @{
            Configuration = $Configuration
            ConnectionString = $ConnectionString
            Schema = $Schema
            AuthAuthority = $AuthAuthority
            IncludeWebRtcEdgeAgentAcceptance = $IncludeWebRtcEdgeAgentAcceptance
            WebRtcCommandExecutor = $WebRtcCommandExecutor
            WebRtcControlHealthUrl = $WebRtcControlHealthUrl
            WebRtcPeerReadyTimeoutSec = [Math]::Max(5, $WebRtcPeerReadyTimeoutSec)
            ExportArtifactsOnFailure = $ExportArtifactsOnFailure
            IncludeSmokeDataOnFailure = $IncludeSmokeDataOnFailure
            IncludeBackupsOnFailure = $IncludeBackupsOnFailure
        } -StopOnFailure:$StopOnFailure
    }
}
catch {
    $abortLogPath = Join-Path $logRoot "full.abort.log"
    Add-Content -Path $abortLogPath -Value $_.Exception.ToString()
    Add-OperationalResult -Results $results -Name "Full orchestration" -Status "FAIL" -Seconds 0 -ExitCode 1 -LogPath $abortLogPath
    Write-Host ""
    Write-Host "Full run aborted: $($_.Exception.Message)" -ForegroundColor Red
}

$failed = @($results | Where-Object { $_.Status -eq "FAIL" })
if ($ExportArtifactsOnFailure -and $failed.Count -gt 0) {
    $artifactExportPath = Join-Path $platformRoot ("Artifacts/full-{0}" -f [DateTimeOffset]::UtcNow.ToString('yyyyMMdd-HHmmss'))
    $exportScript = Join-Path $scriptsRoot "run_export_artifacts.ps1"
    $exportParams = @{ OutputRoot = $artifactExportPath }
    if ($IncludeSmokeDataOnFailure) { $exportParams.IncludeSmokeData = $true }
    if ($IncludeBackupsOnFailure) { $exportParams.IncludeBackups = $true }

    try {
        & $exportScript @exportParams *> (Join-Path $logRoot "export-artifacts.log")
    }
    catch {
        Write-Host ""
        Write-Host "Artifact export failed: $($_.Exception.Message)" -ForegroundColor Yellow
    }
}

Write-OperationalSummary -Results $results -LogRoot $logRoot -Header "Full Run Summary"
if (-not [string]::IsNullOrWhiteSpace($artifactExportPath)) {
    Write-Host ""
    Write-Host "Artifacts: $artifactExportPath"
}

if ($failed.Count -gt 0) { exit 1 }
exit 0

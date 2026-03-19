param(
    [string]$Configuration = "Debug",
    [string]$ConnectionString = "Host=127.0.0.1;Port=5434;Database=hidbridge;Username=hidbridge;Password=hidbridge",
    [string]$Schema = "hidbridge",
    [string]$AuthAuthority = "http://127.0.0.1:18096/realms/hidbridge-dev",
    [switch]$SkipDoctor,
    [switch]$SkipChecks,
    [switch]$SkipBearerSmoke,
    [bool]$IncludeWebRtcEdgeAgentAcceptance = $true,
    [switch]$SkipWebRtcEdgeAgentAcceptance,
    [ValidateSet("uart", "controlws")]
    [string]$WebRtcCommandExecutor = "uart",
    [switch]$AllowLegacyControlWs,
    [string]$WebRtcControlHealthUrl = "http://127.0.0.1:28092/health",
    [int]$WebRtcPeerReadyTimeoutSec = 45,
    [switch]$WebRtcStopExistingBeforeAcceptance = $true,
    [switch]$WebRtcStopStackAfterAcceptance = $true,
    [switch]$StopOnFailure,
    [switch]$ExportArtifactsOnFailure = $true,
    [switch]$IncludeSmokeDataOnFailure = $true,
    [switch]$IncludeBackupsOnFailure
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$scriptsRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$platformRoot = Split-Path -Parent $scriptsRoot
. (Join-Path $scriptsRoot "Common/ScriptCommon.ps1")
$logRoot = New-OperationalLogRoot -PlatformRoot $platformRoot -Category "ci-local"
$results = New-OperationalResultList
$artifactExportPath = ""

try {
    if (-not $SkipDoctor) {
        Invoke-LoggedScript -Results $results -Name "Doctor" -LogRoot $logRoot -ScriptPath (Join-Path $scriptsRoot "run_doctor.ps1") -Parameters @{ StartApiProbe = $true; RequireApi = $true } -StopOnFailure:$StopOnFailure
    }

    if (-not $SkipChecks) {
        Invoke-LoggedScript -Results $results -Name "Checks (Sql)" -LogRoot $logRoot -ScriptPath (Join-Path $scriptsRoot "run_checks.ps1") -Parameters @{ Provider = "Sql"; Configuration = $Configuration; ConnectionString = $ConnectionString; Schema = $Schema } -StopOnFailure:$StopOnFailure
    }

    if (-not $SkipBearerSmoke) {
        Invoke-LoggedScript -Results $results -Name "Bearer Smoke" -LogRoot $logRoot -ScriptPath (Join-Path $scriptsRoot "run_api_bearer_smoke.ps1") -Parameters @{ Configuration = $Configuration; ConnectionString = $ConnectionString; Schema = $Schema; AuthAuthority = $AuthAuthority } -StopOnFailure:$StopOnFailure
    }

    if (-not $IncludeWebRtcEdgeAgentAcceptance -and -not $SkipWebRtcEdgeAgentAcceptance) {
        throw "WebRTC edge-agent acceptance lane is required for CI gate. Set -SkipWebRtcEdgeAgentAcceptance only for emergency/local override."
    }

    if ($IncludeWebRtcEdgeAgentAcceptance -and -not $SkipWebRtcEdgeAgentAcceptance) {
        if ([string]::Equals($WebRtcCommandExecutor, "controlws", [StringComparison]::OrdinalIgnoreCase) -and -not $AllowLegacyControlWs) {
            throw "WebRtcCommandExecutor 'controlws' is legacy compatibility mode. Use 'uart' for production path, or pass -AllowLegacyControlWs explicitly."
        }

        # Runs end-to-end API + edge-agent relay smoke directly (stack bootstrap + Applied command assertion).
        $webrtcAcceptanceParameters = @{
            ApiBaseUrl = "http://127.0.0.1:18093"
            CommandExecutor = $WebRtcCommandExecutor
            ControlHealthUrl = $WebRtcControlHealthUrl
            PeerReadyTimeoutSec = [Math]::Max(5, $WebRtcPeerReadyTimeoutSec)
        }
        if ($AllowLegacyControlWs) {
            $webrtcAcceptanceParameters.AllowLegacyControlWs = $true
        }
        if ($WebRtcStopExistingBeforeAcceptance) {
            $webrtcAcceptanceParameters.StopExisting = $true
        }
        if ($WebRtcStopStackAfterAcceptance) {
            $webrtcAcceptanceParameters.StopStackAfter = $true
        }

        Invoke-LoggedScript -Results $results -Name "WebRTC Edge Agent Acceptance" -LogRoot $logRoot -ScriptPath (Join-Path $scriptsRoot "run_webrtc_edge_agent_acceptance.ps1") -Parameters $webrtcAcceptanceParameters -StopOnFailure:$StopOnFailure
    }
}
catch {
    $abortLogPath = Join-Path $logRoot "ci-local.abort.log"
    Add-Content -Path $abortLogPath -Value $_.Exception.ToString()
    Add-OperationalResult -Results $results -Name "CI Local orchestration" -Status "FAIL" -Seconds 0 -ExitCode 1 -LogPath $abortLogPath
    Write-Host ""
    Write-Host "Local CI aborted: $($_.Exception.Message)" -ForegroundColor Red
}

$failed = @($results | Where-Object { $_.Status -eq "FAIL" })
if ($ExportArtifactsOnFailure -and $failed.Count -gt 0) {
    $artifactExportPath = Join-Path $platformRoot ("Artifacts/ci-local-{0}" -f [DateTimeOffset]::UtcNow.ToString('yyyyMMdd-HHmmss'))
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

Write-OperationalSummary -Results $results -LogRoot $logRoot -Header "CI Local Summary"
if (-not [string]::IsNullOrWhiteSpace($artifactExportPath)) {
    Write-Host ""
    Write-Host "Artifacts: $artifactExportPath"
}
if ($results | Where-Object { $_.Status -eq "FAIL" }) { exit 1 }
exit 0

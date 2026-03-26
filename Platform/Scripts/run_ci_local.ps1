param(
    [string]$Configuration = "Debug",
    [string]$ConnectionString = "Host=127.0.0.1;Port=5434;Database=hidbridge;Username=hidbridge;Password=hidbridge",
    [string]$Schema = "hidbridge",
    [string]$AuthAuthority = "http://host.docker.internal:18096/realms/hidbridge-dev",
    [string]$KeycloakBaseUrl = "http://host.docker.internal:18096",
    [string]$TokenClientId = "controlplane-smoke",
    [string]$TokenClientSecret = "",
    [string]$TokenScope = "openid",
    [string]$TokenUsername = "operator.smoke.admin",
    [string]$TokenPassword = "ChangeMe123!",
    [string]$ViewerTokenUsername = "operator.smoke.viewer",
    [string]$ViewerTokenPassword = "ChangeMe123!",
    [string]$ForeignTokenUsername = "operator.smoke.foreign",
    [string]$ForeignTokenPassword = "ChangeMe123!",
    [switch]$SkipDoctor,
    [switch]$SkipChecks,
    [switch]$SkipBearerSmoke,
    [bool]$IncludeWebRtcEdgeAgentAcceptance = $true,
    [switch]$SkipWebRtcEdgeAgentAcceptance,
    [bool]$IncludeWebRtcMediaE2EGate = $true,
    [switch]$SkipWebRtcMediaE2EGate,
    [int]$WebRtcMediaHealthAttempts = 20,
    [int]$WebRtcMediaHealthDelayMs = 500,
    [bool]$IncludeOpsSloSecurityVerify = $true,
    [switch]$SkipOpsSloSecurityVerify,
    [int]$OpsSloWindowMinutes = 60,
    [switch]$OpsFailOnSloWarning,
    [switch]$OpsFailOnSecurityWarning,
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
        Invoke-LoggedScript -Results $results -Name "Doctor" -LogRoot $logRoot -ScriptPath (Join-Path $scriptsRoot "run_doctor.ps1") -Parameters @{
            StartApiProbe = $true
            RequireApi = $true
            KeycloakBaseUrl = $KeycloakBaseUrl
            SmokeClientId = $TokenClientId
            SmokeAdminUser = $TokenUsername
            SmokeAdminPassword = $TokenPassword
            SmokeViewerUser = $ViewerTokenUsername
            SmokeViewerPassword = $ViewerTokenPassword
            SmokeForeignUser = $ForeignTokenUsername
            SmokeForeignPassword = $ForeignTokenPassword
        } -StopOnFailure:$StopOnFailure
    }

    if (-not $SkipChecks) {
        Invoke-LoggedScript -Results $results -Name "Checks (Sql)" -LogRoot $logRoot -ScriptPath (Join-Path $scriptsRoot "run_checks.ps1") -Parameters @{
            Provider = "Sql"
            Configuration = $Configuration
            ConnectionString = $ConnectionString
            Schema = $Schema
            EnableApiAuth = $true
            AuthAuthority = $AuthAuthority
            AuthAudience = ""
            TokenClientId = $TokenClientId
            TokenClientSecret = $TokenClientSecret
            TokenUsername = $TokenUsername
            TokenPassword = $TokenPassword
            ViewerTokenUsername = $ViewerTokenUsername
            ViewerTokenPassword = $ViewerTokenPassword
            ForeignTokenUsername = $ForeignTokenUsername
            ForeignTokenPassword = $ForeignTokenPassword
            DisableHeaderFallback = $true
            BearerOnly = $true
        } -StopOnFailure:$StopOnFailure
    }

    if (-not $SkipBearerSmoke) {
        Invoke-LoggedScript -Results $results -Name "Bearer Smoke" -LogRoot $logRoot -ScriptPath (Join-Path $scriptsRoot "run_api_bearer_smoke.ps1") -Parameters @{
            Configuration = $Configuration
            ConnectionString = $ConnectionString
            Schema = $Schema
            AuthAuthority = $AuthAuthority
            TokenClientId = $TokenClientId
            TokenClientSecret = $TokenClientSecret
            TokenScope = $TokenScope
            TokenUsername = $TokenUsername
            TokenPassword = $TokenPassword
            ViewerTokenUsername = $ViewerTokenUsername
            ViewerTokenPassword = $ViewerTokenPassword
            ForeignTokenUsername = $ForeignTokenUsername
            ForeignTokenPassword = $ForeignTokenPassword
        } -StopOnFailure:$StopOnFailure
    }

    if (-not $IncludeWebRtcEdgeAgentAcceptance -and -not $SkipWebRtcEdgeAgentAcceptance) {
        throw "WebRTC edge-agent acceptance lane is required for CI gate. Set -SkipWebRtcEdgeAgentAcceptance only for emergency/local override."
    }

    if ($IncludeWebRtcEdgeAgentAcceptance -and -not $SkipWebRtcEdgeAgentAcceptance) {
        if (-not $IncludeWebRtcMediaE2EGate -and -not $SkipWebRtcMediaE2EGate) {
            throw "WebRTC media E2E gate is required for CI gate. Set -SkipWebRtcMediaE2EGate only for emergency/local override."
        }

        if ([string]::Equals($WebRtcCommandExecutor, "controlws", [StringComparison]::OrdinalIgnoreCase) -and -not $AllowLegacyControlWs) {
            throw "WebRtcCommandExecutor 'controlws' is legacy compatibility mode. Use 'uart' for production path, or pass -AllowLegacyControlWs explicitly."
        }

        # Runs end-to-end API + edge-agent relay smoke directly (stack bootstrap + Applied command assertion).
        $webrtcAcceptanceParameters = @{
            ApiBaseUrl = "http://127.0.0.1:18093"
            CommandExecutor = $WebRtcCommandExecutor
            ControlHealthUrl = $WebRtcControlHealthUrl
            KeycloakBaseUrl = $KeycloakBaseUrl
            TokenClientId = $TokenClientId
            TokenClientSecret = $TokenClientSecret
            TokenScope = $TokenScope
            TokenUsername = $TokenUsername
            TokenPassword = $TokenPassword
            PeerReadyTimeoutSec = [Math]::Max(5, $WebRtcPeerReadyTimeoutSec)
            SkipRuntimeBootstrap = $true
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
        if ($IncludeWebRtcMediaE2EGate -and -not $SkipWebRtcMediaE2EGate) {
            $webrtcAcceptanceParameters.RequireMediaReady = $true
            $webrtcAcceptanceParameters.RequireMediaPlaybackUrl = $true
            $webrtcAcceptanceParameters.MediaHealthAttempts = [Math]::Max(1, $WebRtcMediaHealthAttempts)
            $webrtcAcceptanceParameters.MediaHealthDelayMs = [Math]::Max(100, $WebRtcMediaHealthDelayMs)
        }

        Invoke-LoggedScript -Results $results -Name "WebRTC Edge Agent Acceptance" -LogRoot $logRoot -ScriptPath (Join-Path $scriptsRoot "run_webrtc_edge_agent_acceptance.ps1") -Parameters $webrtcAcceptanceParameters -StopOnFailure:$StopOnFailure
    }

    if (-not $IncludeOpsSloSecurityVerify -and -not $SkipOpsSloSecurityVerify) {
        throw "Ops SLO + Security verify lane is required for CI gate. Set -SkipOpsSloSecurityVerify only for emergency/local override."
    }

    if ($IncludeOpsSloSecurityVerify -and -not $SkipOpsSloSecurityVerify) {
        $opsVerifyParameters = @{
            BaseUrl = "http://127.0.0.1:18093"
            SloWindowMinutes = [Math]::Max(5, $OpsSloWindowMinutes)
            AuthAuthority = $AuthAuthority
            TokenClientId = $TokenClientId
            TokenClientSecret = $TokenClientSecret
            TokenScope = $TokenScope
            TokenUsername = $TokenUsername
            TokenPassword = $TokenPassword
            RequireAuditTrailCategories = ($IncludeWebRtcEdgeAgentAcceptance -and -not $SkipWebRtcEdgeAgentAcceptance)
        }
        if ($OpsFailOnSloWarning) {
            $opsVerifyParameters.FailOnSloWarning = $true
        }
        if ($OpsFailOnSecurityWarning) {
            $opsVerifyParameters.FailOnSecurityWarning = $true
        }

        Invoke-LoggedScript -Results $results -Name "Ops SLO + Security Verify" -LogRoot $logRoot -ScriptPath (Join-Path $scriptsRoot "run_ops_slo_security_verify.ps1") -Parameters $opsVerifyParameters -StopOnFailure:$StopOnFailure
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

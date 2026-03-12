param(
    [ValidateSet(1, 2, 3, 4)]
    [int]$Phase,
    [switch]$PrintOnly,
    [switch]$ApplyOnly,
    [switch]$SkipDoctor,
    [switch]$SkipSmoke,
    [switch]$SkipFull,
    [switch]$NoAutoRollback
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$scriptsRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$platformRoot = Split-Path -Parent $scriptsRoot
. (Join-Path $scriptsRoot "Common/ScriptCommon.ps1")

$phaseConfig = @{
    0 = @{
        Name  = "Phase 0"
        Label = "current baseline"
        Value = ""
    }
    1 = @{
        Name  = "Phase 1"
        Label = "control mutation paths"
        Value = "/api/v1/sessions/*/control/request;/api/v1/sessions/*/control/grant;/api/v1/sessions/*/control/force-takeover;/api/v1/sessions/*/control/release"
    }
    2 = @{
        Name  = "Phase 2"
        Label = "control + invitation mutation paths"
        Value = "/api/v1/sessions/*/control/request;/api/v1/sessions/*/control/grant;/api/v1/sessions/*/control/force-takeover;/api/v1/sessions/*/control/release;/api/v1/sessions/*/invitations/requests;/api/v1/sessions/*/invitations/*/approve;/api/v1/sessions/*/invitations/*/decline"
    }
    3 = @{
        Name  = "Phase 3"
        Label = "control + invitation + share/participant mutation paths"
        Value = "/api/v1/sessions/*/control/request;/api/v1/sessions/*/control/grant;/api/v1/sessions/*/control/force-takeover;/api/v1/sessions/*/control/release;/api/v1/sessions/*/invitations/requests;/api/v1/sessions/*/invitations/*/approve;/api/v1/sessions/*/invitations/*/decline;/api/v1/sessions/*/participants;/api/v1/sessions/*/participants/*;/api/v1/sessions/*/shares;/api/v1/sessions/*/shares/*/accept;/api/v1/sessions/*/shares/*/reject;/api/v1/sessions/*/shares/*/revoke"
    }
    4 = @{
        Name  = "Phase 4"
        Label = "control + invitation + share/participant + command mutation paths"
        Value = "/api/v1/sessions/*/control/request;/api/v1/sessions/*/control/grant;/api/v1/sessions/*/control/force-takeover;/api/v1/sessions/*/control/release;/api/v1/sessions/*/invitations/requests;/api/v1/sessions/*/invitations/*/approve;/api/v1/sessions/*/invitations/*/decline;/api/v1/sessions/*/participants;/api/v1/sessions/*/participants/*;/api/v1/sessions/*/shares;/api/v1/sessions/*/shares/*/accept;/api/v1/sessions/*/shares/*/reject;/api/v1/sessions/*/shares/*/revoke;/api/v1/sessions/*/commands"
    }
}

$phaseInfo = $phaseConfig[$Phase]
$value = $phaseInfo.Value

if ($PrintOnly) {
    Write-Output ('${env:HIDBRIDGE_AUTH_HEADER_FALLBACK_DISABLED_PATTERNS}="{0}"' -f $value).Replace('${env', '$env')
    return
}

$env:HIDBRIDGE_AUTH_HEADER_FALLBACK_DISABLED_PATTERNS = $value

Write-Host ("Applied bearer rollout {0} preset for {1}." -f $phaseInfo.Name, $phaseInfo.Label)
Write-Host ("HIDBRIDGE_AUTH_HEADER_FALLBACK_DISABLED_PATTERNS={0}" -f $value)

if ($ApplyOnly) {
    Write-Host ""
    Write-Host "Apply-only mode complete."
    Write-Host "To persist in the current shell, dot-source this command:"
    Write-Host ("  . .\Platform\Scripts\run_bearer_rollout_phase.ps1 -Phase {0} -ApplyOnly" -f $Phase)
    return
}

function Invoke-PhaseValidation {
    param(
        [int]$EffectivePhase,
        [string]$CategorySuffix = ""
    )

    $effectiveInfo = $phaseConfig[$EffectivePhase]
    $env:HIDBRIDGE_AUTH_HEADER_FALLBACK_DISABLED_PATTERNS = $effectiveInfo.Value
    $logRoot = New-OperationalLogRoot -PlatformRoot $platformRoot -Category ("bearer-rollout-phase{0}{1}" -f $EffectivePhase, $CategorySuffix)
    $results = New-OperationalResultList
    $failureMessage = $null

    try {
        if (-not $SkipDoctor) {
            Invoke-LoggedScript -Results $results -Name "Doctor" -LogRoot $logRoot -ScriptPath (Join-Path $platformRoot "run.ps1") -Parameters @{ Task = "doctor" } -StopOnFailure:$true
        }

        if (-not $SkipSmoke) {
            Invoke-LoggedScript -Results $results -Name "Bearer Smoke" -LogRoot $logRoot -ScriptPath (Join-Path $platformRoot "run.ps1") -Parameters @{ Task = "smoke-bearer" } -StopOnFailure:$true
        }

        if (-not $SkipFull) {
            Invoke-LoggedScript -Results $results -Name "Full" -LogRoot $logRoot -ScriptPath (Join-Path $platformRoot "run.ps1") -Parameters @{ Task = "full" } -StopOnFailure:$true
        }
    }
    catch {
        $failureMessage = $_.Exception.Message
    }

    return [pscustomobject]@{
        Phase          = $EffectivePhase
        PhaseName      = $effectiveInfo.Name
        Value          = $effectiveInfo.Value
        LogRoot        = $logRoot
        Results        = $results
        FailureMessage = $failureMessage
        Failed         = @($results | Where-Object { $_.Status -eq "FAIL" }).Count -gt 0
    }
}

$attempt = Invoke-PhaseValidation -EffectivePhase $Phase
if (-not $attempt.Failed) {
    Write-OperationalSummary -Results $attempt.Results -LogRoot $attempt.LogRoot -Header ("Bearer Rollout {0} Summary" -f $attempt.PhaseName)
    exit 0
}

if ($NoAutoRollback -or $Phase -le 1) {
    Write-Host ""
    Write-Host ("{0} failed: {1}" -f $attempt.PhaseName, $attempt.FailureMessage) -ForegroundColor Red
    Write-OperationalSummary -Results $attempt.Results -LogRoot $attempt.LogRoot -Header ("Bearer Rollout {0} Summary" -f $attempt.PhaseName)
    exit 1
}

Write-Host ""
Write-Host ("{0} failed: {1}" -f $attempt.PhaseName, $attempt.FailureMessage) -ForegroundColor Yellow
Write-Host ("Auto-rollback search is starting from Phase {0} down to Phase 0." -f ($Phase - 1)) -ForegroundColor Yellow

$rollbackAttempts = @()
for ($fallbackPhase = $Phase - 1; $fallbackPhase -ge 0; $fallbackPhase--) {
    $rollback = Invoke-PhaseValidation -EffectivePhase $fallbackPhase -CategorySuffix "-rollback"
    $rollbackAttempts += $rollback

    if (-not $rollback.Failed) {
        Write-Host ""
        Write-Host ("Requested {0} is blocked in the current realm. Effective rollout remains {1}." -f $attempt.PhaseName, $rollback.PhaseName) -ForegroundColor Yellow
        Write-OperationalSummary -Results $rollback.Results -LogRoot $rollback.LogRoot -Header ("Bearer Rollout Fallback Summary ({0})" -f $rollback.PhaseName)
        exit 0
    }

    Write-Host ("Rollback candidate {0} failed: {1}" -f $rollback.PhaseName, $rollback.FailureMessage) -ForegroundColor Yellow
}

$lastRollback = $rollbackAttempts[-1]
Write-Host ""
Write-Host ("All fallback phases down to Phase 0 failed. Last failure: {0}" -f $lastRollback.FailureMessage) -ForegroundColor Red
Write-OperationalSummary -Results $lastRollback.Results -LogRoot $lastRollback.LogRoot -Header ("Bearer Rollout Fallback Summary ({0})" -f $lastRollback.PhaseName)
exit 1

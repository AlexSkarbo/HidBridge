param(
    [string]$ApiBaseUrl = "http://127.0.0.1:18093",
    [ValidateSet("uart", "controlws")]
    [string]$CommandExecutor = "uart",
    [switch]$AllowLegacyControlWs,
    [string]$ControlHealthUrl = "http://127.0.0.1:28092/health",
    [string]$ControlWsUrl = "",
    [int]$RequestTimeoutSec = 15,
    [int]$ControlHealthAttempts = 20,
    [switch]$SkipTransportHealthCheck,
    [int]$TransportHealthAttempts = 20,
    [int]$TransportHealthDelayMs = 500,
    [string]$UartPort = "COM6",
    [int]$UartBaud = 3000000,
    [string]$UartHmacKey = "your-master-secret",
    [string]$PrincipalId = "smoke-runner",
    [int]$PeerReadyTimeoutSec = 45,
    [switch]$SkipRuntimeBootstrap,
    [switch]$StopExisting,
    [switch]$StopStackAfter,
    [string]$OutputJsonPath = "Platform/.logs/webrtc-edge-agent-acceptance.result.json"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$scriptsRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$platformRoot = Split-Path -Parent $scriptsRoot
. (Join-Path $scriptsRoot "Common/ScriptCommon.ps1")

$stackScript = Join-Path $scriptsRoot "run_webrtc_stack.ps1"
$smokeScript = Join-Path $scriptsRoot "run_webrtc_edge_agent_smoke.ps1"
$logRoot = New-OperationalLogRoot -PlatformRoot $platformRoot -Category "webrtc-edge-agent-acceptance"
$results = New-OperationalResultList
$stackSummaryPath = Join-Path $logRoot "webrtc-stack.summary.json"
$smokeSummaryPath = Join-Path $logRoot "webrtc-edge-agent-smoke.result.json"

if ([string]::Equals($CommandExecutor, "controlws", [StringComparison]::OrdinalIgnoreCase) -and -not $AllowLegacyControlWs) {
    throw "CommandExecutor 'controlws' is legacy compatibility mode. Use 'uart' for production path, or pass -AllowLegacyControlWs explicitly."
}

# Converts control health endpoint to websocket endpoint for controlws executor.
function Convert-ControlHealthToWebSocketUrl {
    param([string]$HealthUrl)

    $uri = [Uri]$HealthUrl
    $builder = [System.UriBuilder]::new($uri)
    $builder.Scheme = if ([string]::Equals($uri.Scheme, "https", [StringComparison]::OrdinalIgnoreCase)) { "wss" } else { "ws" }
    if ($builder.Path.EndsWith("/health", [StringComparison]::OrdinalIgnoreCase)) {
        $builder.Path = $builder.Path.Substring(0, $builder.Path.Length - "/health".Length) + "/ws/control"
    }
    else {
        $builder.Path = "/ws/control"
    }

    return $builder.Uri.AbsoluteUri
}

# Reads JSON from disk and returns $null when file is missing or invalid.
function Read-JsonOrNull {
    param([string]$Path)

    if ([string]::IsNullOrWhiteSpace($Path) -or -not (Test-Path -Path $Path -PathType Leaf)) {
        return $null
    }

    try {
        return (Get-Content -Raw -Path $Path | ConvertFrom-Json)
    }
    catch {
        return $null
    }
}

# Stops one process by id and keeps acceptance flow alive on cleanup failures.
function Stop-OptionalProcess {
    param([object]$PidValue)

    $targetPid = 0
    if ($null -eq $PidValue -or -not [int]::TryParse([string]$PidValue, [ref]$targetPid) -or $targetPid -le 0) {
        return
    }

    try {
        Stop-Process -Id $targetPid -Force -ErrorAction Stop
        Write-Host "Stopped PID $targetPid"
    }
    catch {
        Write-Warning "Failed to stop PID ${targetPid}: $($_.Exception.Message)"
    }
}

try {
    $stackParameters = @{
        ApiBaseUrl = $ApiBaseUrl
        CommandExecutor = $CommandExecutor
        UartPort = $UartPort
        UartBaud = [Math]::Max(300, $UartBaud)
        UartHmacKey = $UartHmacKey
        PrincipalId = $PrincipalId
        SkipIdentityReset = $true
        SkipCiLocal = $true
        OutputJsonPath = $stackSummaryPath
    }
    if ($SkipRuntimeBootstrap) { $stackParameters.SkipRuntimeBootstrap = $true }
    if ($StopExisting) { $stackParameters.StopExisting = $true }
    if ($AllowLegacyControlWs) { $stackParameters.AllowLegacyControlWs = $true }

    if ([string]::Equals($CommandExecutor, "controlws", [StringComparison]::OrdinalIgnoreCase)) {
        $effectiveControlWsUrl = if ([string]::IsNullOrWhiteSpace($ControlWsUrl)) { Convert-ControlHealthToWebSocketUrl -HealthUrl $ControlHealthUrl } else { $ControlWsUrl }
        $stackParameters.ControlWsUrl = $effectiveControlWsUrl
    }

    Invoke-LoggedScript -Results $results -Name "WebRTC Stack" -LogRoot $logRoot -ScriptPath $stackScript -Parameters $stackParameters -StopOnFailure:$true

    $targetPeerReadyTimeoutSec = [Math]::Max(5, $PeerReadyTimeoutSec)
    $effectiveTransportHealthDelayMs = [Math]::Max(100, $TransportHealthDelayMs)
    $minTransportAttemptsByTimeout = [int][Math]::Ceiling(($targetPeerReadyTimeoutSec * 1000.0) / $effectiveTransportHealthDelayMs)

    $smokeParameters = @{
        ApiBaseUrl = $ApiBaseUrl
        ControlHealthUrl = $ControlHealthUrl
        RequestTimeoutSec = [Math]::Max(1, $RequestTimeoutSec)
        ControlHealthAttempts = [Math]::Max(1, $ControlHealthAttempts)
        TransportHealthAttempts = [Math]::Max([Math]::Max(1, $TransportHealthAttempts), $minTransportAttemptsByTimeout)
        TransportHealthDelayMs = $effectiveTransportHealthDelayMs
        PrincipalId = $PrincipalId
        OutputJsonPath = $smokeSummaryPath
    }
    if ($SkipTransportHealthCheck) {
        $smokeParameters.SkipTransportHealthCheck = $true
    }

    Invoke-LoggedScript -Results $results -Name "WebRTC Edge Agent Smoke" -LogRoot $logRoot -ScriptPath $smokeScript -Parameters $smokeParameters -StopOnFailure:$true
}
catch {
    $abortPath = Join-Path $logRoot "webrtc-edge-agent-acceptance.abort.log"
    Add-Content -Path $abortPath -Value $_.Exception.ToString()
    Add-OperationalResult -Results $results -Name "WebRTC Edge Agent Acceptance orchestration" -Status "FAIL" -Seconds 0 -ExitCode 1 -LogPath $abortPath
    Write-Host ""
    Write-Host "WebRTC edge-agent acceptance aborted: $($_.Exception.Message)" -ForegroundColor Red
}
finally {
    if ($StopStackAfter) {
        $stackSummary = Read-JsonOrNull -Path $stackSummaryPath
        if ($null -ne $stackSummary) {
            Stop-OptionalProcess -PidValue $stackSummary.adapterPid
            Stop-OptionalProcess -PidValue $stackSummary.exp022Pid
        }
    }
}

$failed = @($results | Where-Object { $_.Status -eq "FAIL" })
$smokeSummary = Read-JsonOrNull -Path $smokeSummaryPath
$stackSummary = Read-JsonOrNull -Path $stackSummaryPath
$finalSummary = [pscustomobject]@{
    apiBaseUrl = $ApiBaseUrl
    commandExecutor = $CommandExecutor
    stackSummaryPath = $stackSummaryPath
    smokeSummaryPath = $smokeSummaryPath
    stack = $stackSummary
    smoke = $smokeSummary
    pass = ($failed.Count -eq 0)
}

if (-not [string]::IsNullOrWhiteSpace($OutputJsonPath)) {
    $resolvedOutput = if ([System.IO.Path]::IsPathRooted($OutputJsonPath)) { $OutputJsonPath } else { Join-Path $platformRoot $OutputJsonPath }
    $resolvedOutputDir = Split-Path -Parent $resolvedOutput
    if (-not [string]::IsNullOrWhiteSpace($resolvedOutputDir) -and -not (Test-Path -Path $resolvedOutputDir)) {
        New-Item -ItemType Directory -Force -Path $resolvedOutputDir | Out-Null
    }

    $finalSummary | ConvertTo-Json -Depth 20 | Set-Content -Path $resolvedOutput -Encoding utf8
}

Write-OperationalSummary -Results $results -LogRoot $logRoot -Header "WebRTC Edge Agent Acceptance Summary"
if (-not [string]::IsNullOrWhiteSpace($OutputJsonPath)) {
    Write-Host ""
    Write-Host "Acceptance summary JSON: $OutputJsonPath"
}

if ($failed.Count -gt 0) {
    exit 1
}

exit 0

param(
    [ValidateSet("checks", "tests", "smoke", "smoke-file", "smoke-sql", "smoke-bearer", "doctor", "clean-logs", "ci-local", "full", "export-artifacts", "token-debug", "bearer-rollout", "identity-reset", "identity-onboard", "demo-flow", "demo-seed", "demo-gate", "uart-diagnostics", "webrtc-relay-smoke", "webrtc-peer-adapter", "webrtc-stack", "webrtc-stack-terminal-b", "webrtc-edge-agent-smoke", "webrtc-edge-agent-acceptance", "ops-slo-security-verify", "close-failed-rooms", "close-stale-rooms", "platform-runtime")]
    [string]$Task = "checks",
    [string]$BaseUrl,
    [switch]$RequireDeviceAck,
    [string]$TransportProvider,
    [int]$KeyboardInterfaceSelector = -1,
    [int]$StaleAfterMinutes = -1,
    [switch]$IncludeWebRtcEdgeAgentSmoke,
    [switch]$SkipDoctor,
    [switch]$SkipChecks,
    [switch]$SkipBearerSmoke,
    [switch]$SkipRealmSync,
    [switch]$SkipCiLocal,
    [switch]$StopOnFailure,
    [switch]$IncludeWebRtcEdgeAgentAcceptance,
    [switch]$SkipWebRtcEdgeAgentAcceptance,
    [switch]$IncludeWebRtcMediaE2EGate,
    [switch]$SkipWebRtcMediaE2EGate,
    [bool]$IncludeOpsSloSecurityVerify = $true,
    [switch]$SkipOpsSloSecurityVerify,
    [switch]$ExportArtifactsOnFailure,
    [switch]$IncludeSmokeDataOnFailure,
    [switch]$IncludeBackupsOnFailure,
    [string]$WebRtcCommandExecutor,
    [switch]$AllowLegacyControlWs,
    [string]$WebRtcControlHealthUrl,
    [int]$WebRtcRequestTimeoutSec = -1,
    [int]$WebRtcControlHealthAttempts = -1,
    [switch]$SkipControlHealthCheck,
    [string]$ControlHealthUrl,
    [int]$RequestTimeoutSec = -1,
    [int]$ControlHealthAttempts = -1,
    [switch]$SkipTransportHealthCheck,
    [int]$TransportHealthAttempts = -1,
    [int]$TransportHealthDelayMs = -1,
    [switch]$RequireMediaReady,
    [switch]$RequireMediaPlaybackUrl,
    [int]$MediaHealthAttempts = -1,
    [int]$MediaHealthDelayMs = -1,
    [int]$SloWindowMinutes = -1,
    [switch]$AllowAuthDisabled,
    [switch]$FailOnSloWarning,
    [switch]$FailOnSecurityWarning,
    [switch]$RequireAuditTrailCategories,
    [string]$CommandExecutor,
    [string]$ControlWsUrl,
    [string]$UartPort,
    [int]$UartBaud = -1,
    [string]$UartHmacKey,
    [int]$Exp022DurationSec = -1,
    [int]$AdapterDurationSec = -1,
    [int]$PeerReadyTimeoutSec = -1,
    [switch]$StopExisting,
    [switch]$StopStackAfter,
    [switch]$SkipRuntimeBootstrap,
    [string]$EndpointId,
    [string]$PrincipalId,
    [ValidateSet("up", "down", "restart", "status", "logs")]
    [string]$Action,
    [string]$ComposeFile,
    [switch]$Build,
    [switch]$Pull,
    [switch]$RemoveVolumes,
    [switch]$RemoveOrphans,
    [switch]$Follow,
    [switch]$SkipReadyWait,
    [int]$ReadyTimeoutSec = -1,
    [string]$OutputJsonPath,
    [string]$InterfaceSelectorsCsv,
    [Parameter(ValueFromRemainingArguments = $true)]
    [string[]]$ForwardArgs
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Add-BoundParametersToArgumentList {
    param(
        [hashtable]$Bound,
        [System.Collections.Generic.List[string]]$Target,
        [string[]]$Excluded
    )

    $excludedLookup = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
    foreach ($item in $Excluded) {
        [void]$excludedLookup.Add($item)
    }

    foreach ($entry in $Bound.GetEnumerator()) {
        $name = [string]$entry.Key
        if ($excludedLookup.Contains($name)) {
            continue
        }

        $value = $entry.Value
        if ($null -eq $value) {
            continue
        }

        if ($value -is [System.Management.Automation.SwitchParameter]) {
            if ($value.IsPresent) {
                $Target.Add("-$name") | Out-Null
            }
            continue
        }

        if ($value -is [bool]) {
            $Target.Add("-$name") | Out-Null
            $Target.Add($value.ToString().ToLowerInvariant()) | Out-Null
            continue
        }

        if ($value -is [System.Array]) {
            foreach ($element in $value) {
                if ($null -eq $element) {
                    continue
                }

                $text = [string]$element
                if ([string]::IsNullOrWhiteSpace($text)) {
                    continue
                }

                $Target.Add("-$name") | Out-Null
                $Target.Add($text) | Out-Null
            }
            continue
        }

        $Target.Add("-$name") | Out-Null
        $Target.Add([string]$value) | Out-Null
    }
}

$runtimeCtlProject = Join-Path $PSScriptRoot "Tools/HidBridge.RuntimeCtl/HidBridge.RuntimeCtl.csproj"
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
$arguments.Add($PSScriptRoot) | Out-Null

if ([string]::Equals($Task, "ci-local", [System.StringComparison]::OrdinalIgnoreCase) -or
    [string]::Equals($Task, "full", [System.StringComparison]::OrdinalIgnoreCase)) {
    $arguments.Add($Task) | Out-Null
} elseif ([string]::Equals($Task, "webrtc-edge-agent-acceptance", [System.StringComparison]::OrdinalIgnoreCase)) {
    $arguments.Add("webrtc-acceptance") | Out-Null
} elseif ([string]::Equals($Task, "ops-slo-security-verify", [System.StringComparison]::OrdinalIgnoreCase)) {
    $arguments.Add("ops-verify") | Out-Null
} else {
    $arguments.Add("task") | Out-Null
    $arguments.Add($Task) | Out-Null
}

Add-BoundParametersToArgumentList -Bound $PSBoundParameters -Target $arguments -Excluded @("Task", "ForwardArgs")

if ($null -ne $ForwardArgs) {
    foreach ($argument in $ForwardArgs) {
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

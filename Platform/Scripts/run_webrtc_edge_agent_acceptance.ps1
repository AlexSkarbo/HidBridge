param(
    [Alias("BaseUrl")]
    [string]$ApiBaseUrl = "http://127.0.0.1:18093",
    [ValidateSet("uart", "controlws")]
    [string]$CommandExecutor = "uart",
    [switch]$AllowLegacyControlWs,
    [string]$ControlHealthUrl = "http://127.0.0.1:28092/health",
    [string]$ControlWsUrl = "",
    [string]$KeycloakBaseUrl = "http://host.docker.internal:18096",
    [string]$RealmName = "hidbridge-dev",
    [string]$TokenClientId = "controlplane-smoke",
    [string]$TokenClientSecret = "",
    [string]$TokenScope = "openid",
    [string]$TokenUsername = "operator.smoke.admin",
    [string]$TokenPassword = "ChangeMe123!",
    [int]$RequestTimeoutSec = 15,
    [int]$ControlHealthAttempts = 20,
    [int]$KeycloakHealthAttempts = 60,
    [int]$KeycloakHealthDelayMs = 500,
    [int]$TokenRequestAttempts = 5,
    [int]$TokenRequestDelayMs = 500,
    [switch]$SkipTransportHealthCheck,
    [int]$TransportHealthAttempts = 20,
    [int]$TransportHealthDelayMs = 500,
    [switch]$RequireMediaReady,
    [switch]$RequireMediaPlaybackUrl,
    [int]$MediaHealthAttempts = 20,
    [int]$MediaHealthDelayMs = 500,
    [string]$UartPort = "COM6",
    [int]$UartBaud = 3000000,
    [string]$UartHmacKey = "your-master-secret",
    [string]$PrincipalId = "smoke-runner",
    [int]$PeerReadyTimeoutSec = 45,
    [switch]$SkipRuntimeBootstrap,
    [switch]$StopExisting,
    [switch]$StopStackAfter,
    [string]$OutputJsonPath = "Platform/.logs/webrtc-edge-agent-acceptance.result.json",
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
$arguments.Add("webrtc-acceptance") | Out-Null

Add-BoundParametersToArgumentList -Bound $PSBoundParameters -Target $arguments -Excluded @("ForwardArgs")

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

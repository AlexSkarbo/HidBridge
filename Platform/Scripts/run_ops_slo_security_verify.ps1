param(
    [string]$BaseUrl = "http://127.0.0.1:18093",
    [int]$SloWindowMinutes = 60,
    [string]$AccessToken = "auto",
    [string]$AuthAuthority = "http://host.docker.internal:18096/realms/hidbridge-dev",
    [string]$TokenClientId = "controlplane-smoke",
    [string]$TokenClientSecret = "",
    [string]$TokenScope = "openid profile email",
    [string]$TokenUsername = "operator.smoke.admin",
    [string]$TokenPassword = "ChangeMe123!",
    [switch]$AllowAuthDisabled,
    [switch]$FailOnSloWarning,
    [switch]$FailOnSecurityWarning,
    [switch]$RequireAuditTrailCategories,
    [int]$AuditSampleTake = 250,
    [string]$OutputJsonPath = "",
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
$arguments.Add("ops-verify") | Out-Null

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

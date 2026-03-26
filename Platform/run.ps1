Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Resolve-TaskInvocation {
    param([string[]]$Args)

    $task = "checks"
    $remaining = [System.Collections.Generic.List[string]]::new()

    for ($i = 0; $i -lt $Args.Count; $i++) {
        $token = $Args[$i]
        if ([string]::IsNullOrWhiteSpace($token)) {
            continue
        }

        if ($token -match "^(?i)-task:(.+)$") {
            $task = $matches[1]
            continue
        }

        if ([string]::Equals($token, "-Task", [StringComparison]::OrdinalIgnoreCase)) {
            if ($i + 1 -ge $Args.Count -or [string]::IsNullOrWhiteSpace($Args[$i + 1])) {
                throw "Parameter -Task requires a value."
            }

            $task = $Args[$i + 1]
            $i++
            continue
        }

        if ([string]::Equals($token, "-ForwardArgs", [StringComparison]::OrdinalIgnoreCase)) {
            # Compatibility: `-ForwardArgs @(...)` is legacy run.ps1 syntax.
            # We drop the marker and forward only actual argument tokens.
            continue
        }

        $remaining.Add($token) | Out-Null
    }

    if ($task -eq "checks" -and $remaining.Count -gt 0) {
        $first = $remaining[0]
        if (-not [string]::IsNullOrWhiteSpace($first) -and -not $first.StartsWith("-")) {
            $task = $first
            $remaining.RemoveAt(0)
        }
    }

    return @{
        Task = $task
        Remaining = $remaining
    }
}

$platformRoot = $PSScriptRoot
$runtimeCtlProject = Join-Path $platformRoot "Tools/HidBridge.RuntimeCtl/HidBridge.RuntimeCtl.csproj"
if (-not (Test-Path $runtimeCtlProject)) {
    throw "RuntimeCtl project not found: $runtimeCtlProject"
}

$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if ($null -eq $dotnet) {
    throw "dotnet CLI not found in PATH"
}

$rawArgs = if ($null -eq $args) { @() } else { $args }
$invocation = Resolve-TaskInvocation -Args $rawArgs
$task = [string]$invocation.Task
$forward = [System.Collections.Generic.List[string]]$invocation.Remaining

$arguments = [System.Collections.Generic.List[string]]::new()
$arguments.Add("run") | Out-Null
$arguments.Add("--project") | Out-Null
$arguments.Add($runtimeCtlProject) | Out-Null
$arguments.Add("--") | Out-Null
$arguments.Add("--platform-root") | Out-Null
$arguments.Add($platformRoot) | Out-Null

switch ($task.ToLowerInvariant()) {
    "ci-local" { $arguments.Add("ci-local") | Out-Null }
    "full" { $arguments.Add("full") | Out-Null }
    "webrtc-edge-agent-acceptance" { $arguments.Add("webrtc-acceptance") | Out-Null }
    "webrtc-edge-agent-smoke" { $arguments.Add("webrtc-edge-agent-smoke") | Out-Null }
    "ops-slo-security-verify" { $arguments.Add("ops-verify") | Out-Null }
    "demo-flow" { $arguments.Add("demo-flow") | Out-Null }
    "demo-gate" { $arguments.Add("demo-gate") | Out-Null }
    "webrtc-stack" { $arguments.Add("webrtc-stack") | Out-Null }
    default {
        $arguments.Add("task") | Out-Null
        $arguments.Add($task) | Out-Null
    }
}

foreach ($arg in $forward) {
    if (-not [string]::IsNullOrWhiteSpace($arg)) {
        $arguments.Add($arg) | Out-Null
    }
}

& $dotnet.Source @arguments
$exitCode = $LASTEXITCODE
if ($null -eq $exitCode) {
    $exitCode = 1
}

exit $exitCode

param(
    [string]$Configuration = "Debug",
    [string]$ConnectionString = "Host=127.0.0.1;Port=5434;Database=hidbridge;Username=hidbridge;Password=hidbridge",
    [string]$Schema = "hidbridge",
    [string]$AuthAuthority = "http://127.0.0.1:18096/realms/hidbridge-dev",
    [string]$ApiBaseUrl = "http://127.0.0.1:18093",
    [string]$WebBaseUrl = "http://127.0.0.1:18110",
    [switch]$SkipIdentityReset,
    [switch]$SkipDoctor,
    [switch]$SkipCiLocal,
    [switch]$IncludeFull,
    [switch]$SkipServiceStartup,
    [switch]$SkipDemoGate,
    [switch]$NoBuild,
    [switch]$ShowServiceWindows,
    [switch]$ReuseRunningServices,
    [switch]$EnableCallerContextScope,
    [switch]$RequireDemoGateDeviceAck,
    [int]$DemoGateKeyboardInterfaceSelector = -1,
    [switch]$IncludeWebRtcEdgeAgentSmoke,
    [string]$WebRtcControlHealthUrl = "http://127.0.0.1:28092/health",
    [int]$WebRtcRequestTimeoutSec = 15,
    [int]$WebRtcControlHealthAttempts = 20
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$scriptsRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$platformRoot = Split-Path -Parent $scriptsRoot
$repoRoot = Split-Path -Parent $platformRoot

. (Join-Path $scriptsRoot "Common/ScriptCommon.ps1")

$logRoot = New-OperationalLogRoot -PlatformRoot $platformRoot -Category "demo-flow"
$results = New-OperationalResultList
$runtimeProcesses = [System.Collections.Generic.List[object]]::new()
$runtimeServices = [System.Collections.Generic.List[object]]::new()
$serviceStartupAttempted = $false

$identityResetScript = Join-Path $platformRoot "run_identity_reset.ps1"
$doctorScript = Join-Path $scriptsRoot "run_doctor.ps1"
$ciLocalScript = Join-Path $scriptsRoot "run_ci_local.ps1"
$fullScript = Join-Path $scriptsRoot "run_full.ps1"
$demoSeedScript = Join-Path $scriptsRoot "run_demo_seed.ps1"
$demoGateScript = Join-Path $scriptsRoot "run_demo_gate.ps1"
$webrtcStackScript = Join-Path $scriptsRoot "run_webrtc_stack.ps1"
$webrtcEdgeAgentSmokeScript = Join-Path $scriptsRoot "run_webrtc_edge_agent_smoke.ps1"
$apiProjectPath = Join-Path $repoRoot "Platform/Platform/HidBridge.ControlPlane.Api/HidBridge.ControlPlane.Api.csproj"
$webProjectPath = Join-Path $repoRoot "Platform/Clients/HidBridge.ControlPlane.Web/HidBridge.ControlPlane.Web.csproj"
$apiUri = [Uri]$ApiBaseUrl
$webUri = [Uri]$WebBaseUrl
$authUri = [Uri]$AuthAuthority
$keycloakBaseUrl = if ($authUri.IsDefaultPort) {
    "$($authUri.Scheme)://$($authUri.Host)"
}
else {
    "$($authUri.Scheme)://$($authUri.Host):$($authUri.Port)"
}
$realmName = "hidbridge-dev"
if ($authUri.AbsolutePath -match "^/realms/([^/]+)$") {
    $realmName = $matches[1]
}

$effectiveReuseRunningServices = $ReuseRunningServices
$effectiveSkipCiLocal = $SkipCiLocal
$effectiveSkipDemoGate = $SkipDemoGate

function Add-DemoResult {
    param(
        [string]$Name,
        [ValidateSet("PASS", "WARN", "FAIL")]
        [string]$Status,
        [double]$Seconds,
        [int]$ExitCode,
        [string]$LogPath
    )

    Add-OperationalResult -Results $results -Name $Name -Status $Status -Seconds $Seconds -ExitCode $ExitCode -LogPath $LogPath
}

function Test-ApiHealth {
    param(
        [string]$BaseUrl
    )

    try {
        $response = Invoke-RestMethod -Method Get -Uri "$($BaseUrl.TrimEnd('/'))/health" -TimeoutSec 5
        return ($null -ne $response -and $response.status -eq "ok")
    }
    catch {
        return $false
    }
}

function Test-ControlHealth {
    param(
        [string]$Url,
        [int]$Attempts = 5,
        [int]$DelayMs = 500
    )

    for ($attempt = 0; $attempt -lt [Math]::Max(1, $Attempts); $attempt++) {
        try {
            $response = Invoke-RestMethod -Method Get -Uri $Url -TimeoutSec 2
            if (($response.PSObject.Properties["ok"] -and [bool]$response.ok) -or ($response -is [System.Collections.IDictionary] -and $response.Contains("ok") -and [bool]$response["ok"])) {
                return $true
            }
        }
        catch {
        }

        Start-Sleep -Milliseconds ([Math]::Max(100, $DelayMs))
    }

    return $false
}

function Convert-ControlHealthToWebSocketUrl {
    param([string]$ControlHealthUrl)

    $controlUri = [Uri]$ControlHealthUrl
    $scheme = if ([string]::Equals($controlUri.Scheme, "https", [StringComparison]::OrdinalIgnoreCase)) { "wss" } else { "ws" }
    $builder = [System.UriBuilder]::new($controlUri)
    $builder.Scheme = $scheme
    if ($builder.Path.EndsWith("/health", [StringComparison]::OrdinalIgnoreCase)) {
        $builder.Path = $builder.Path.Substring(0, $builder.Path.Length - "/health".Length) + "/ws/control"
    }
    else {
        $builder.Path = "/ws/control"
    }

    return $builder.Uri.AbsoluteUri
}

function Stop-PortListeners {
    param(
        [int]$Port,
        [string]$Label
    )

    $listeners = @(Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue | Select-Object -ExpandProperty OwningProcess -Unique)
    foreach ($listenerPid in $listeners) {
        if (-not $listenerPid -or $listenerPid -le 0) {
            continue
        }

        try {
            Stop-Process -Id $listenerPid -Force -ErrorAction Stop
            Write-Host "Preflight cleanup ($Label): stopped listener PID $listenerPid on port $Port."
        }
        catch {
            throw "Preflight cleanup ($Label) failed to stop PID $listenerPid on port $Port."
        }
    }
}

function Set-TemporaryProcessEnvironment {
    param(
        [hashtable]$Environment
    )

    $previous = @{}
    foreach ($key in $Environment.Keys) {
        $previous[$key] = [System.Environment]::GetEnvironmentVariable($key, [System.EnvironmentVariableTarget]::Process)
        [System.Environment]::SetEnvironmentVariable($key, [string]$Environment[$key], [System.EnvironmentVariableTarget]::Process)
    }

    return $previous
}

function Restore-ProcessEnvironment {
    param(
        [hashtable]$Previous
    )

    foreach ($key in $Previous.Keys) {
        [System.Environment]::SetEnvironmentVariable($key, $Previous[$key], [System.EnvironmentVariableTarget]::Process)
    }
}

function Start-DemoService {
    param(
        [string]$Name,
        [string]$ProjectPath,
        [string]$BaseUrl,
        [string]$TargetHost,
        [int]$Port,
        [hashtable]$Environment,
        [string]$Category,
        [switch]$UseHealthProbe,
        [switch]$ReuseRunningService
    )

    Write-Host ""
    Write-Host "=== $Name ==="
    $stdoutLog = Join-Path $logRoot "$Category.stdout.log"
    $stderrLog = Join-Path $logRoot "$Category.stderr.log"
    $sw = [System.Diagnostics.Stopwatch]::StartNew()

    if (Test-TcpPort -TargetHost $TargetHost -TargetPort $Port) {
        if ($ReuseRunningService) {
            if ($UseHealthProbe -and -not (Test-ApiHealth -BaseUrl $BaseUrl)) {
                throw "$Name reuse requested but health probe failed at $BaseUrl/health. Ensure runtime is running and healthy, or run without -ReuseRunningServices."
            }

            $sw.Stop()
            Add-DemoResult -Name $Name -Status "PASS" -Seconds $sw.Elapsed.TotalSeconds -ExitCode 0 -LogPath $stdoutLog
            Write-Host "PASS  $Name"
            Write-Host "URL:   $BaseUrl (already running)"
            return [pscustomobject]@{
                Name           = $Name
                BaseUrl        = $BaseUrl
                Started        = $false
                AlreadyRunning = $true
                Process        = $null
                StdoutLog      = $stdoutLog
                StderrLog      = $stderrLog
            }
        }

        $listeners = @(Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue | Select-Object -ExpandProperty OwningProcess -Unique)
        $failedToStop = [System.Collections.Generic.List[int]]::new()
        foreach ($listenerPid in $listeners) {
            if ($listenerPid -and $listenerPid -gt 0) {
                try {
                    Stop-Process -Id $listenerPid -Force -ErrorAction Stop
                    Write-Host "Restarting $($Name): stopped existing listener PID $listenerPid on port $Port."
                }
                catch {
                    $failedToStop.Add([int]$listenerPid) | Out-Null
                    Write-Host "Restarting $($Name): failed to stop PID $listenerPid on port $Port. Will probe for safe reuse." -ForegroundColor Yellow
                }
            }
        }

        Start-Sleep -Milliseconds 500
        if (Test-TcpPort -TargetHost $TargetHost -TargetPort $Port) {
            if ($UseHealthProbe -and (Test-ApiHealth -BaseUrl $BaseUrl)) {
                $sw.Stop()
                Add-DemoResult -Name $Name -Status "PASS" -Seconds $sw.Elapsed.TotalSeconds -ExitCode 0 -LogPath $stdoutLog
                Write-Host "PASS  $Name"
                if ($failedToStop.Count -gt 0) {
                    Write-Host "URL:   $BaseUrl (reused healthy listener; could not stop PID(s): $($failedToStop -join ', '))"
                }
                else {
                    Write-Host "URL:   $BaseUrl (already running)"
                }

                return [pscustomobject]@{
                    Name           = $Name
                    BaseUrl        = $BaseUrl
                    Started        = $false
                    AlreadyRunning = $true
                    Process        = $null
                    StdoutLog      = $stdoutLog
                    StderrLog      = $stderrLog
                }
            }

            throw "$Name could not claim port $Port because another process is still listening."
        }
    }

    New-Item -ItemType File -Force -Path $stdoutLog | Out-Null
    New-Item -ItemType File -Force -Path $stderrLog | Out-Null

    $restore = Set-TemporaryProcessEnvironment -Environment $Environment
    try {
        $arguments = @("run", "--project", $ProjectPath, "-c", $Configuration, "--no-launch-profile")
        if ($NoBuild) {
            $arguments += "--no-build"
        }

        $startParams = @{
            FilePath               = "dotnet"
            ArgumentList           = $arguments
            WorkingDirectory       = $repoRoot
            PassThru               = $true
            RedirectStandardOutput = $stdoutLog
            RedirectStandardError  = $stderrLog
        }
        if (-not $ShowServiceWindows) {
            $startParams.WindowStyle = "Hidden"
        }

        $process = Start-Process @startParams
    }
    finally {
        Restore-ProcessEnvironment -Previous $restore
    }

    $started = $false
    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        Start-Sleep -Milliseconds 500
        if ($UseHealthProbe) {
            if (Test-ApiHealth -BaseUrl $BaseUrl) {
                $started = $true
                break
            }
        }
        elseif (Test-TcpPort -TargetHost $TargetHost -TargetPort $Port) {
            $started = $true
            break
        }

        if ($process.HasExited) {
            break
        }
    }

    $sw.Stop()
    if ($started) {
        Add-DemoResult -Name $Name -Status "PASS" -Seconds $sw.Elapsed.TotalSeconds -ExitCode 0 -LogPath $stdoutLog
        Write-Host "PASS  $Name"
        Write-Host "URL:   $BaseUrl"
        Write-Host "PID:   $($process.Id)"
        Write-Host "Out:   $stdoutLog"
        Write-Host "Err:   $stderrLog"
        return [pscustomobject]@{
            Name           = $Name
            BaseUrl        = $BaseUrl
            Started        = $true
            AlreadyRunning = $false
            Process        = $process
            StdoutLog      = $stdoutLog
            StderrLog      = $stderrLog
        }
    }

    if (-not $process.HasExited) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
    }

    Add-DemoResult -Name $Name -Status "FAIL" -Seconds $sw.Elapsed.TotalSeconds -ExitCode 1 -LogPath $stderrLog
    Write-Host "FAIL  $Name" -ForegroundColor Red
    Write-Host "Out:   $stdoutLog"
    Write-Host "Err:   $stderrLog"
    throw "$Name failed to start. See logs: $stdoutLog / $stderrLog"
}

if ($IncludeWebRtcEdgeAgentSmoke) {
    if (-not $effectiveReuseRunningServices) {
        if (Test-ApiHealth -BaseUrl $ApiBaseUrl) {
            Write-Warning "IncludeWebRtcEdgeAgentSmoke enabled: forcing service reuse to avoid restarting live WebRTC relay sessions."
            $effectiveReuseRunningServices = $true
        }
        else {
            Write-Warning "IncludeWebRtcEdgeAgentSmoke enabled but API is not healthy at $($ApiBaseUrl.TrimEnd('/'))/health. Starting runtime services normally (no reuse)."
        }
    }

    if (-not $effectiveSkipCiLocal) {
        Write-Warning "IncludeWebRtcEdgeAgentSmoke enabled: skipping CI Local to avoid replacing the active API runtime during WebRTC relay validation."
        $effectiveSkipCiLocal = $true
    }
}

try {
    if (-not $effectiveReuseRunningServices) {
        Stop-PortListeners -Port $apiUri.Port -Label "API"
        Stop-PortListeners -Port $webUri.Port -Label "Web"
    }

    $skipDoctorInCi = $false
    if (-not $SkipIdentityReset) {
        Invoke-LoggedScript -Results $results -Name "Identity Reset" -LogRoot $logRoot -ScriptPath $identityResetScript -Parameters @{} -StopOnFailure
    }

    if (-not $SkipDoctor) {
        $doctorRequireApi = -not $IncludeWebRtcEdgeAgentSmoke
        $doctorStartApiProbe = -not $IncludeWebRtcEdgeAgentSmoke
        Invoke-LoggedScript -Results $results -Name "Doctor" -LogRoot $logRoot -ScriptPath $doctorScript -Parameters @{
            StartApiProbe = $doctorStartApiProbe
            RequireApi = $doctorRequireApi
            ApiConfiguration = $Configuration
            ApiPersistenceProvider = "Sql"
            ApiConnectionString = $ConnectionString
            ApiSchema = $Schema
            KeycloakBaseUrl = ($AuthAuthority -replace '/realms/.*$', '')
        } -StopOnFailure
        $skipDoctorInCi = $true
    }

    if (-not $effectiveSkipCiLocal) {
        Invoke-LoggedScript -Results $results -Name "CI Local" -LogRoot $logRoot -ScriptPath $ciLocalScript -Parameters @{
            Configuration = $Configuration
            ConnectionString = $ConnectionString
            Schema = $Schema
            AuthAuthority = $AuthAuthority
            SkipDoctor = $skipDoctorInCi
        } -StopOnFailure
    }

    if ($IncludeFull) {
        Invoke-LoggedScript -Results $results -Name "Full" -LogRoot $logRoot -ScriptPath $fullScript -Parameters @{
            Configuration = $Configuration
            ConnectionString = $ConnectionString
            Schema = $Schema
            AuthAuthority = $AuthAuthority
        } -StopOnFailure
    }

    if (-not $SkipServiceStartup) {
        $serviceStartupAttempted = $true
        $apiEnvironment = @{
            ASPNETCORE_ENVIRONMENT = "Development"
            ASPNETCORE_URLS = $ApiBaseUrl
            HIDBRIDGE_PERSISTENCE_PROVIDER = "Sql"
            HIDBRIDGE_SQL_CONNECTION = $ConnectionString
            HIDBRIDGE_SQL_SCHEMA = $Schema
            HIDBRIDGE_SQL_APPLY_MIGRATIONS = "true"
            HIDBRIDGE_AUTH_ENABLED = "true"
            HIDBRIDGE_AUTH_AUTHORITY = $AuthAuthority
            HIDBRIDGE_AUTH_AUDIENCE = ""
            HIDBRIDGE_AUTH_REQUIRE_HTTPS_METADATA = "false"
            HIDBRIDGE_AUTH_BEARER_ONLY_PREFIXES = ""
            HIDBRIDGE_AUTH_CALLER_CONTEXT_REQUIRED_PREFIXES = ""
            HIDBRIDGE_AUTH_HEADER_FALLBACK_DISABLED_PATTERNS = ""
        }
        if ($IncludeWebRtcEdgeAgentSmoke) {
            # Keep API from holding the UART COM port while WebRTC edge stack is active.
            $apiEnvironment["HIDBRIDGE_UART_PASSIVE_HEALTH_MODE"] = "true"
            $apiEnvironment["HIDBRIDGE_UART_RELEASE_PORT_AFTER_EXECUTE"] = "true"
            $apiEnvironment["HIDBRIDGE_UART_PORT"] = "COM255"
            $apiEnvironment["HIDBRIDGE_TRANSPORT_PROVIDER"] = "webrtc-datachannel"
            $apiEnvironment["HIDBRIDGE_TRANSPORT_FALLBACK_TO_DEFAULT_ON_WEBRTC_ERROR"] = "false"
        }

        $apiRuntime = Start-DemoService -Name "Start API" -ProjectPath $apiProjectPath -BaseUrl $ApiBaseUrl -TargetHost $apiUri.Host -Port $apiUri.Port -Category "api-runtime" -UseHealthProbe -ReuseRunningService:$effectiveReuseRunningServices -Environment $apiEnvironment
        $runtimeServices.Add($apiRuntime) | Out-Null
        if ($apiRuntime.Process -ne $null) {
            $runtimeProcesses.Add($apiRuntime) | Out-Null
        }

        Invoke-LoggedScript -Results $results -Name "Demo Seed" -LogRoot $logRoot -ScriptPath $demoSeedScript -Parameters @{
            BaseUrl = $ApiBaseUrl
            KeycloakBaseUrl = $keycloakBaseUrl
            RealmName = $realmName
        } -StopOnFailure

        $shouldBootstrapWebRtcStack = $false
        if ($IncludeWebRtcEdgeAgentSmoke) {
            if (-not $effectiveReuseRunningServices) {
                $shouldBootstrapWebRtcStack = $true
            }
            elseif (-not (Test-ControlHealth -Url $WebRtcControlHealthUrl -Attempts ([Math]::Max(1, $WebRtcControlHealthAttempts)) -DelayMs 500)) {
                $shouldBootstrapWebRtcStack = $true
            }
        }

        if ($shouldBootstrapWebRtcStack) {
            if (-not (Test-Path $webrtcStackScript)) {
                throw "WebRTC stack bootstrap script was not found: $webrtcStackScript"
            }

            Write-Warning "Bootstrapping WebRTC edge stack automatically for demo-flow WebRTC gate."
            $controlWsUrl = Convert-ControlHealthToWebSocketUrl -ControlHealthUrl $WebRtcControlHealthUrl
            Invoke-LoggedScript -Results $results -Name "WebRTC Stack Bootstrap" -LogRoot $logRoot -ScriptPath $webrtcStackScript -Parameters @{
                ApiBaseUrl = $ApiBaseUrl
                WebBaseUrl = $WebBaseUrl
                ControlWsUrl = $controlWsUrl
                SkipRuntimeBootstrap = $true
                SkipIdentityReset = $true
                SkipCiLocal = $true
                StopExisting = $true
            } -StopOnFailure
        }

        if ($IncludeWebRtcEdgeAgentSmoke -and -not (Test-ControlHealth -Url $WebRtcControlHealthUrl -Attempts ([Math]::Max(1, $WebRtcControlHealthAttempts)) -DelayMs 500)) {
            throw "WebRTC control health endpoint is still not ready after bootstrap: $WebRtcControlHealthUrl"
        }

        if (-not $effectiveSkipDemoGate) {
            $demoGateParameters = @{
                BaseUrl = $ApiBaseUrl
                KeycloakBaseUrl = $keycloakBaseUrl
                RealmName = $realmName
                OutputJsonPath = (Join-Path $logRoot "demo-gate.result.json")
            }
            if ($IncludeWebRtcEdgeAgentSmoke) {
                $demoGateParameters["TransportProvider"] = "webrtc-datachannel"
                $demoGateParameters["ControlHealthUrl"] = $WebRtcControlHealthUrl
                $demoGateParameters["RequestTimeoutSec"] = [Math]::Max(1, $WebRtcRequestTimeoutSec)
                $demoGateParameters["ControlHealthAttempts"] = [Math]::Max(1, $WebRtcControlHealthAttempts)
                $demoGateParameters["PrincipalId"] = "smoke-runner"
            }
            if ($RequireDemoGateDeviceAck) {
                $demoGateParameters["RequireDeviceAck"] = $true
                if ($DemoGateKeyboardInterfaceSelector -ge 0 -and $DemoGateKeyboardInterfaceSelector -le 255) {
                    $demoGateParameters["KeyboardInterfaceSelector"] = $DemoGateKeyboardInterfaceSelector
                }
            }

            Invoke-LoggedScript -Results $results -Name "Demo Gate" -LogRoot $logRoot -ScriptPath $demoGateScript -Parameters $demoGateParameters -StopOnFailure
        }

        if ($IncludeWebRtcEdgeAgentSmoke) {
            if ($effectiveSkipDemoGate) {
                Invoke-LoggedScript -Results $results -Name "WebRTC Edge Agent Smoke" -LogRoot $logRoot -ScriptPath $webrtcEdgeAgentSmokeScript -Parameters @{
                    ApiBaseUrl = $ApiBaseUrl
                    ControlHealthUrl = $WebRtcControlHealthUrl
                    RequestTimeoutSec = [Math]::Max(1, $WebRtcRequestTimeoutSec)
                    ControlHealthAttempts = [Math]::Max(1, $WebRtcControlHealthAttempts)
                    OutputJsonPath = (Join-Path $logRoot "webrtc-edge-agent-smoke.result.json")
                } -StopOnFailure
            }
            else {
                Write-Warning "Skipping standalone WebRTC Edge Agent Smoke step because Demo Gate already executed WebRTC transport validation in this run."
            }
        }

        $webEnvironment = @{
            ASPNETCORE_ENVIRONMENT = "Development"
            ASPNETCORE_URLS = $WebBaseUrl
            ControlPlaneApi__BaseUrl = $ApiBaseUrl
            ControlPlaneApi__PropagateAccessToken = "true"
            ControlPlaneApi__PropagateIdentityHeaders = "true"
            Identity__Enabled = "true"
            Identity__ProviderDisplayName = "Keycloak"
            Identity__Authority = $AuthAuthority
            Identity__ClientId = "controlplane-web"
            Identity__ClientSecret = "hidbridge-web-dev-secret"
            Identity__RequireHttpsMetadata = "false"
            Identity__DisablePushedAuthorization = "true"
            Identity__PrincipalClaimType = "principal_id"
            Identity__TenantClaimType = "tenant_id"
            Identity__OrganizationClaimType = "org_id"
            Identity__RoleClaimType = "role"
        }
        if ($EnableCallerContextScope) {
            $webEnvironment["Identity__Scopes__0"] = "hidbridge-caller-context-v2"
        }

        $webRuntime = Start-DemoService -Name "Start Web" -ProjectPath $webProjectPath -BaseUrl $WebBaseUrl -TargetHost $webUri.Host -Port $webUri.Port -Category "web-runtime" -ReuseRunningService:$effectiveReuseRunningServices -Environment $webEnvironment
        $runtimeServices.Add($webRuntime) | Out-Null
        if ($webRuntime.Process -ne $null) {
            $runtimeProcesses.Add($webRuntime) | Out-Null
        }
    }
}
catch {
    $abortLogPath = Join-Path $logRoot "demo-flow.abort.log"
    Add-Content -Path $abortLogPath -Value $_.Exception.ToString()
    Add-DemoResult -Name "Demo flow orchestration" -Status "FAIL" -Seconds 0 -ExitCode 1 -LogPath $abortLogPath
    Write-Host ""
    Write-Host "Demo flow aborted: $($_.Exception.Message)" -ForegroundColor Red
}

Write-OperationalSummary -Results $results -LogRoot $logRoot -Header "Demo Flow Summary"

if ($runtimeServices.Count -gt 0) {
    Write-Host ""
    Write-Host "Demo endpoints:"
    foreach ($runtime in $runtimeServices) {
        Write-Host ("- {0}: {1}" -f $runtime.Name, $runtime.BaseUrl)
    }

    if ($runtimeProcesses.Count -gt 0) {
        Write-Host ""
        Write-Host "Stop commands:"
        foreach ($runtime in $runtimeProcesses) {
            Write-Host ("- Stop-Process -Id {0}" -f $runtime.Process.Id)
        }
    }
}
elseif ($serviceStartupAttempted) {
    Write-Host ""
    Write-Host "Runtime services were already running; no new processes were started."
}

if ($results | Where-Object { $_.Status -eq "FAIL" }) { exit 1 }
exit 0

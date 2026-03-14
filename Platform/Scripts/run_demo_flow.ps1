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
    [int]$DemoGateKeyboardInterfaceSelector = -1
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

try {
    if (-not $ReuseRunningServices) {
        Stop-PortListeners -Port $apiUri.Port -Label "API"
        Stop-PortListeners -Port $webUri.Port -Label "Web"
    }

    $skipDoctorInCi = $false
    if (-not $SkipIdentityReset) {
        Invoke-LoggedScript -Results $results -Name "Identity Reset" -LogRoot $logRoot -ScriptPath $identityResetScript -Parameters @{} -StopOnFailure
    }

    if (-not $SkipDoctor) {
        Invoke-LoggedScript -Results $results -Name "Doctor" -LogRoot $logRoot -ScriptPath $doctorScript -Parameters @{
            StartApiProbe = $true
            RequireApi = $true
            ApiConfiguration = $Configuration
            ApiPersistenceProvider = "Sql"
            ApiConnectionString = $ConnectionString
            ApiSchema = $Schema
            KeycloakBaseUrl = ($AuthAuthority -replace '/realms/.*$', '')
        } -StopOnFailure
        $skipDoctorInCi = $true
    }

    if (-not $SkipCiLocal) {
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
        $apiRuntime = Start-DemoService -Name "Start API" -ProjectPath $apiProjectPath -BaseUrl $ApiBaseUrl -TargetHost $apiUri.Host -Port $apiUri.Port -Category "api-runtime" -UseHealthProbe -ReuseRunningService:$ReuseRunningServices -Environment @{
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
        $runtimeServices.Add($apiRuntime) | Out-Null
        if ($apiRuntime.Process -ne $null) {
            $runtimeProcesses.Add($apiRuntime) | Out-Null
        }

        Invoke-LoggedScript -Results $results -Name "Demo Seed" -LogRoot $logRoot -ScriptPath $demoSeedScript -Parameters @{
            BaseUrl = $ApiBaseUrl
            KeycloakBaseUrl = $keycloakBaseUrl
            RealmName = $realmName
        } -StopOnFailure

        if (-not $SkipDemoGate) {
            $demoGateParameters = @{
                BaseUrl = $ApiBaseUrl
                KeycloakBaseUrl = $keycloakBaseUrl
                RealmName = $realmName
                OutputJsonPath = (Join-Path $logRoot "demo-gate.result.json")
            }
            if ($RequireDemoGateDeviceAck) {
                $demoGateParameters["RequireDeviceAck"] = $true
                if ($DemoGateKeyboardInterfaceSelector -ge 0 -and $DemoGateKeyboardInterfaceSelector -le 255) {
                    $demoGateParameters["KeyboardInterfaceSelector"] = $DemoGateKeyboardInterfaceSelector
                }
            }

            Invoke-LoggedScript -Results $results -Name "Demo Gate" -LogRoot $logRoot -ScriptPath $demoGateScript -Parameters $demoGateParameters -StopOnFailure
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

        $webRuntime = Start-DemoService -Name "Start Web" -ProjectPath $webProjectPath -BaseUrl $WebBaseUrl -TargetHost $webUri.Host -Port $webUri.Port -Category "web-runtime" -ReuseRunningService:$ReuseRunningServices -Environment $webEnvironment
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

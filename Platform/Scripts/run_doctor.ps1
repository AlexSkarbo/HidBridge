param(
    [string]$KeycloakBaseUrl = "http://127.0.0.1:18096",
    [string]$Realm = "hidbridge-dev",
    [string]$AdminUser = "admin",
    [string]$AdminPassword = "1q-p2w0o",
    [string]$PostgresHost = "127.0.0.1",
    [int]$PostgresPort = 5434,
    [string]$ApiBaseUrl = "http://127.0.0.1:18093",
    [switch]$RequireApi,
    [switch]$StartApiProbe,
    [string]$ApiConfiguration = "Debug",
    [ValidateSet("File", "Sql")]
    [string]$ApiPersistenceProvider = "Sql",
    [string]$ApiConnectionString = "Host=127.0.0.1;Port=5434;Database=hidbridge;Username=hidbridge;Password=hidbridge",
    [string]$ApiSchema = "hidbridge",
    [string]$SmokeClientId = "controlplane-smoke",
    [string]$SmokeAdminUser = "operator.smoke.admin",
    [string]$SmokeAdminPassword = "ChangeMe123!",
    [string]$SmokeViewerUser = "operator.smoke.viewer",
    [string]$SmokeViewerPassword = "ChangeMe123!",
    [string]$SmokeForeignUser = "operator.smoke.foreign",
    [string]$SmokeForeignPassword = "ChangeMe123!"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$scriptsRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$platformRoot = Split-Path -Parent $scriptsRoot
$repoRoot = Split-Path -Parent $platformRoot
. (Join-Path $scriptsRoot "Common/ScriptCommon.ps1")
. (Join-Path $scriptsRoot "Common/KeycloakCommon.ps1")
$logRoot = New-OperationalLogRoot -PlatformRoot $platformRoot -Category "doctor"
$results = New-OperationalResultList
$logPath = Join-Path $logRoot "doctor.log"
$apiProbeStdout = Join-Path $logRoot "api-probe.stdout.log"
$apiProbeStderr = Join-Path $logRoot "api-probe.stderr.log"
$apiProbeProcess = $null
$apiProjectPath = Join-Path $repoRoot "Platform/Platform/HidBridge.ControlPlane.Api/HidBridge.ControlPlane.Api.csproj"

function Add-DoctorResult {
    param(
        [string]$Name,
        [ValidateSet("PASS", "WARN", "FAIL")]
        [string]$Status,
        [string]$Message
    )

    $exitCode = if ($Status -eq "FAIL") { 1 } else { 0 }
    Add-OperationalResult -Results $results -Name $Name -Status $Status -Seconds 0 -ExitCode $exitCode -LogPath $logPath
    Add-Content -Path $logPath -Value (("[{0}] {1}: {2}" -f $Status, $Name, $Message))
}

function Test-ApiHealth {
    param([string]$BaseUrl)

    try {
        $health = Invoke-RestMethod -Method Get -Uri "$($BaseUrl.TrimEnd('/'))/health" -TimeoutSec 10
        return ($null -ne $health -and $health.status -eq "ok")
    }
    catch {
        return $false
    }
}

function ConvertFrom-Base64UrlJson {
    param([string]$Segment)

    $padded = $Segment.Replace('-', '+').Replace('_', '/')
    switch ($padded.Length % 4) {
        2 { $padded += "==" }
        3 { $padded += "=" }
    }

    $bytes = [Convert]::FromBase64String($padded)
    $json = [System.Text.Encoding]::UTF8.GetString($bytes)
    return $json | ConvertFrom-Json
}

function Test-CallerContextReady {
    param([string]$AccessToken)

    if ([string]::IsNullOrWhiteSpace($AccessToken)) {
        return $false
    }

    $parts = @($AccessToken -split '\.')
    if ($parts.Count -lt 2) {
        return $false
    }

    $payload = ConvertFrom-Base64UrlJson -Segment $parts[1]
    $roleClaim = $payload.PSObject.Properties["role"]
    $roles = if ($null -ne $roleClaim) { @($roleClaim.Value) } else { @() }
    $operatorRoles = @($roles | Where-Object { $_ -like "operator.*" })

    return `
        $null -ne $payload.PSObject.Properties["preferred_username"] -and `
        -not [string]::IsNullOrWhiteSpace([string]$payload.preferred_username) -and `
        $null -ne $payload.PSObject.Properties["tenant_id"] -and `
        -not [string]::IsNullOrWhiteSpace([string]$payload.tenant_id) -and `
        $null -ne $payload.PSObject.Properties["org_id"] -and `
        -not [string]::IsNullOrWhiteSpace([string]$payload.org_id) -and `
        $operatorRoles.Count -gt 0
}

New-Item -ItemType File -Force -Path $logPath | Out-Null

try {
    Add-DoctorResult -Name "dotnet" -Status ($(if (Get-Command dotnet -ErrorAction SilentlyContinue) { "PASS" } else { "FAIL" })) -Message "dotnet availability check"
    $keycloakPortOk = Test-TcpPort -TargetHost "127.0.0.1" -TargetPort 18096
    Add-DoctorResult -Name "keycloak-port" -Status ($(if ($keycloakPortOk) { "PASS" } else { "FAIL" })) -Message "$KeycloakBaseUrl reachable"
    $postgresPortOk = Test-TcpPort -TargetHost $PostgresHost -TargetPort $PostgresPort
    Add-DoctorResult -Name "postgres-port" -Status ($(if ($postgresPortOk) { "PASS" } else { "FAIL" })) -Message "${PostgresHost}:$PostgresPort reachable"

    $apiPortOk = Test-TcpPort -TargetHost "127.0.0.1" -TargetPort 18093
    if (-not $apiPortOk -and $StartApiProbe) {
        $env:HIDBRIDGE_PERSISTENCE_PROVIDER = $ApiPersistenceProvider
        $env:HIDBRIDGE_SQL_CONNECTION = $ApiConnectionString
        $env:HIDBRIDGE_SQL_SCHEMA = $ApiSchema
        $env:HIDBRIDGE_SQL_APPLY_MIGRATIONS = "true"
        $env:HIDBRIDGE_AUTH_ENABLED = "false"

        $apiProbeProcess = Start-Process -FilePath "dotnet" -ArgumentList @("run", "--project", $apiProjectPath, "-c", $ApiConfiguration, "--no-build", "--no-launch-profile") -WorkingDirectory $repoRoot -PassThru -RedirectStandardOutput $apiProbeStdout -RedirectStandardError $apiProbeStderr
        for ($attempt = 0; $attempt -lt 20; $attempt++) {
            Start-Sleep -Milliseconds 500
            if (Test-ApiHealth -BaseUrl $ApiBaseUrl) {
                $apiPortOk = $true
                break
            }
            if ($apiProbeProcess.HasExited) {
                break
            }
        }
    }

    $apiStatus = if ($apiPortOk) { "PASS" } elseif ($RequireApi) { "FAIL" } else { "WARN" }
    $apiMessage = if ($apiPortOk) { "$ApiBaseUrl reachable" } elseif ($StartApiProbe) { "$ApiBaseUrl not reachable after probe start attempt" } else { "$ApiBaseUrl not reachable; API is not running" }
    Add-DoctorResult -Name "api-port" -Status $apiStatus -Message $apiMessage

    Add-DoctorResult -Name "realm-sync-script" -Status ($(if (Test-Path (Join-Path $platformRoot "Identity/Keycloak/Sync-HidBridgeDevRealm.ps1")) { "PASS" } else { "FAIL" })) -Message "realm sync script present"
    Add-DoctorResult -Name "realm-import" -Status ($(if (Test-Path (Join-Path $platformRoot "Identity/Keycloak/realm-import/hidbridge-dev-realm.json")) { "PASS" } else { "FAIL" })) -Message "realm import present"
    Add-DoctorResult -Name "smoke-client-wrapper" -Status ($(if (Test-Path (Join-Path $platformRoot "run_api_bearer_smoke.ps1")) { "PASS" } else { "FAIL" })) -Message "bearer smoke wrapper present"

    if ($keycloakPortOk) {
        $adminToken = Get-KeycloakAdminToken -BaseUrl $KeycloakBaseUrl -AdminRealm "master" -AdminUser $AdminUser -AdminPassword $AdminPassword
        Add-DoctorResult -Name "keycloak-admin-auth" -Status ($(if (-not [string]::IsNullOrWhiteSpace($adminToken)) { "PASS" } else { "FAIL" })) -Message "admin-cli token acquisition"

        if (-not [string]::IsNullOrWhiteSpace($adminToken)) {
            $realmResponse = Invoke-KeycloakAdminJson -BaseUrl $KeycloakBaseUrl -AccessToken $adminToken -Method Get -Path "/admin/realms/$Realm"
            Add-DoctorResult -Name "realm-exists" -Status ($(if ($null -ne $realmResponse -and $realmResponse.realm -eq $Realm) { "PASS" } else { "FAIL" })) -Message "realm $Realm lookup"

            try {
                $smokeAdminToken = Get-KeycloakOidcToken -BaseUrl $KeycloakBaseUrl -RealmName $Realm -ClientId $SmokeClientId -Username $SmokeAdminUser -Password $SmokeAdminPassword -Scope "openid profile email"
                $smokeClientStatus = if (-not [string]::IsNullOrWhiteSpace($smokeAdminToken.access_token)) { "PASS" } else { "FAIL" }
                Add-DoctorResult -Name "smoke-client" -Status $smokeClientStatus -Message "$SmokeClientId direct-grant token acquisition"
                Add-DoctorResult -Name ("user-{0}" -f $SmokeAdminUser) -Status $smokeClientStatus -Message "direct-grant token acquisition for $SmokeAdminUser"
                $claimsReady = Test-CallerContextReady -AccessToken $smokeAdminToken.access_token
                Add-DoctorResult -Name "smoke-claims" -Status ($(if ($claimsReady) { "PASS" } else { "WARN" })) -Message "caller-context claims present in smoke access token"
            }
            catch {
                Add-DoctorResult -Name "smoke-client" -Status "FAIL" -Message "$SmokeClientId direct-grant token acquisition failed"
                Add-DoctorResult -Name ("user-{0}" -f $SmokeAdminUser) -Status "FAIL" -Message $_.Exception.Message
                Add-DoctorResult -Name "smoke-claims" -Status "FAIL" -Message "caller-context claims could not be inspected"
            }

            foreach ($smokeUser in @(
                @{ User = $SmokeViewerUser; Password = $SmokeViewerPassword },
                @{ User = $SmokeForeignUser; Password = $SmokeForeignPassword }
            )) {
                try {
                    $tokenResponse = Get-KeycloakOidcToken -BaseUrl $KeycloakBaseUrl -RealmName $Realm -ClientId $SmokeClientId -Username $smokeUser.User -Password $smokeUser.Password -Scope "openid profile email"
                    $status = if (-not [string]::IsNullOrWhiteSpace($tokenResponse.access_token)) { "PASS" } else { "FAIL" }
                    Add-DoctorResult -Name ("user-{0}" -f $smokeUser.User) -Status $status -Message "direct-grant token acquisition for $($smokeUser.User)"
                }
                catch {
                    Add-DoctorResult -Name ("user-{0}" -f $smokeUser.User) -Status "FAIL" -Message $_.Exception.Message
                }
            }
        }
    }
}
catch {
    Add-Content -Path $logPath -Value $_.Exception.ToString()
    throw
}
finally {
    if ($null -ne $apiProbeProcess -and -not $apiProbeProcess.HasExited) {
        Stop-Process -Id $apiProbeProcess.Id -Force -ErrorAction SilentlyContinue
    }
}

Write-OperationalSummary -Results $results -LogRoot $logRoot -Header "Doctor Summary"
if ($results | Where-Object { $_.Status -eq "FAIL" }) { exit 1 }
exit 0

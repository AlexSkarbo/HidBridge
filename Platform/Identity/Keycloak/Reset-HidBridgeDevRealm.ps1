param(
    [string]$KeycloakBaseUrl = "http://127.0.0.1:18096",
    [string]$AdminRealm = "master",
    [string]$AdminUser = "",
    [string]$AdminPassword = "",
    [string]$RealmName = "hidbridge-dev",
    [string]$ImportPath = "",
    [string]$BackupRoot = "",
    [switch]$SkipVerification,
    [string[]]$ExternalProviderConfigPaths = @(),
    [switch]$AllowIdentityProviderLoss
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$platformRoot = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))
$platformDir = Join-Path $platformRoot "Platform"
. (Join-Path $platformDir "Scripts/Common/ScriptCommon.ps1")
. (Join-Path $platformDir "Scripts/Common/KeycloakCommon.ps1")

if ([string]::IsNullOrWhiteSpace($AdminUser)) {
    $AdminUser = if (-not [string]::IsNullOrWhiteSpace($env:HIDBRIDGE_KEYCLOAK_ADMIN_USER)) {
        $env:HIDBRIDGE_KEYCLOAK_ADMIN_USER
    }
    elseif (-not [string]::IsNullOrWhiteSpace($env:KEYCLOAK_ADMIN)) {
        $env:KEYCLOAK_ADMIN
    }
    else {
        "admin"
    }
}

if ([string]::IsNullOrWhiteSpace($AdminPassword)) {
    $AdminPassword = if (-not [string]::IsNullOrWhiteSpace($env:HIDBRIDGE_KEYCLOAK_ADMIN_PASSWORD)) {
        $env:HIDBRIDGE_KEYCLOAK_ADMIN_PASSWORD
    }
    elseif (-not [string]::IsNullOrWhiteSpace($env:KEYCLOAK_ADMIN_PASSWORD)) {
        $env:KEYCLOAK_ADMIN_PASSWORD
    }
    else {
        "1q-p2w0o"
    }
}

if ([string]::IsNullOrWhiteSpace($ImportPath)) {
    $ImportPath = Join-Path $PSScriptRoot "realm-import/hidbridge-dev-realm.json"
}

if ([string]::IsNullOrWhiteSpace($BackupRoot)) {
    $BackupRoot = Join-Path $PSScriptRoot "backups"
}

if (-not (Test-Path $ImportPath)) {
    throw "Realm import file not found: $ImportPath"
}

New-Item -ItemType Directory -Force -Path $BackupRoot | Out-Null

$runStamp = [DateTimeOffset]::UtcNow.ToString("yyyyMMdd-HHmmss")
$backupPath = Join-Path $BackupRoot "$RealmName-reset-backup-$runStamp.json"
$activityLogPath = Join-Path $BackupRoot "$RealmName-reset-$runStamp.log"
$activity = New-Object System.Collections.Generic.List[string]

function Write-Activity {
    param([string]$Message)

    $activity.Add($Message) | Out-Null
    Write-Host $Message
}

function Wait-RealmAvailability {
    param(
        [string]$BaseUrl,
        [string]$AccessToken,
        [string]$TargetRealm,
        [int]$TimeoutSeconds = 30
    )

    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        try {
            $realm = Invoke-KeycloakAdminJson -BaseUrl $BaseUrl -AccessToken $AccessToken -Method Get -Path "/admin/realms/$TargetRealm"
            if ($null -ne $realm) {
                return $true
            }
        }
        catch {
        }

        Start-Sleep -Milliseconds 500
    }

    return $false
}

function ConvertTo-Hashtable {
    param([object]$InputObject)

    if ($null -eq $InputObject) {
        return $null
    }

    if ($InputObject -is [string] -or $InputObject.GetType().IsValueType) {
        return $InputObject
    }

    if ($InputObject -is [pscustomobject]) {
        $result = @{}
        foreach ($property in $InputObject.PSObject.Properties) {
            $result[$property.Name] = ConvertTo-Hashtable $property.Value
        }

        return $result
    }

    if ($InputObject -is [System.Collections.IDictionary]) {
        $table = @{}
        foreach ($key in $InputObject.Keys) {
            $table[$key] = ConvertTo-Hashtable $InputObject[$key]
        }

        return $table
    }

    if ($InputObject -is [System.Collections.IEnumerable] -and -not ($InputObject -is [string])) {
        $items = @()
        foreach ($item in $InputObject) {
            $items += ,(ConvertTo-Hashtable $item)
        }

        return $items
    }

    return $InputObject
}

function Resolve-ExternalProviderConfigPaths {
    $allRawPaths = New-Object System.Collections.Generic.List[string]
    foreach ($pathCandidate in @($ExternalProviderConfigPaths)) {
        if (-not [string]::IsNullOrWhiteSpace($pathCandidate)) {
            foreach ($segment in @($pathCandidate -split ';')) {
                if (-not [string]::IsNullOrWhiteSpace($segment)) {
                    $allRawPaths.Add($segment.Trim()) | Out-Null
                }
            }
        }
    }

    if (-not [string]::IsNullOrWhiteSpace($env:HIDBRIDGE_KEYCLOAK_EXTERNAL_PROVIDER_CONFIGS)) {
        foreach ($segment in @($env:HIDBRIDGE_KEYCLOAK_EXTERNAL_PROVIDER_CONFIGS -split ';')) {
            if (-not [string]::IsNullOrWhiteSpace($segment)) {
                $allRawPaths.Add($segment.Trim()) | Out-Null
            }
        }
    }

    $defaultGoogleLocalPath = Join-Path $PSScriptRoot "providers/google-oidc.local.json"
    if (Test-Path $defaultGoogleLocalPath) {
        $allRawPaths.Add($defaultGoogleLocalPath) | Out-Null
    }

    $resolved = New-Object System.Collections.Generic.List[string]
    $seen = New-Object System.Collections.Generic.HashSet[string]
    foreach ($candidate in $allRawPaths) {
        $resolvedPath = if ([System.IO.Path]::IsPathRooted($candidate)) {
            $candidate
        }
        else {
            Join-Path $platformRoot $candidate
        }

        if (-not (Test-Path $resolvedPath)) {
            throw "External provider config file not found: $resolvedPath"
        }

        $fullPath = [System.IO.Path]::GetFullPath($resolvedPath)
        if (-not $seen.Contains($fullPath)) {
            $seen.Add($fullPath) | Out-Null
            $resolved.Add($fullPath) | Out-Null
        }
    }

    return @($resolved)
}

function Get-IdentityProviderByAlias {
    param([string]$Alias)

    try {
        return Invoke-KeycloakAdminJson `
            -BaseUrl $KeycloakBaseUrl `
            -AccessToken $script:AdminToken `
            -Method Get `
            -Path "/admin/realms/$RealmName/identity-provider/instances/$([Uri]::EscapeDataString($Alias))"
    }
    catch {
        if ($_.Exception.Message -match "\(404\)") {
            return $null
        }

        throw
    }
}

function Ensure-IdentityProviderFromConfig {
    param([string]$ConfigPath)

    $providerConfig = Get-Content $ConfigPath -Raw | ConvertFrom-Json
    if ([string]::IsNullOrWhiteSpace($providerConfig.alias)) {
        throw "Provider config '$ConfigPath' does not define alias."
    }

    if ([string]::IsNullOrWhiteSpace($providerConfig.providerId)) {
        throw "Provider config '$ConfigPath' does not define providerId."
    }

    $providerBody = ConvertTo-Hashtable $providerConfig
    if ($providerBody -is [System.Collections.IDictionary]) {
        $providerBody.Remove("internalId") | Out-Null
    }

    $alias = [string]$providerConfig.alias
    $existing = Get-IdentityProviderByAlias -Alias $alias
    if ($null -eq $existing) {
        Invoke-KeycloakAdminNoContent `
            -BaseUrl $KeycloakBaseUrl `
            -AccessToken $script:AdminToken `
            -Method Post `
            -Path "/admin/realms/$RealmName/identity-provider/instances" `
            -Body $providerBody
        Write-Activity "Created identity provider: $alias (from $ConfigPath)"
        return
    }

    Invoke-KeycloakAdminNoContent `
        -BaseUrl $KeycloakBaseUrl `
        -AccessToken $script:AdminToken `
        -Method Put `
        -Path "/admin/realms/$RealmName/identity-provider/instances/$([Uri]::EscapeDataString($alias))" `
        -Body $providerBody
    Write-Activity "Updated identity provider: $alias (from $ConfigPath)"
}

$results = New-OperationalResultList
$logRoot = New-OperationalLogRoot -PlatformRoot $platformDir -Category "identity-reset"

function Invoke-ResetStep {
    param(
        [string]$Name,
        [scriptblock]$Action
    )

    $safeName = (($Name -replace '[^A-Za-z0-9._-]', '-') -replace '-+', '-').Trim('-')
    $logPath = Join-Path $logRoot "$safeName.log"
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    try {
        & $Action *> $logPath
        $sw.Stop()
        Add-OperationalResult -Results $results -Name $Name -Status "PASS" -Seconds $sw.Elapsed.TotalSeconds -ExitCode 0 -LogPath $logPath
        Write-Host ""
        Write-Host "=== $Name ==="
        Write-Host "PASS  $Name"
        Write-Host "Log:   $logPath"
    }
    catch {
        $sw.Stop()
        Add-OperationalResult -Results $results -Name $Name -Status "FAIL" -Seconds $sw.Elapsed.TotalSeconds -ExitCode 1 -LogPath $logPath
        Write-Host ""
        Write-Host "=== $Name ==="
        Write-Host "FAIL  $Name" -ForegroundColor Red
        Write-Host "Log:   $logPath"
        throw
    }
}

$desiredRealm = Get-Content $ImportPath -Raw | ConvertFrom-Json
$resolvedExternalProviderConfigPaths = Resolve-ExternalProviderConfigPaths
$desiredIdentityProviders = @($desiredRealm.identityProviders)
$hasProviderRestoreInputs = $desiredIdentityProviders.Count -gt 0 -or $resolvedExternalProviderConfigPaths.Count -gt 0
if (-not $hasProviderRestoreInputs -and -not $AllowIdentityProviderLoss) {
    throw @"
Identity reset is blocked to prevent accidental external IdP loss.

The realm import does not define identity providers, and no external provider config files were provided.
This reset would remove Google/Microsoft/GitHub broker settings and broker-created users.

Fix options:
1) create provider config(s), e.g.:
   Platform/Identity/Keycloak/providers/google-oidc.local.json
2) run again with:
   -ExternalProviderConfigPaths \"<path-to-provider-config>\"
or set env:
   HIDBRIDGE_KEYCLOAK_EXTERNAL_PROVIDER_CONFIGS

If you intentionally want a clean realm without external IdPs, rerun with:
   -AllowIdentityProviderLoss
"@
}

Invoke-ResetStep -Name "Admin Auth" -Action {
    $script:AdminToken = Get-KeycloakAdminToken -BaseUrl $KeycloakBaseUrl -AdminRealm $AdminRealm -AdminUser $AdminUser -AdminPassword $AdminPassword
    Write-Activity "Authenticated against Keycloak admin API."
}

Invoke-ResetStep -Name "Realm Backup" -Action {
    try {
        $existingRealm = Invoke-KeycloakAdminJson -BaseUrl $KeycloakBaseUrl -AccessToken $script:AdminToken -Method Get -Path "/admin/realms/$RealmName"
        if ($null -ne $existingRealm) {
            $export = Invoke-KeycloakAdminJson -BaseUrl $KeycloakBaseUrl -AccessToken $script:AdminToken -Method Get -Path "/admin/realms/$RealmName"
            $export | ConvertTo-Json -Depth 100 | Set-Content -Path $backupPath -Encoding UTF8
            Write-Activity "Backup written: $backupPath"
        }
    }
    catch {
        Write-Activity "Realm '$RealmName' not found during backup step; continuing."
    }
}

Invoke-ResetStep -Name "Realm Delete" -Action {
    try {
        Invoke-KeycloakAdminNoContent -BaseUrl $KeycloakBaseUrl -AccessToken $script:AdminToken -Method Delete -Path "/admin/realms/$RealmName"
        Write-Activity "Deleted realm: $RealmName"
    }
    catch {
        $message = $_.Exception.Message
        if ($message -match "404" -or $message -match "Not Found") {
            Write-Activity "Realm '$RealmName' does not exist; nothing to delete."
        }
        else {
            throw
        }
    }
}

Invoke-ResetStep -Name "Realm Recreate" -Action {
    $importBody = Get-Content $ImportPath -Raw | ConvertFrom-Json
    Invoke-KeycloakAdminNoContent -BaseUrl $KeycloakBaseUrl -AccessToken $script:AdminToken -Method Post -Path "/admin/realms" -Body $importBody
    if (-not (Wait-RealmAvailability -BaseUrl $KeycloakBaseUrl -AccessToken $script:AdminToken -TargetRealm $RealmName -TimeoutSeconds 30)) {
        throw "Realm '$RealmName' did not become available after recreate."
    }

    Write-Activity "Recreated realm from import: $ImportPath"
}

if ($resolvedExternalProviderConfigPaths.Count -gt 0) {
    Invoke-ResetStep -Name "External Providers" -Action {
        foreach ($providerConfigPath in $resolvedExternalProviderConfigPaths) {
            Ensure-IdentityProviderFromConfig -ConfigPath $providerConfigPath
        }
    }
}
elseif ($AllowIdentityProviderLoss) {
    Write-Activity "External provider restore skipped by explicit AllowIdentityProviderLoss."
}

if (-not $SkipVerification) {
    Invoke-ResetStep -Name "Verify Smoke Admin Token" -Action {
        $token = Get-KeycloakOidcToken -BaseUrl $KeycloakBaseUrl -RealmName $RealmName -ClientId "controlplane-smoke" -Username "operator.smoke.admin" -Password "ChangeMe123!" -Scope "openid profile email"
        if ([string]::IsNullOrWhiteSpace($token.access_token)) {
            throw "Smoke admin token response did not contain access_token."
        }

        Write-Activity "Verified smoke admin token issuance."
    }

    Invoke-ResetStep -Name "Verify Smoke Viewer Token" -Action {
        $token = Get-KeycloakOidcToken -BaseUrl $KeycloakBaseUrl -RealmName $RealmName -ClientId "controlplane-smoke" -Username "operator.smoke.viewer" -Password "ChangeMe123!" -Scope "openid profile email"
        if ([string]::IsNullOrWhiteSpace($token.access_token)) {
            throw "Smoke viewer token response did not contain access_token."
        }

        Write-Activity "Verified smoke viewer token issuance."
    }
}

$activity | Set-Content -Path $activityLogPath -Encoding UTF8

Write-Host ""
Write-Host "=== Keycloak Realm Reset Summary ==="
Write-Host "Realm: $RealmName"
Write-Host "Import: $ImportPath"
Write-Host "Backup: $backupPath"
Write-Host "Activity log: $activityLogPath"
Write-OperationalSummary -Results $results -LogRoot $logRoot -Header "Reset Steps"

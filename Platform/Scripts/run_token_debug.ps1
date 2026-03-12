param(
    [string]$KeycloakBaseUrl = "http://127.0.0.1:18096",
    [string]$Realm = "hidbridge-dev",
    [string]$ClientId = "controlplane-smoke",
    [string]$ClientSecret = "",
    [string]$Username = "operator.smoke.admin",
    [string]$Password = "ChangeMe123!",
    [string]$Scope = "openid profile email",
    [string]$AccessToken = "auto"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$scriptsRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$platformRoot = Split-Path -Parent $scriptsRoot
. (Join-Path $scriptsRoot "Common/ScriptCommon.ps1")
. (Join-Path $scriptsRoot "Common/KeycloakCommon.ps1")

$logRoot = New-OperationalLogRoot -PlatformRoot $platformRoot -Category "token-debug"
$summaryPath = Join-Path $logRoot "token.summary.txt"
$headerPath = Join-Path $logRoot "token.header.json"
$payloadPath = Join-Path $logRoot "token.payload.json"
$rawPath = Join-Path $logRoot "token.raw.txt"
$rawResponsePath = Join-Path $logRoot "token.response.json"
$idTokenHeaderPath = Join-Path $logRoot "id_token.header.json"
$idTokenPayloadPath = Join-Path $logRoot "id_token.payload.json"
$idTokenRawPath = Join-Path $logRoot "id_token.raw.txt"
$userinfoPath = Join-Path $logRoot "userinfo.json"

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

function Get-ClaimValue {
    param(
        [object]$Object,
        [string]$Name
    )

    if ($null -eq $Object) {
        return $null
    }

    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) {
        return $null
    }

    return $property.Value
}

if ([string]::IsNullOrWhiteSpace($AccessToken) -or $AccessToken -eq "auto" -or $AccessToken -eq "<token>") {
    $tokenResponse = Get-KeycloakOidcToken -BaseUrl $KeycloakBaseUrl -RealmName $Realm -ClientId $ClientId -ClientSecret $ClientSecret -Username $Username -Password $Password -Scope $Scope
    if ([string]::IsNullOrWhiteSpace($tokenResponse.access_token)) {
        throw "OIDC token response did not contain access_token."
    }
    $AccessToken = $tokenResponse.access_token
    $source = "oidc-password-grant"
    ($tokenResponse | ConvertTo-Json -Depth 20) | Set-Content -Path $rawResponsePath -Encoding UTF8
}
else {
    $source = "provided-token"
}

$parts = @($AccessToken -split '\.')
if ($parts.Count -lt 2) {
    throw "Access token is not a JWT."
}

$header = ConvertFrom-Base64UrlJson -Segment $parts[0]
$payload = ConvertFrom-Base64UrlJson -Segment $parts[1]

$AccessToken | Set-Content -Path $rawPath -Encoding UTF8
($header | ConvertTo-Json -Depth 20) | Set-Content -Path $headerPath -Encoding UTF8
($payload | ConvertTo-Json -Depth 20) | Set-Content -Path $payloadPath -Encoding UTF8

$idTokenSubject = $null
if ($source -eq "oidc-password-grant" -and -not [string]::IsNullOrWhiteSpace($tokenResponse.id_token)) {
    $idToken = $tokenResponse.id_token
    $idTokenParts = @($idToken -split '\.')
    if ($idTokenParts.Count -ge 2) {
        $idTokenHeader = ConvertFrom-Base64UrlJson -Segment $idTokenParts[0]
        $idTokenPayload = ConvertFrom-Base64UrlJson -Segment $idTokenParts[1]
        $idToken | Set-Content -Path $idTokenRawPath -Encoding UTF8
        ($idTokenHeader | ConvertTo-Json -Depth 20) | Set-Content -Path $idTokenHeaderPath -Encoding UTF8
        ($idTokenPayload | ConvertTo-Json -Depth 20) | Set-Content -Path $idTokenPayloadPath -Encoding UTF8
        $idTokenSubject = Get-ClaimValue -Object $idTokenPayload -Name "sub"
    }
}

$userinfo = $null
if ($source -eq "oidc-password-grant") {
    try {
        $userinfo = Get-KeycloakUserInfo -BaseUrl $KeycloakBaseUrl -RealmName $Realm -AccessToken $AccessToken
        ($userinfo | ConvertTo-Json -Depth 20) | Set-Content -Path $userinfoPath -Encoding UTF8
    }
    catch {
        ("{0}" -f $_.Exception.Message) | Set-Content -Path $userinfoPath -Encoding UTF8
    }
}

$realmAccess = Get-ClaimValue -Object $payload -Name "realm_access"
$roles = @()
if ($null -ne $realmAccess) {
    $realmRoles = Get-ClaimValue -Object $realmAccess -Name "roles"
    if ($null -ne $realmRoles) {
        $roles = @($realmRoles)
    }
}
$topLevelRoles = Get-ClaimValue -Object $payload -Name "role"
if ($null -ne $topLevelRoles) {
    $roles += @($topLevelRoles)
}
$roles = @($roles | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique)

$subject = Get-ClaimValue -Object $payload -Name "sub"
$preferredUsername = Get-ClaimValue -Object $payload -Name "preferred_username"
$email = Get-ClaimValue -Object $payload -Name "email"
$tenantId = Get-ClaimValue -Object $payload -Name "tenant_id"
$organizationId = Get-ClaimValue -Object $payload -Name "org_id"
$audience = @((Get-ClaimValue -Object $payload -Name "aud"))
$issuedAt = Get-ClaimValue -Object $payload -Name "iat"
$expiresAt = Get-ClaimValue -Object $payload -Name "exp"
$userinfoSubject = Get-ClaimValue -Object $userinfo -Name "sub"
$userinfoPreferredUsername = Get-ClaimValue -Object $userinfo -Name "preferred_username"
$userinfoEmail = Get-ClaimValue -Object $userinfo -Name "email"
$userinfoTenantId = Get-ClaimValue -Object $userinfo -Name "tenant_id"
$userinfoOrganizationId = Get-ClaimValue -Object $userinfo -Name "org_id"
$callerContextReady = `
    -not [string]::IsNullOrWhiteSpace([string]$preferredUsername) -and `
    -not [string]::IsNullOrWhiteSpace([string]$tenantId) -and `
    -not [string]::IsNullOrWhiteSpace([string]$organizationId) -and `
    ($roles | Where-Object { $_ -like "operator.*" }).Count -gt 0

$summary = @(
    "=== Token Debug Summary ===",
    "Realm: $Realm",
    "ClientId: $ClientId",
    "Username: $Username",
    "Source: $source",
    ("Subject: {0}" -f $subject),
    ("PreferredUsername: {0}" -f $preferredUsername),
    ("Email: {0}" -f $email),
    ("TenantId: {0}" -f $tenantId),
    ("OrganizationId: {0}" -f $organizationId),
    ("IdTokenSubject: {0}" -f $idTokenSubject),
    ("UserInfoSubject: {0}" -f $userinfoSubject),
    ("UserInfoPreferredUsername: {0}" -f $userinfoPreferredUsername),
    ("UserInfoEmail: {0}" -f $userinfoEmail),
    ("UserInfoTenantId: {0}" -f $userinfoTenantId),
    ("UserInfoOrganizationId: {0}" -f $userinfoOrganizationId),
    ("CallerContextReady: {0}" -f ($(if ($callerContextReady) { "true" } else { "false" }))),
    ("Audience: {0}" -f ($audience -join ", ")),
    ("IssuedAtUtc: {0}" -f ($(if ($issuedAt) { [DateTimeOffset]::FromUnixTimeSeconds([int64]$issuedAt).UtcDateTime.ToString("O") } else { "" }))),
    ("ExpiresAtUtc: {0}" -f ($(if ($expiresAt) { [DateTimeOffset]::FromUnixTimeSeconds([int64]$expiresAt).UtcDateTime.ToString("O") } else { "" }))),
    ("Roles: {0}" -f ($roles -join ", ")),
    "RawTokenResponse: $rawResponsePath",
    "HeaderJson: $headerPath",
    "PayloadJson: $payloadPath",
    "IdTokenHeaderJson: $idTokenHeaderPath",
    "IdTokenPayloadJson: $idTokenPayloadPath",
    "IdTokenRaw: $idTokenRawPath",
    "UserInfoJson: $userinfoPath",
    "RawToken: $rawPath"
)

$summary | Set-Content -Path $summaryPath -Encoding UTF8
Get-Content $summaryPath | Write-Host
exit 0

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Get-KeycloakErrorResponseBody {
    param([System.Exception]$Exception)

    $responseProperty = $Exception.PSObject.Properties["Response"]
    $response = if ($null -ne $responseProperty) { $responseProperty.Value } else { $null }
    if ($null -eq $response) {
        return $null
    }

    try {
        $stream = $response.GetResponseStream()
        if ($null -eq $stream) {
            return $null
        }

        $reader = New-Object System.IO.StreamReader($stream)
        try {
            return $reader.ReadToEnd()
        }
        finally {
            $reader.Dispose()
            $stream.Dispose()
        }
    }
    catch {
        return $null
    }
}

function Get-KeycloakAdminToken {
    param(
        [string]$BaseUrl,
        [string]$AdminRealm = "master",
        [string]$AdminUser,
        [string]$AdminPassword
    )

    $tokenEndpoint = "$($BaseUrl.TrimEnd('/'))/realms/$AdminRealm/protocol/openid-connect/token"
    $body = @{
        grant_type = "password"
        client_id = "admin-cli"
        username = $AdminUser
        password = $AdminPassword
    }

    try {
        $response = Invoke-RestMethod -Method Post -Uri $tokenEndpoint -ContentType "application/x-www-form-urlencoded" -Body $body -TimeoutSec 30
    }
    catch {
        $responseBody = Get-KeycloakErrorResponseBody $_.Exception
        if ($null -ne $responseBody -and $responseBody -match '"error"\s*:\s*"invalid_grant"') {
            throw @"
Keycloak admin authentication failed for '$AdminUser'.

Used token endpoint:
$tokenEndpoint

Most likely causes:
- admin username/password are different in the running Keycloak instance
- you are using a temporary admin and not the bootstrap one
- the container was recreated with different credentials
"@
        }

        if ([string]::IsNullOrWhiteSpace($responseBody)) {
            throw "Keycloak admin authentication failed for ${AdminUser}: $($_.Exception.Message)"
        }

        throw "Keycloak admin authentication failed for ${AdminUser}: $($_.Exception.Message)`n$responseBody"
    }

    if ([string]::IsNullOrWhiteSpace($response.access_token)) {
        throw "Keycloak admin token response did not contain access_token."
    }

    return $response.access_token
}

function Get-KeycloakOidcToken {
    param(
        [string]$BaseUrl,
        [string]$RealmName,
        [string]$ClientId,
        [string]$Username,
        [string]$Password,
        [string]$ClientSecret = "",
        [string]$Scope = ""
    )

    $tokenEndpoint = "$($BaseUrl.TrimEnd('/'))/realms/$RealmName/protocol/openid-connect/token"
    $scopeCandidates = @()
    if (-not [string]::IsNullOrWhiteSpace($Scope)) {
        $scopeCandidates += $Scope
    }

    foreach ($fallbackScope in @("openid", "")) {
        if ($scopeCandidates -notcontains $fallbackScope) {
            $scopeCandidates += $fallbackScope
        }
    }

    $lastError = $null
    foreach ($scopeCandidate in $scopeCandidates) {
        $body = @{
            grant_type = "password"
            client_id = $ClientId
            username = $Username
            password = $Password
        }

        if (-not [string]::IsNullOrWhiteSpace($ClientSecret)) {
            $body.client_secret = $ClientSecret
        }

        if (-not [string]::IsNullOrWhiteSpace($scopeCandidate)) {
            $body.scope = $scopeCandidate
        }

        try {
            return Invoke-RestMethod -Method Post -Uri $tokenEndpoint -ContentType "application/x-www-form-urlencoded" -Body $body -TimeoutSec 30
        }
        catch {
            $responseBody = Get-KeycloakErrorResponseBody $_.Exception
            $message = if ([string]::IsNullOrWhiteSpace($responseBody)) {
                "Keycloak OIDC token request failed for ${Username}/${ClientId}: $($_.Exception.Message)"
            }
            else {
                "Keycloak OIDC token request failed for ${Username}/${ClientId}: $($_.Exception.Message)`n$responseBody"
            }

            $lastError = $message
            $hasMoreCandidates = $scopeCandidate -ne $scopeCandidates[-1]
            $isBadRequest = $message -match '\(400\)'
            $isInvalidScope = $message -match 'invalid_scope'
            if (($isInvalidScope -or $isBadRequest) -and $hasMoreCandidates) {
                continue
            }

            throw $message
        }
    }

    throw $lastError
}

function Get-KeycloakUserInfo {
    param(
        [string]$BaseUrl,
        [string]$RealmName,
        [string]$AccessToken
    )

    $userinfoEndpoint = "$($BaseUrl.TrimEnd('/'))/realms/$RealmName/protocol/openid-connect/userinfo"
    $headers = @{ Authorization = "Bearer $AccessToken" }

    try {
        return Invoke-RestMethod -Method Get -Uri $userinfoEndpoint -Headers $headers -TimeoutSec 30
    }
    catch {
        $responseBody = Get-KeycloakErrorResponseBody $_.Exception
        if ([string]::IsNullOrWhiteSpace($responseBody)) {
            throw "Keycloak userinfo request failed: $($_.Exception.Message)"
        }

        throw "Keycloak userinfo request failed: $($_.Exception.Message)`n$responseBody"
    }
}

function Invoke-KeycloakAdminJson {
    param(
        [string]$BaseUrl,
        [string]$AccessToken,
        [string]$Method,
        [string]$Path,
        [object]$Body = $null
    )

    $uri = "$($BaseUrl.TrimEnd('/'))$Path"
    $headers = @{ Authorization = "Bearer $AccessToken" }

    try {
        if ($null -eq $Body) {
            return Invoke-RestMethod -Method $Method -Uri $uri -Headers $headers -TimeoutSec 30
        }

        $json = ConvertTo-Json -InputObject $Body -Depth 100
        return Invoke-RestMethod -Method $Method -Uri $uri -Headers $headers -ContentType "application/json" -Body $json -TimeoutSec 30
    }
    catch {
        $responseBody = Get-KeycloakErrorResponseBody $_.Exception
        if ([string]::IsNullOrWhiteSpace($responseBody)) {
            throw "Keycloak API $Method $Path failed: $($_.Exception.Message)"
        }

        throw "Keycloak API $Method $Path failed: $($_.Exception.Message)`n$responseBody"
    }
}

function Invoke-KeycloakAdminNoContent {
    param(
        [string]$BaseUrl,
        [string]$AccessToken,
        [string]$Method,
        [string]$Path,
        [object]$Body = $null
    )

    if ($null -eq $Body) {
        $null = Invoke-KeycloakAdminJson -BaseUrl $BaseUrl -AccessToken $AccessToken -Method $Method -Path $Path
    }
    else {
        $null = Invoke-KeycloakAdminJson -BaseUrl $BaseUrl -AccessToken $AccessToken -Method $Method -Path $Path -Body $Body
    }
}

function Find-KeycloakClientByClientId {
    param(
        [string]$BaseUrl,
        [string]$AccessToken,
        [string]$RealmName,
        [string]$ClientId
    )

    $clients = @(Invoke-KeycloakAdminJson -BaseUrl $BaseUrl -AccessToken $AccessToken -Method Get -Path "/admin/realms/$RealmName/clients?clientId=$([Uri]::EscapeDataString($ClientId))")
    $match = @($clients | Where-Object { $_ -and $_.PSObject.Properties['clientId'] -and $_.clientId -eq $ClientId })
    if ($match.Count -eq 0) { return $null }
    return $match[0]
}

function Find-KeycloakUserByUsername {
    param(
        [string]$BaseUrl,
        [string]$AccessToken,
        [string]$RealmName,
        [string]$Username
    )

    $users = @(Invoke-KeycloakAdminJson -BaseUrl $BaseUrl -AccessToken $AccessToken -Method Get -Path "/admin/realms/$RealmName/users?username=$([Uri]::EscapeDataString($Username))&exact=true")
    $match = @($users | Where-Object { $_ -and $_.PSObject.Properties['username'] -and $_.username -eq $Username })
    if ($match.Count -eq 0) { return $null }
    return $match[0]
}

function Find-KeycloakGroupByName {
    param(
        [string]$BaseUrl,
        [string]$AccessToken,
        [string]$RealmName,
        [string]$GroupName
    )

    $groups = @(Invoke-KeycloakAdminJson -BaseUrl $BaseUrl -AccessToken $AccessToken -Method Get -Path "/admin/realms/$RealmName/groups?search=$([Uri]::EscapeDataString($GroupName))")
    $match = @($groups | Where-Object { $_ -and $_.PSObject.Properties['name'] -and $_.name -eq $GroupName })
    if ($match.Count -eq 0) { return $null }
    return $match[0]
}

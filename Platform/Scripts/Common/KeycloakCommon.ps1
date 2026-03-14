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

function Get-KeycloakCollectionItems {
    param([object]$Response)

    if ($null -eq $Response) {
        return @()
    }

    $itemsProperty = $Response.PSObject.Properties["items"]
    if ($null -ne $itemsProperty) {
        return @(Get-KeycloakCollectionItems -Response $itemsProperty.Value)
    }

    if ($Response -is [System.Collections.IEnumerable] -and -not ($Response -is [string]) -and -not ($Response -is [System.Collections.IDictionary])) {
        $items = @()
        foreach ($item in $Response) {
            $items += ,$item
        }

        return @($items)
    }

    return @($Response)
}

function Find-KeycloakClientByClientId {
    param(
        [string]$BaseUrl,
        [string]$AccessToken,
        [string]$RealmName,
        [string]$ClientId
    )

    $clients = Get-KeycloakCollectionItems -Response (Invoke-KeycloakAdminJson -BaseUrl $BaseUrl -AccessToken $AccessToken -Method Get -Path "/admin/realms/$RealmName/clients?clientId=$([Uri]::EscapeDataString($ClientId))")
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

    if ([string]::IsNullOrWhiteSpace($Username)) {
        return $null
    }

    $usernameValue = [string]$Username

    $exact = Get-KeycloakCollectionItems -Response (
        Invoke-KeycloakAdminJson `
            -BaseUrl $BaseUrl `
            -AccessToken $AccessToken `
            -Method Get `
            -Path "/admin/realms/$RealmName/users?username=$([Uri]::EscapeDataString($usernameValue))&exact=true"
    )
    $exactMatch = @(
        $exact | Where-Object {
            $_ -and
            $_.PSObject.Properties['username'] -and
            [string]::Equals([string]$_.username, $usernameValue, [StringComparison]::OrdinalIgnoreCase)
        }
    )
    if ($exactMatch.Count -gt 0) {
        return $exactMatch[0]
    }

    $search = Get-KeycloakCollectionItems -Response (
        Invoke-KeycloakAdminJson `
            -BaseUrl $BaseUrl `
            -AccessToken $AccessToken `
            -Method Get `
            -Path "/admin/realms/$RealmName/users?search=$([Uri]::EscapeDataString($usernameValue))"
    )
    $searchMatch = @(
        $search | Where-Object {
            $_ -and
            $_.PSObject.Properties['username'] -and
            [string]::Equals([string]$_.username, $usernameValue, [StringComparison]::OrdinalIgnoreCase)
        }
    )
    if ($searchMatch.Count -gt 0) {
        return $searchMatch[0]
    }

    return $null
}

function Find-KeycloakGroupByName {
    param(
        [string]$BaseUrl,
        [string]$AccessToken,
        [string]$RealmName,
        [string]$GroupName
    )

    $groups = Get-KeycloakCollectionItems -Response (Invoke-KeycloakAdminJson -BaseUrl $BaseUrl -AccessToken $AccessToken -Method Get -Path "/admin/realms/$RealmName/groups?search=$([Uri]::EscapeDataString($GroupName))")
    $match = @($groups | Where-Object { $_ -and $_.PSObject.Properties['name'] -and $_.name -eq $GroupName })
    if ($match.Count -eq 0) { return $null }
    return $match[0]
}

function ConvertTo-KeycloakHashtable {
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
            $result[$property.Name] = ConvertTo-KeycloakHashtable -InputObject $property.Value
        }

        return $result
    }

    if ($InputObject -is [System.Collections.IDictionary]) {
        $table = @{}
        foreach ($key in $InputObject.Keys) {
            $table[[string]$key] = ConvertTo-KeycloakHashtable -InputObject $InputObject[$key]
        }

        return $table
    }

    if ($InputObject -is [System.Collections.IEnumerable] -and -not ($InputObject -is [string])) {
        $items = @()
        foreach ($item in $InputObject) {
            $items += ,(ConvertTo-KeycloakHashtable -InputObject $item)
        }

        return $items
    }

    return $InputObject
}

function ConvertTo-KeycloakStringArray {
    param([object]$Value)

    if ($null -eq $Value) {
        return @()
    }

    if ($Value -is [System.Collections.IEnumerable] -and -not ($Value -is [string])) {
        $values = @()
        foreach ($item in $Value) {
            if (-not [string]::IsNullOrWhiteSpace([string]$item)) {
                $values += ,([string]$item)
            }
        }

        return @($values)
    }

    if ([string]::IsNullOrWhiteSpace([string]$Value)) {
        return @()
    }

    return @([string]$Value)
}

function ConvertTo-KeycloakAttributeHashtable {
    param([object]$Attributes)

    $source = ConvertTo-KeycloakHashtable -InputObject $Attributes
    $result = @{}
    if ($null -eq $source) {
        return $result
    }

    if (-not ($source -is [System.Collections.IDictionary])) {
        return $result
    }

    foreach ($entry in $source.GetEnumerator()) {
        $result[[string]$entry.Key] = ConvertTo-KeycloakStringArray -Value $entry.Value
    }

    return $result
}

function Test-KeycloakAttributeHashtableEqual {
    param(
        [hashtable]$Left,
        [hashtable]$Right
    )

    $leftMap = if ($null -eq $Left) { @{} } else { $Left }
    $rightMap = if ($null -eq $Right) { @{} } else { $Right }

    $leftKeys = @($leftMap.Keys | Sort-Object)
    $rightKeys = @($rightMap.Keys | Sort-Object)

    if ($leftKeys.Count -ne $rightKeys.Count) {
        return $false
    }

    for ($index = 0; $index -lt $leftKeys.Count; $index++) {
        if (-not [string]::Equals([string]$leftKeys[$index], [string]$rightKeys[$index], [StringComparison]::Ordinal)) {
            return $false
        }
    }

    foreach ($key in $leftKeys) {
        $leftValues = @(ConvertTo-KeycloakStringArray -Value $leftMap[$key])
        $rightValues = @(ConvertTo-KeycloakStringArray -Value $rightMap[$key])
        if ($leftValues.Count -ne $rightValues.Count) {
            return $false
        }

        for ($valueIndex = 0; $valueIndex -lt $leftValues.Count; $valueIndex++) {
            if (-not [string]::Equals([string]$leftValues[$valueIndex], [string]$rightValues[$valueIndex], [StringComparison]::Ordinal)) {
                return $false
            }
        }
    }

    return $true
}

function Get-KeycloakGroupById {
    param(
        [string]$BaseUrl,
        [string]$AccessToken,
        [string]$RealmName,
        [string]$GroupId
    )

    if ([string]::IsNullOrWhiteSpace($GroupId)) {
        return $null
    }

    return Invoke-KeycloakAdminJson `
        -BaseUrl $BaseUrl `
        -AccessToken $AccessToken `
        -Method Get `
        -Path "/admin/realms/$RealmName/groups/$([Uri]::EscapeDataString($GroupId))"
}

function Get-KeycloakUserById {
    param(
        [string]$BaseUrl,
        [string]$AccessToken,
        [string]$RealmName,
        [string]$UserId
    )

    if ([string]::IsNullOrWhiteSpace($UserId)) {
        return $null
    }

    return Invoke-KeycloakAdminJson `
        -BaseUrl $BaseUrl `
        -AccessToken $AccessToken `
        -Method Get `
        -Path "/admin/realms/$RealmName/users/$([Uri]::EscapeDataString($UserId))"
}

function Find-KeycloakUserByEmail {
    param(
        [string]$BaseUrl,
        [string]$AccessToken,
        [string]$RealmName,
        [string]$Email
    )

    if ([string]::IsNullOrWhiteSpace($Email)) {
        return $null
    }

    $emailValue = [string]$Email

    $exactByEmail = Get-KeycloakCollectionItems -Response (
        Invoke-KeycloakAdminJson `
            -BaseUrl $BaseUrl `
            -AccessToken $AccessToken `
            -Method Get `
            -Path "/admin/realms/$RealmName/users?email=$([Uri]::EscapeDataString($emailValue))&exact=true"
    )
    $exactEmailMatch = @(
        $exactByEmail | Where-Object {
            $_ -and
            $_.PSObject.Properties['email'] -and
            [string]::Equals([string]$_.email, $emailValue, [StringComparison]::OrdinalIgnoreCase)
        }
    )
    if ($exactEmailMatch.Count -gt 0) {
        return $exactEmailMatch[0]
    }

    $users = Get-KeycloakCollectionItems -Response (
        Invoke-KeycloakAdminJson `
            -BaseUrl $BaseUrl `
            -AccessToken $AccessToken `
            -Method Get `
            -Path "/admin/realms/$RealmName/users?search=$([Uri]::EscapeDataString($emailValue))"
    )

    $emailMatch = @(
        $users | Where-Object {
            $_ -and
            $_.PSObject.Properties['email'] -and
            [string]::Equals([string]$_.email, $emailValue, [StringComparison]::OrdinalIgnoreCase)
        }
    )
    if ($emailMatch.Count -gt 0) {
        return $emailMatch[0]
    }

    $usernameMatch = @(
        $users | Where-Object {
            $_ -and
            $_.PSObject.Properties['username'] -and
            [string]::Equals([string]$_.username, $emailValue, [StringComparison]::OrdinalIgnoreCase)
        }
    )
    if ($usernameMatch.Count -gt 0) {
        return $usernameMatch[0]
    }

    $exactByUsername = Find-KeycloakUserByUsername `
        -BaseUrl $BaseUrl `
        -AccessToken $AccessToken `
        -RealmName $RealmName `
        -Username $emailValue
    if ($null -ne $exactByUsername) {
        return $exactByUsername
    }

    return $null
}

function Resolve-KeycloakUser {
    param(
        [string]$BaseUrl,
        [string]$AccessToken,
        [string]$RealmName,
        [string]$UserId = "",
        [string]$Username = "",
        [string]$Email = ""
    )

    if (-not [string]::IsNullOrWhiteSpace($UserId)) {
        return Get-KeycloakUserById `
            -BaseUrl $BaseUrl `
            -AccessToken $AccessToken `
            -RealmName $RealmName `
            -UserId $UserId
    }

    if (-not [string]::IsNullOrWhiteSpace($Username)) {
        $byUsername = Find-KeycloakUserByUsername `
            -BaseUrl $BaseUrl `
            -AccessToken $AccessToken `
            -RealmName $RealmName `
            -Username $Username
        if ($null -ne $byUsername) {
            return $byUsername
        }
    }

    if (-not [string]::IsNullOrWhiteSpace($Email)) {
        $byEmail = Find-KeycloakUserByEmail `
            -BaseUrl $BaseUrl `
            -AccessToken $AccessToken `
            -RealmName $RealmName `
            -Email $Email
        if ($null -ne $byEmail) {
            return $byEmail
        }
    }

    $allUsers = Get-KeycloakCollectionItems -Response (
        Invoke-KeycloakAdminJson `
            -BaseUrl $BaseUrl `
            -AccessToken $AccessToken `
            -Method Get `
            -Path "/admin/realms/$RealmName/users?max=500"
    )

    if (-not [string]::IsNullOrWhiteSpace($UserId)) {
        $scanById = @(
            $allUsers | Where-Object {
                $_ -and
                $_.PSObject.Properties['id'] -and
                [string]::Equals([string]$_.id, [string]$UserId, [StringComparison]::Ordinal)
            }
        )
        if ($scanById.Count -gt 0) {
            return $scanById[0]
        }
    }

    if (-not [string]::IsNullOrWhiteSpace($Username)) {
        $scanByUsername = @(
            $allUsers | Where-Object {
                $_ -and
                $_.PSObject.Properties['username'] -and
                [string]::Equals([string]$_.username, [string]$Username, [StringComparison]::OrdinalIgnoreCase)
            }
        )
        if ($scanByUsername.Count -gt 0) {
            return $scanByUsername[0]
        }
    }

    if (-not [string]::IsNullOrWhiteSpace($Email)) {
        $scanByEmail = @(
            $allUsers | Where-Object {
                $_ -and
                $_.PSObject.Properties['email'] -and
                [string]::Equals([string]$_.email, [string]$Email, [StringComparison]::OrdinalIgnoreCase)
            }
        )
        if ($scanByEmail.Count -gt 0) {
            return $scanByEmail[0]
        }
    }

    return $null
}

function Get-KeycloakRealmRoleByName {
    param(
        [string]$BaseUrl,
        [string]$AccessToken,
        [string]$RealmName,
        [string]$RoleName
    )

    return Invoke-KeycloakAdminJson `
        -BaseUrl $BaseUrl `
        -AccessToken $AccessToken `
        -Method Get `
        -Path "/admin/realms/$RealmName/roles/$([Uri]::EscapeDataString($RoleName))"
}

function Ensure-KeycloakGroup {
    param(
        [string]$BaseUrl,
        [string]$AccessToken,
        [string]$RealmName,
        [string]$GroupName,
        [string[]]$RealmRoleNames = @(),
        [hashtable]$Attributes = @{}
    )

    $created = $false
    $attributesUpdated = $false
    $assignedRoles = @()

    $group = Find-KeycloakGroupByName `
        -BaseUrl $BaseUrl `
        -AccessToken $AccessToken `
        -RealmName $RealmName `
        -GroupName $GroupName

    if ($null -eq $group) {
        Invoke-KeycloakAdminNoContent `
            -BaseUrl $BaseUrl `
            -AccessToken $AccessToken `
            -Method Post `
            -Path "/admin/realms/$RealmName/groups" `
            -Body @{ name = $GroupName }

        $group = Find-KeycloakGroupByName `
            -BaseUrl $BaseUrl `
            -AccessToken $AccessToken `
            -RealmName $RealmName `
            -GroupName $GroupName
        $created = $true
    }

    if ($null -eq $group -or -not $group.PSObject.Properties['id'] -or [string]::IsNullOrWhiteSpace([string]$group.id)) {
        throw "Failed to resolve group '$GroupName'."
    }

    $group = Get-KeycloakGroupById `
        -BaseUrl $BaseUrl `
        -AccessToken $AccessToken `
        -RealmName $RealmName `
        -GroupId ([string]$group.id)

    $desiredAttributes = ConvertTo-KeycloakAttributeHashtable -Attributes $Attributes
    if ($desiredAttributes.Count -gt 0) {
        $groupAttributes = $null
        if ($group.PSObject.Properties['attributes']) {
            $groupAttributes = $group.attributes
        }

        $existingAttributes = ConvertTo-KeycloakAttributeHashtable -Attributes $groupAttributes
        if (-not (Test-KeycloakAttributeHashtableEqual -Left $existingAttributes -Right $desiredAttributes)) {
            Invoke-KeycloakAdminNoContent `
                -BaseUrl $BaseUrl `
                -AccessToken $AccessToken `
                -Method Put `
                -Path "/admin/realms/$RealmName/groups/$([Uri]::EscapeDataString([string]$group.id))" `
                -Body @{
                    name = $GroupName
                    attributes = $desiredAttributes
                }
            $attributesUpdated = $true
        }
    }

    $requestedRoleNames = @($RealmRoleNames | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique)
    if ($requestedRoleNames.Count -gt 0) {
        $currentRoles = Get-KeycloakCollectionItems -Response (
            Invoke-KeycloakAdminJson `
                -BaseUrl $BaseUrl `
                -AccessToken $AccessToken `
                -Method Get `
                -Path "/admin/realms/$RealmName/groups/$([Uri]::EscapeDataString([string]$group.id))/role-mappings/realm"
        )
        $existingRoleNames = @(
            $currentRoles | Where-Object { $_ -and $_.PSObject.Properties['name'] } | ForEach-Object { [string]$_.name }
        )

        $rolePayload = @()
        foreach ($roleName in $requestedRoleNames) {
            if ($existingRoleNames -contains $roleName) {
                continue
            }

            $role = Get-KeycloakRealmRoleByName `
                -BaseUrl $BaseUrl `
                -AccessToken $AccessToken `
                -RealmName $RealmName `
                -RoleName $roleName
            if ($null -eq $role) {
                throw "Realm role '$roleName' was not found."
            }

            $rolePayload += ,@{
                id = $role.id
                name = $role.name
                description = $role.description
                composite = $role.composite
                clientRole = $role.clientRole
                containerId = $role.containerId
            }
        }

        if ($rolePayload.Count -gt 0) {
            Invoke-KeycloakAdminNoContent `
                -BaseUrl $BaseUrl `
                -AccessToken $AccessToken `
                -Method Post `
                -Path "/admin/realms/$RealmName/groups/$([Uri]::EscapeDataString([string]$group.id))/role-mappings/realm" `
                -Body $rolePayload
            $assignedRoles = @($rolePayload | ForEach-Object { [string]$_.name })
        }
    }

    return @{
        Group = $group
        Created = $created
        AttributesUpdated = $attributesUpdated
        AssignedRealmRoles = @($assignedRoles)
    }
}

function Test-KeycloakUserInGroup {
    param(
        [string]$BaseUrl,
        [string]$AccessToken,
        [string]$RealmName,
        [string]$UserId,
        [string]$GroupId
    )

    $groups = Get-KeycloakCollectionItems -Response (
        Invoke-KeycloakAdminJson `
            -BaseUrl $BaseUrl `
            -AccessToken $AccessToken `
            -Method Get `
            -Path "/admin/realms/$RealmName/users/$([Uri]::EscapeDataString($UserId))/groups"
    )

    return @(
        $groups | Where-Object {
            $_ -and
            $_.PSObject.Properties['id'] -and
            [string]::Equals([string]$_.id, [string]$GroupId, [StringComparison]::Ordinal)
        }
    ).Count -gt 0
}

function Ensure-KeycloakUserInGroup {
    param(
        [string]$BaseUrl,
        [string]$AccessToken,
        [string]$RealmName,
        [string]$UserId,
        [string]$GroupId
    )

    $alreadyAssigned = Test-KeycloakUserInGroup `
        -BaseUrl $BaseUrl `
        -AccessToken $AccessToken `
        -RealmName $RealmName `
        -UserId $UserId `
        -GroupId $GroupId

    if ($alreadyAssigned) {
        return $false
    }

    Invoke-KeycloakAdminNoContent `
        -BaseUrl $BaseUrl `
        -AccessToken $AccessToken `
        -Method Put `
        -Path "/admin/realms/$RealmName/users/$([Uri]::EscapeDataString($UserId))/groups/$([Uri]::EscapeDataString($GroupId))"
    return $true
}

function New-KeycloakUserUpdateBody {
    param(
        [object]$User,
        [hashtable]$Attributes
    )

    if ($null -eq $User -or -not $User.PSObject.Properties['id']) {
        throw "New-KeycloakUserUpdateBody expects a resolved user object with id."
    }

    return @{
        id = $User.id
        username = $User.username
        enabled = if ($User.PSObject.Properties['enabled']) { [bool]$User.enabled } else { $true }
        emailVerified = if ($User.PSObject.Properties['emailVerified']) { [bool]$User.emailVerified } else { $false }
        firstName = if ($User.PSObject.Properties['firstName']) { $User.firstName } else { $null }
        lastName = if ($User.PSObject.Properties['lastName']) { $User.lastName } else { $null }
        email = if ($User.PSObject.Properties['email']) { $User.email } else { $null }
        attributes = if ($null -eq $Attributes) { @{} } else { $Attributes }
    }
}

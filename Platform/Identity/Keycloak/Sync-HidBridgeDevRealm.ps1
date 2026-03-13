param(
    [string]$KeycloakBaseUrl = "http://127.0.0.1:18096",
    [string]$AdminRealm = "master",
    [string]$AdminUser = "",
    [string]$AdminPassword = "",
    [string]$RealmName = "hidbridge-dev",
    [string]$ImportPath = "",
    [string]$BackupRoot = "",
    [switch]$IncludeUsersInBackup,
    [switch]$SkipUsers,
    [switch]$SkipGroups,
    [string]$DefaultOperatorTenantId = "local-tenant",
    [string]$DefaultOperatorOrganizationId = "local-org",
    [string[]]$ExternalProviderConfigPaths = @()
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

. (Join-Path (Join-Path (Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) "Scripts") "Common") "KeycloakCommon.ps1")
$repoRoot = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSScriptRoot))

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

$desiredRealm = Get-Content $ImportPath -Raw | ConvertFrom-Json
$runStamp = [DateTimeOffset]::UtcNow.ToString("yyyyMMdd-HHmmss")
$backupPath = Join-Path $BackupRoot "$RealmName-$runStamp.json"
$activityLogPath = Join-Path $BackupRoot "$RealmName-$runStamp.log"
$activity = New-Object System.Collections.Generic.List[string]

function Write-Activity {
    param([string]$Message)

    $activity.Add($Message) | Out-Null
    Write-Host $Message
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

function Get-AdminToken {
    return Get-KeycloakAdminToken -BaseUrl $KeycloakBaseUrl -AdminRealm $AdminRealm -AdminUser $AdminUser -AdminPassword $AdminPassword
}

function Invoke-Keycloak {
    param(
        [string]$Method,
        [string]$Path,
        [object]$Body = $null
    )

    return Invoke-KeycloakAdminJson -BaseUrl $KeycloakBaseUrl -AccessToken $script:AdminToken -Method $Method -Path $Path -Body $Body
}

function Invoke-KeycloakNoContent {
    param(
        [string]$Method,
        [string]$Path,
        [object]$Body = $null
    )

    Invoke-KeycloakAdminNoContent -BaseUrl $KeycloakBaseUrl -AccessToken $script:AdminToken -Method $Method -Path $Path -Body $Body
}

function Get-Realm {
    try {
        return Invoke-Keycloak -Method Get -Path "/admin/realms/$RealmName"
    }
    catch {
        if ($_.Exception.Message -match "\(404\)") {
            return $null
        }

        throw
    }
}

function Backup-Realm {
    param([switch]$IncludeUsers)

    $realm = Invoke-Keycloak -Method Get -Path "/admin/realms/$RealmName"
    $roles = Invoke-Keycloak -Method Get -Path "/admin/realms/$RealmName/roles"
    $clientScopes = Invoke-Keycloak -Method Get -Path "/admin/realms/$RealmName/client-scopes"
    $clients = Invoke-Keycloak -Method Get -Path "/admin/realms/$RealmName/clients"
    $groups = Invoke-Keycloak -Method Get -Path "/admin/realms/$RealmName/groups?briefRepresentation=false"
    $identityProviders = Invoke-Keycloak -Method Get -Path "/admin/realms/$RealmName/identity-provider/instances"
    $users = @()

    if ($IncludeUsers) {
        $users = Invoke-Keycloak -Method Get -Path "/admin/realms/$RealmName/users?max=500"
    }

    $backup = [ordered]@{
        exportedAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
        keycloakBaseUrl = $KeycloakBaseUrl
        realm = ConvertTo-Hashtable $realm
        roles = ConvertTo-Hashtable $roles
        clientScopes = ConvertTo-Hashtable $clientScopes
        clients = ConvertTo-Hashtable $clients
        groups = ConvertTo-Hashtable $groups
        identityProviders = ConvertTo-Hashtable $identityProviders
        users = ConvertTo-Hashtable $users
    }

    $backup | ConvertTo-Json -Depth 100 | Set-Content -Path $backupPath -Encoding UTF8
    Write-Activity "Backup written: $backupPath"
}

function Ensure-RealmSettings {
    $realmBody = [ordered]@{
        realm = $desiredRealm.realm
        enabled = $desiredRealm.enabled
        displayName = $desiredRealm.displayName
        registrationAllowed = $desiredRealm.registrationAllowed
        rememberMe = $desiredRealm.rememberMe
        resetPasswordAllowed = $desiredRealm.resetPasswordAllowed
        editUsernameAllowed = $desiredRealm.editUsernameAllowed
        loginWithEmailAllowed = $desiredRealm.loginWithEmailAllowed
        duplicateEmailsAllowed = $desiredRealm.duplicateEmailsAllowed
        internationalizationEnabled = $desiredRealm.internationalizationEnabled
        supportedLocales = @($desiredRealm.supportedLocales)
        defaultLocale = $desiredRealm.defaultLocale
    }

    Invoke-KeycloakNoContent -Method Put -Path "/admin/realms/$RealmName" -Body $realmBody
    Write-Activity "Realm settings normalized."
}

function Get-RealmRole {
    param([string]$RoleName)

    try {
        return Invoke-Keycloak -Method Get -Path "/admin/realms/$RealmName/roles/$([Uri]::EscapeDataString($RoleName))"
    }
    catch {
        if ($_.Exception.Message -match "\(404\)") {
            return $null
        }

        throw
    }
}

function Ensure-RealmRole {
    param([object]$Role)

    $existing = Get-RealmRole -RoleName $Role.name
    $body = @{
        name = $Role.name
        description = $Role.description
    }

    if ($null -eq $existing) {
        Invoke-KeycloakNoContent -Method Post -Path "/admin/realms/$RealmName/roles" -Body $body
        Write-Activity "Created realm role: $($Role.name)"
        return
    }

    Invoke-KeycloakNoContent -Method Put -Path "/admin/realms/$RealmName/roles/$([Uri]::EscapeDataString($Role.name))" -Body $body
    Write-Activity "Updated realm role: $($Role.name)"
}

function Get-ClientScopeByName {
    param([string]$Name)

    $scopes = Invoke-Keycloak -Method Get -Path "/admin/realms/$RealmName/client-scopes"
    $matches = @($scopes | Where-Object { $_.name -eq $Name })
    if ($matches.Count -eq 0) {
        return $null
    }

    return $matches[0]
}

function Ensure-ClientScope {
    param([object]$Scope)

    $existing = Get-ClientScopeByName -Name $Scope.name
    $body = @{
        name = $Scope.name
        description = $Scope.description
        protocol = $Scope.protocol
        attributes = ConvertTo-Hashtable $Scope.attributes
    }

    if ($null -eq $existing) {
        try {
            Invoke-KeycloakNoContent -Method Post -Path "/admin/realms/$RealmName/client-scopes" -Body $body
            $existing = Get-ClientScopeByName -Name $Scope.name
            Write-Activity "Created client scope: $($Scope.name)"
        }
        catch {
            if ($Scope.name -eq "hidbridge-caller-context-v2") {
                Write-Activity "Client scope '$($Scope.name)' could not be created via admin API; continuing without it for this realm."
                return $null
            }

            throw
        }
    }
    else {
        Write-Activity "Client scope already exists, keeping base settings and normalizing mappers only: $($Scope.name)"
    }

    $existingMappers = @(Invoke-Keycloak -Method Get -Path "/admin/realms/$RealmName/client-scopes/$($existing.id)/protocol-mappers/models")
    foreach ($mapper in @($Scope.protocolMappers)) {
        $matchingMappers = @($existingMappers | Where-Object {
            $null -ne $_ -and
            $null -ne $_.PSObject.Properties["name"] -and
            $_.name -eq $mapper.name
        })
        $existingMapper = if ($matchingMappers.Count -gt 0) { $matchingMappers[0] } else { $null }
        $mapperBody = @{
            name = $mapper.name
            protocol = $mapper.protocol
            protocolMapper = $mapper.protocolMapper
            consentRequired = [bool]$mapper.consentRequired
            config = ConvertTo-Hashtable $mapper.config
        }

        if ($null -eq $existingMapper) {
            try {
                Invoke-KeycloakNoContent -Method Post -Path "/admin/realms/$RealmName/client-scopes/$($existing.id)/protocol-mappers/models" -Body $mapperBody
                Write-Activity "Created client scope mapper: $($Scope.name)/$($mapper.name)"
            }
            catch {
                if ($_.Exception.Message -match "\(409\)") {
                    Write-Activity "Client scope mapper already exists (conflict), keeping existing mapper: $($Scope.name)/$($mapper.name)"
                }
                else {
                    throw
                }
            }
        }
        else {
            try {
                $mapperBody["id"] = $existingMapper.id
                Invoke-KeycloakNoContent -Method Put -Path "/admin/realms/$RealmName/client-scopes/$($existing.id)/protocol-mappers/models/$($existingMapper.id)" -Body $mapperBody
                Write-Activity "Updated client scope mapper: $($Scope.name)/$($mapper.name)"
            }
            catch {
                if ($_.Exception.Message -match "\(409\)") {
                    Write-Activity "Client scope mapper update conflict, keeping existing mapper: $($Scope.name)/$($mapper.name)"
                }
                else {
                    throw
                }
            }
        }
    }

    return Get-ClientScopeByName -Name $Scope.name
}

function Get-ClientByClientId {
    param([string]$ClientId)

    $clients = Invoke-Keycloak -Method Get -Path "/admin/realms/$RealmName/clients?clientId=$([Uri]::EscapeDataString($ClientId))"
    $matches = @($clients | Where-Object { $_.clientId -eq $ClientId })
    if ($matches.Count -eq 0) {
        return $null
    }

    return $matches[0]
}

function Get-DefaultClientScopeIds {
    param([string]$ClientUuid)

    return @((Invoke-Keycloak -Method Get -Path "/admin/realms/$RealmName/clients/$ClientUuid/default-client-scopes") | ForEach-Object { $_.id })
}

function Get-OptionalClientScopeIds {
    param([string]$ClientUuid)

    return @((Invoke-Keycloak -Method Get -Path "/admin/realms/$RealmName/clients/$ClientUuid/optional-client-scopes") | ForEach-Object { $_.id })
}

function Ensure-ClientScopeAssignments {
    param(
        [string]$ClientUuid,
        [string[]]$DefaultScopeNames,
        [string[]]$OptionalScopeNames
    )

    $scopeLookup = @{}
    foreach ($scope in @(Invoke-Keycloak -Method Get -Path "/admin/realms/$RealmName/client-scopes")) {
        $scopeLookup[$scope.name] = $scope.id
    }

    $currentDefault = Get-DefaultClientScopeIds -ClientUuid $ClientUuid
    foreach ($scopeName in $DefaultScopeNames) {
        if (-not $scopeLookup.ContainsKey($scopeName)) {
            continue
        }

        $scopeId = $scopeLookup[$scopeName]
        if ($currentDefault -contains $scopeId) {
            continue
        }

        Invoke-KeycloakNoContent -Method Put -Path "/admin/realms/$RealmName/clients/$ClientUuid/default-client-scopes/$scopeId"
        Write-Activity "Attached default client scope '$scopeName' to client $ClientUuid"
    }

    $currentOptional = Get-OptionalClientScopeIds -ClientUuid $ClientUuid
    foreach ($scopeName in $OptionalScopeNames) {
        if (-not $scopeLookup.ContainsKey($scopeName)) {
            continue
        }

        $scopeId = $scopeLookup[$scopeName]
        if ($currentOptional -contains $scopeId) {
            continue
        }

        Invoke-KeycloakNoContent -Method Put -Path "/admin/realms/$RealmName/clients/$ClientUuid/optional-client-scopes/$scopeId"
        Write-Activity "Attached optional client scope '$scopeName' to client $ClientUuid"
    }
}

function Ensure-Client {
    param([object]$Client)

    $existing = Get-ClientByClientId -ClientId $Client.clientId
    $clientWasCreated = $false
    $body = @{
        clientId = $Client.clientId
        name = $Client.name
        description = $Client.description
        enabled = $Client.enabled
        protocol = $Client.protocol
    }

    foreach ($propertyName in @(
        "publicClient",
        "secret",
        "standardFlowEnabled",
        "directAccessGrantsEnabled",
        "serviceAccountsEnabled",
        "implicitFlowEnabled",
        "bearerOnly",
        "frontchannelLogout",
        "redirectUris",
        "webOrigins")) {
        $property = $Client.PSObject.Properties[$propertyName]
        if ($null -ne $property) {
            $body[$propertyName] = ConvertTo-Hashtable $property.Value
        }
    }

    if ($null -eq $existing) {
        Invoke-KeycloakNoContent -Method Post -Path "/admin/realms/$RealmName/clients" -Body $body
        $existing = Get-ClientByClientId -ClientId $Client.clientId
        $clientWasCreated = $true
        Write-Activity "Created client: $($Client.clientId)"
    }
    else {
        Write-Activity "Client already exists, keeping base settings and normalizing scope assignments/mappers only: $($Client.clientId)"
    }

    $defaultClientScopesProperty = $Client.PSObject.Properties["defaultClientScopes"]
    $optionalClientScopesProperty = $Client.PSObject.Properties["optionalClientScopes"]
    $defaultClientScopes = if ($null -ne $defaultClientScopesProperty) { @($defaultClientScopesProperty.Value) } else { @() }
    $optionalClientScopes = if ($null -ne $optionalClientScopesProperty) { @($optionalClientScopesProperty.Value) } else { @() }

    Ensure-ClientScopeAssignments `
        -ClientUuid $existing.id `
        -DefaultScopeNames $defaultClientScopes `
        -OptionalScopeNames $optionalClientScopes

    if (-not $clientWasCreated) {
        Write-Activity "Client already exists, keeping current protocol mappers: $($Client.clientId)"
        return Get-ClientByClientId -ClientId $Client.clientId
    }

    $clientProtocolMappersProperty = $Client.PSObject.Properties["protocolMappers"]
    if ($null -eq $clientProtocolMappersProperty) {
        return Get-ClientByClientId -ClientId $Client.clientId
    }

    $existingMappers = @(Invoke-Keycloak -Method Get -Path "/admin/realms/$RealmName/clients/$($existing.id)/protocol-mappers/models")
    foreach ($mapper in @($clientProtocolMappersProperty.Value)) {
        $matchingMappers = @($existingMappers | Where-Object {
            $null -ne $_ -and
            $null -ne $_.PSObject.Properties["name"] -and
            $_.name -eq $mapper.name
        })
        $existingMapper = if ($matchingMappers.Count -gt 0) { $matchingMappers[0] } else { $null }
        $mapperBody = @{
            name = $mapper.name
            protocol = $mapper.protocol
            protocolMapper = $mapper.protocolMapper
            consentRequired = [bool]$mapper.consentRequired
            config = ConvertTo-Hashtable $mapper.config
        }

        if ($null -eq $existingMapper) {
            try {
                Invoke-KeycloakNoContent -Method Post -Path "/admin/realms/$RealmName/clients/$($existing.id)/protocol-mappers/models" -Body $mapperBody
                Write-Activity "Created client mapper: $($Client.clientId)/$($mapper.name)"
            }
            catch {
                if ($_.Exception.Message -match "\(409\)") {
                    Write-Activity "Client mapper already exists (conflict), keeping existing mapper: $($Client.clientId)/$($mapper.name)"
                }
                else {
                    throw
                }
            }
        }
        else {
            Write-Activity "Client mapper already exists, keeping current mapper: $($Client.clientId)/$($mapper.name)"
        }
    }

    return Get-ClientByClientId -ClientId $Client.clientId
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
            Join-Path $repoRoot $candidate
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
        return Invoke-Keycloak -Method Get -Path "/admin/realms/$RealmName/identity-provider/instances/$([Uri]::EscapeDataString($Alias))"
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
        Invoke-KeycloakNoContent -Method Post -Path "/admin/realms/$RealmName/identity-provider/instances" -Body $providerBody
        Write-Activity "Created identity provider: $alias (from $ConfigPath)"
        return
    }

    Invoke-KeycloakNoContent -Method Put -Path "/admin/realms/$RealmName/identity-provider/instances/$([Uri]::EscapeDataString($alias))" -Body $providerBody
    Write-Activity "Updated identity provider: $alias (from $ConfigPath)"
}

function Get-UserByUsername {
    param([string]$Username)

    $users = Invoke-Keycloak -Method Get -Path "/admin/realms/$RealmName/users?username=$([Uri]::EscapeDataString($Username))&exact=true"
    $matches = @($users | Where-Object {
        $null -ne $_ -and
        $null -ne $_.PSObject.Properties["username"] -and
        $_.username -eq $Username
    })

    if ($matches.Count -eq 0) {
        return $null
    }

    return $matches[0]
}

function Set-UserPassword {
    param(
        [string]$UserId,
        [string]$Password
    )

    Invoke-KeycloakNoContent -Method Put -Path "/admin/realms/$RealmName/users/$UserId/reset-password" -Body @{
        type = "password"
        value = $Password
        temporary = $false
    }
}

function Ensure-UserRealmRoles {
    param(
        [string]$UserId,
        [string[]]$RoleNames
    )

    $existing = @(Invoke-Keycloak -Method Get -Path "/admin/realms/$RealmName/users/$UserId/role-mappings/realm")
    $existingNames = @($existing | Where-Object { $null -ne $_ -and $null -ne $_.PSObject.Properties["name"] } | ForEach-Object { $_.name })
    $missing = foreach ($roleName in $RoleNames) {
        if ($existingNames -notcontains $roleName) {
            Get-RealmRole -RoleName $roleName
        }
    }

    $missing = @($missing | Where-Object { $null -ne $_ })
    if ($missing.Count -gt 0) {
        $body = @($missing | ForEach-Object {
            @{
                id = $_.id
                name = $_.name
                description = $_.description
                composite = $_.composite
                clientRole = $_.clientRole
                containerId = $_.containerId
            }
        })
        Invoke-KeycloakNoContent -Method Post -Path "/admin/realms/$RealmName/users/$UserId/role-mappings/realm" -Body $body
    }
}

function Ensure-UserInGroup {
    param(
        [string]$UserId,
        [string]$GroupName
    )

    $group = Get-GroupByName -Name $GroupName
    if ($null -eq $group) {
        throw "Group '$GroupName' was not found."
    }

    $currentGroups = @(Invoke-Keycloak -Method Get -Path "/admin/realms/$RealmName/users/$UserId/groups")
    $alreadyAssigned = @($currentGroups | Where-Object {
        $null -ne $_ -and
        $null -ne $_.PSObject.Properties["id"] -and
        $_.id -eq $group.id
    }).Count -gt 0

    if ($alreadyAssigned) {
        return
    }

    Invoke-KeycloakNoContent -Method Put -Path "/admin/realms/$RealmName/users/$UserId/groups/$($group.id)"
    Write-Activity "Assigned user $UserId to group '$GroupName'"
}

function Get-UserTargetGroups {
    param([object]$User)

    $targetGroups = @()
    $realmRolesProperty = $User.PSObject.Properties["realmRoles"]
    if ($null -eq $realmRolesProperty) {
        return $targetGroups
    }

    $roleNames = @($realmRolesProperty.Value)
    if ($roleNames -contains "operator.admin") {
        $targetGroups += "hidbridge-admins"
    }
    elseif ($roleNames -contains "operator.viewer") {
        $targetGroups += "hidbridge-operators"
    }

    return $targetGroups
}

function Ensure-User {
    param([object]$User)

    $existing = Get-UserByUsername -Username $User.username
    $body = @{
        username = $User.username
        enabled = $User.enabled
        emailVerified = $User.emailVerified
        firstName = $User.firstName
        lastName = $User.lastName
        email = $User.email
        attributes = ConvertTo-Hashtable $User.attributes
    }

    if ($null -eq $existing) {
        Invoke-KeycloakNoContent -Method Post -Path "/admin/realms/$RealmName/users" -Body $body
        $existing = Get-UserByUsername -Username $User.username
        Write-Activity "Created user: $($User.username)"
    }
    else {
        $body.id = $existing.id
        Invoke-KeycloakNoContent -Method Put -Path "/admin/realms/$RealmName/users/$($existing.id)" -Body $body
        Write-Activity "Updated user: $($User.username)"
    }

    $password = @($User.credentials | Select-Object -First 1)[0].value
    if (-not [string]::IsNullOrWhiteSpace($password)) {
        Set-UserPassword -UserId $existing.id -Password $password
    }

    foreach ($groupName in @(Get-UserTargetGroups -User $User)) {
        Ensure-UserInGroup -UserId $existing.id -GroupName $groupName
    }
}

function Get-GroupByName {
    param([string]$Name)

    $groups = Invoke-Keycloak -Method Get -Path "/admin/realms/$RealmName/groups?search=$([Uri]::EscapeDataString($Name))&briefRepresentation=false"
    $matches = @($groups | Where-Object {
        $null -ne $_ -and
        $null -ne $_.PSObject.Properties["name"] -and
        $_.name -eq $Name
    })

    if ($matches.Count -eq 0) {
        return $null
    }

    return $matches[0]
}

function Ensure-Group {
    param(
        [string]$Name,
        [string[]]$RealmRoles,
        [hashtable]$Attributes = @{}
    )

    $existing = Get-GroupByName -Name $Name
    if ($null -eq $existing) {
        Invoke-KeycloakNoContent -Method Post -Path "/admin/realms/$RealmName/groups" -Body @{ name = $Name }
        $existing = Get-GroupByName -Name $Name
        Write-Activity "Created group: $Name"
    }
    else {
        Write-Activity "Group already exists: $Name"
    }

    if ($Attributes.Count -gt 0) {
        $groupUpdateBody = @{
            name = $Name
            attributes = $Attributes
        }
        Invoke-KeycloakNoContent -Method Put -Path "/admin/realms/$RealmName/groups/$($existing.id)" -Body $groupUpdateBody
        Write-Activity "Normalized group attributes: $Name"
    }

    $existingRoles = @(Invoke-Keycloak -Method Get -Path "/admin/realms/$RealmName/groups/$($existing.id)/role-mappings/realm")
    $existingNames = @($existingRoles | Where-Object {
        $null -ne $_ -and
        $null -ne $_.PSObject.Properties["name"]
    } | ForEach-Object { $_.name })
    $missing = foreach ($roleName in $RealmRoles) {
        if ($existingNames -notcontains $roleName) {
            Get-RealmRole -RoleName $roleName
        }
    }

    $missing = @($missing | Where-Object { $null -ne $_ })
    if ($missing.Count -gt 0) {
        $body = @($missing | ForEach-Object {
            @{
                id = $_.id
                name = $_.name
                description = $_.description
                composite = $_.composite
                clientRole = $_.clientRole
                containerId = $_.containerId
            }
        })
        Invoke-KeycloakNoContent -Method Post -Path "/admin/realms/$RealmName/groups/$($existing.id)/role-mappings/realm" -Body $body
        Write-Activity "Assigned realm roles to group '$Name': $($RealmRoles -join ', ')"
    }
}

function Ensure-DefaultGroup {
    param([string]$GroupName)

    $group = Get-GroupByName -Name $GroupName
    if ($null -eq $group) {
        throw "Cannot set default group '$GroupName': group not found."
    }

    $defaultGroups = @(Invoke-Keycloak -Method Get -Path "/admin/realms/$RealmName/default-groups")
    $alreadyDefault = @($defaultGroups | Where-Object {
        $null -ne $_ -and
        $null -ne $_.PSObject.Properties["id"] -and
        $_.id -eq $group.id
    }).Count -gt 0
    if ($alreadyDefault) {
        Write-Activity "Default group already configured: $GroupName"
        return
    }

    Invoke-KeycloakNoContent -Method Put -Path "/admin/realms/$RealmName/default-groups/$($group.id)"
    Write-Activity "Configured default group: $GroupName"
}

function Ensure-UsersInGroup {
    param([string]$GroupName)

    $group = Get-GroupByName -Name $GroupName
    if ($null -eq $group) {
        throw "Cannot assign users to group '$GroupName': group not found."
    }

    $users = @(Invoke-Keycloak -Method Get -Path "/admin/realms/$RealmName/users?max=500")
    foreach ($user in $users) {
        if ($null -eq $user -or $null -eq $user.PSObject.Properties["id"] -or [string]::IsNullOrWhiteSpace([string]$user.id)) {
            continue
        }

        if ($null -ne $user.PSObject.Properties["username"] -and [string]$user.username -like "service-account-*") {
            continue
        }

        $currentGroups = @(Invoke-Keycloak -Method Get -Path "/admin/realms/$RealmName/users/$($user.id)/groups")
        $alreadyAssigned = @($currentGroups | Where-Object {
            $null -ne $_ -and
            $null -ne $_.PSObject.Properties["id"] -and
            $_.id -eq $group.id
        }).Count -gt 0
        if ($alreadyAssigned) {
            continue
        }

        Invoke-KeycloakNoContent -Method Put -Path "/admin/realms/$RealmName/users/$($user.id)/groups/$($group.id)"
        Write-Activity "Assigned user $($user.username) to default operator group '$GroupName'"
    }
}

function Get-GroupMembersByName {
    param([string]$GroupName)

    $group = Get-GroupByName -Name $GroupName
    if ($null -eq $group) {
        return @()
    }

    return @(Invoke-Keycloak -Method Get -Path "/admin/realms/$RealmName/groups/$($group.id)/members?max=500")
}

function Get-UserById {
    param([string]$UserId)

    return Invoke-Keycloak -Method Get -Path "/admin/realms/$RealmName/users/$UserId"
}

function Ensure-OperatorScopeDefaultsForUser {
    param([string]$UserId)

    $user = Get-UserById -UserId $UserId
    if ($null -eq $user) {
        return
    }

    $attributes = ConvertTo-Hashtable $user.attributes
    if ($null -eq $attributes) {
        $attributes = @{}
    }

    $changed = $false
    $tenantValues = @($attributes["tenant_id"])
    if ($tenantValues.Count -eq 0 -or [string]::IsNullOrWhiteSpace([string]$tenantValues[0])) {
        $attributes["tenant_id"] = @($DefaultOperatorTenantId)
        $changed = $true
    }

    $organizationValues = @($attributes["org_id"])
    if ($organizationValues.Count -eq 0 -or [string]::IsNullOrWhiteSpace([string]$organizationValues[0])) {
        $attributes["org_id"] = @($DefaultOperatorOrganizationId)
        $changed = $true
    }

    $principalValues = @($attributes["principal_id"])
    if ($principalValues.Count -eq 0 -or [string]::IsNullOrWhiteSpace([string]$principalValues[0])) {
        $principal = if (-not [string]::IsNullOrWhiteSpace($user.username)) { [string]$user.username } else { [string]$user.email }
        if (-not [string]::IsNullOrWhiteSpace($principal)) {
            $attributes["principal_id"] = @($principal)
            $changed = $true
        }
    }

    if (-not $changed) {
        return
    }

    $body = @{
        id = $user.id
        username = $user.username
        enabled = $user.enabled
        emailVerified = $user.emailVerified
        firstName = $user.firstName
        lastName = $user.lastName
        email = $user.email
        attributes = $attributes
    }

    Invoke-KeycloakNoContent -Method Put -Path "/admin/realms/$RealmName/users/$($user.id)" -Body $body
    Write-Activity "Normalized operator defaults for user: $($user.username)"
}

function Ensure-OperatorOnboardingDefaults {
    $operatorMembers = @(Get-GroupMembersByName -GroupName "hidbridge-operators")
    $adminMembers = @(Get-GroupMembersByName -GroupName "hidbridge-admins")
    $candidateIds = @(($operatorMembers + $adminMembers) | Where-Object {
        $null -ne $_ -and
        $null -ne $_.PSObject.Properties["id"] -and
        -not [string]::IsNullOrWhiteSpace([string]$_.id)
    } | ForEach-Object { [string]$_.id } | Sort-Object -Unique)

    foreach ($userId in $candidateIds) {
        Ensure-OperatorScopeDefaultsForUser -UserId $userId
    }
}

$script:AdminToken = Get-AdminToken
Write-Activity "Authenticated against Keycloak admin API."

$currentRealm = Get-Realm
if ($null -eq $currentRealm) {
    Invoke-KeycloakNoContent -Method Post -Path "/admin/realms" -Body (ConvertTo-Hashtable $desiredRealm)
    Write-Activity "Created realm from import: $RealmName"
}
else {
    Backup-Realm -IncludeUsers:$IncludeUsersInBackup
    Ensure-RealmSettings
}

foreach ($role in @($desiredRealm.roles.realm)) {
    Ensure-RealmRole -Role $role
}

foreach ($scope in @($desiredRealm.clientScopes)) {
    Ensure-ClientScope -Scope $scope | Out-Null
}

foreach ($client in @($desiredRealm.clients)) {
    Ensure-Client -Client $client | Out-Null
}

foreach ($providerConfigPath in @(Resolve-ExternalProviderConfigPaths)) {
    Ensure-IdentityProviderFromConfig -ConfigPath $providerConfigPath
}

Ensure-Group -Name "hidbridge-operators" -RealmRoles @("operator.viewer") -Attributes @{
    tenant_id = @($DefaultOperatorTenantId)
    org_id = @($DefaultOperatorOrganizationId)
}
Ensure-Group -Name "hidbridge-admins" -RealmRoles @("operator.viewer", "operator.moderator", "operator.admin") -Attributes @{
    tenant_id = @($DefaultOperatorTenantId)
    org_id = @($DefaultOperatorOrganizationId)
}
Ensure-DefaultGroup -GroupName "hidbridge-operators"
Ensure-UsersInGroup -GroupName "hidbridge-operators"

if (-not $SkipUsers) {
    foreach ($user in @($desiredRealm.users)) {
        Ensure-User -User $user
    }
}

if (-not $SkipGroups) {
    Ensure-Group -Name "hidbridge-operators" -RealmRoles @("operator.viewer") -Attributes @{
        tenant_id = @($DefaultOperatorTenantId)
        org_id = @($DefaultOperatorOrganizationId)
    }
    Ensure-Group -Name "hidbridge-admins" -RealmRoles @("operator.viewer", "operator.moderator", "operator.admin") -Attributes @{
        tenant_id = @($DefaultOperatorTenantId)
        org_id = @($DefaultOperatorOrganizationId)
    }
}

Ensure-OperatorOnboardingDefaults

$activity | Set-Content -Path $activityLogPath -Encoding UTF8

Write-Host ""
Write-Host "=== Keycloak Realm Sync Summary ==="
Write-Host "Realm: $RealmName"
Write-Host "Import: $ImportPath"
Write-Host "Backup: $(if (Test-Path $backupPath) { $backupPath } else { 'not-created (realm was missing)' })"
Write-Host "Activity log: $activityLogPath"

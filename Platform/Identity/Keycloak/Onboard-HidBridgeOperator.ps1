[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$KeycloakBaseUrl = "http://127.0.0.1:18096",
    [string]$AdminRealm = "master",
    [string]$AdminUser = "",
    [string]$AdminPassword = "",
    [string]$RealmName = "hidbridge-dev",
    [string]$UserId = "",
    [string]$Username = "",
    [string]$Email = "",
    [string]$GroupName = "hidbridge-operators",
    [string[]]$RequiredRealmRoles = @("operator.viewer"),
    [string]$TenantId = "local-tenant",
    [string]$OrganizationId = "local-org",
    [string]$PrincipalId = "",
    [Alias("DryRun")]
    [switch]$PrintOnly,
    [string]$OutputJsonPath = ""
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$commonPath = Join-Path (Join-Path (Split-Path -Parent (Split-Path -Parent $PSScriptRoot)) "Scripts") "Common/KeycloakCommon.ps1"
. $commonPath

function Resolve-AdminUser {
    param([string]$Value)

    if (-not [string]::IsNullOrWhiteSpace($Value)) {
        return $Value
    }

    if (-not [string]::IsNullOrWhiteSpace($env:HIDBRIDGE_KEYCLOAK_ADMIN_USER)) {
        return $env:HIDBRIDGE_KEYCLOAK_ADMIN_USER
    }

    if (-not [string]::IsNullOrWhiteSpace($env:KEYCLOAK_ADMIN)) {
        return $env:KEYCLOAK_ADMIN
    }

    return "admin"
}

function Resolve-AdminPassword {
    param([string]$Value)

    if (-not [string]::IsNullOrWhiteSpace($Value)) {
        return $Value
    }

    if (-not [string]::IsNullOrWhiteSpace($env:HIDBRIDGE_KEYCLOAK_ADMIN_PASSWORD)) {
        return $env:HIDBRIDGE_KEYCLOAK_ADMIN_PASSWORD
    }

    if (-not [string]::IsNullOrWhiteSpace($env:KEYCLOAK_ADMIN_PASSWORD)) {
        return $env:KEYCLOAK_ADMIN_PASSWORD
    }

    return "1q-p2w0o"
}

function Get-ObjectPropertyValue {
    param(
        [object]$Object,
        [string]$PropertyName
    )

    if ($null -eq $Object -or [string]::IsNullOrWhiteSpace($PropertyName)) {
        return $null
    }

    $property = $Object.PSObject.Properties[$PropertyName]
    if ($null -eq $property) {
        return $null
    }

    return $property.Value
}

function Get-SafeCount {
    param([object]$Value)

    if ($null -eq $Value) {
        return 0
    }

    return @($Value).Count
}

function Get-DesiredUserAttributes {
    param(
        [object]$User,
        [string]$DesiredTenantId,
        [string]$DesiredOrganizationId,
        [string]$DesiredPrincipalId
    )

    $attributes = ConvertTo-KeycloakAttributeHashtable -Attributes (Get-ObjectPropertyValue -Object $User -PropertyName "attributes")
    $desired = @{}
    foreach ($entry in $attributes.GetEnumerator()) {
        $desired[[string]$entry.Key] = @(ConvertTo-KeycloakStringArray -Value $entry.Value)
    }

    $resolvedPrincipalId = $DesiredPrincipalId
    if ([string]::IsNullOrWhiteSpace($resolvedPrincipalId)) {
        if ($User.PSObject.Properties["username"] -and -not [string]::IsNullOrWhiteSpace([string]$User.username)) {
            $resolvedPrincipalId = [string]$User.username
        }
        elseif ($User.PSObject.Properties["email"] -and -not [string]::IsNullOrWhiteSpace([string]$User.email)) {
            $resolvedPrincipalId = [string]$User.email
        }
    }

    if (-not [string]::IsNullOrWhiteSpace($DesiredTenantId)) {
        $desired["tenant_id"] = @($DesiredTenantId)
    }

    if (-not [string]::IsNullOrWhiteSpace($DesiredOrganizationId)) {
        $desired["org_id"] = @($DesiredOrganizationId)
    }

    if (-not [string]::IsNullOrWhiteSpace($resolvedPrincipalId)) {
        $desired["principal_id"] = @($resolvedPrincipalId)
    }

    return $desired
}

function Get-AttributeChangeKeys {
    param(
        [hashtable]$Current,
        [hashtable]$Desired
    )

    $keys = @($Current.Keys + $Desired.Keys | Sort-Object -Unique)
    $changed = @()
    foreach ($key in $keys) {
        $left = @{ $key = @(ConvertTo-KeycloakStringArray -Value $Current[$key]) }
        $right = @{ $key = @(ConvertTo-KeycloakStringArray -Value $Desired[$key]) }
        if (-not (Test-KeycloakAttributeHashtableEqual -Left $left -Right $right)) {
            $changed += ,$key
        }
    }

    return @($changed)
}

if ([string]::IsNullOrWhiteSpace($UserId) -and [string]::IsNullOrWhiteSpace($Username) -and [string]::IsNullOrWhiteSpace($Email)) {
    throw "Specify one of -UserId, -Username, or -Email."
}

$resolvedAdminUser = Resolve-AdminUser -Value $AdminUser
$resolvedAdminPassword = Resolve-AdminPassword -Value $AdminPassword

$adminToken = Get-KeycloakAdminToken `
    -BaseUrl $KeycloakBaseUrl `
    -AdminRealm $AdminRealm `
    -AdminUser $resolvedAdminUser `
    -AdminPassword $resolvedAdminPassword

$resolvedUser = Resolve-KeycloakUser `
    -BaseUrl $KeycloakBaseUrl `
    -AccessToken $adminToken `
    -RealmName $RealmName `
    -UserId $UserId `
    -Username $Username `
    -Email $Email

if ($null -eq $resolvedUser) {
    $lookupValue = if (-not [string]::IsNullOrWhiteSpace($Email)) {
        $Email
    }
    elseif (-not [string]::IsNullOrWhiteSpace($Username)) {
        $Username
    }
    else {
        $UserId
    }

    $suggestions = @()
    if (-not [string]::IsNullOrWhiteSpace($lookupValue)) {
        try {
            $candidates = Get-KeycloakCollectionItems -Response (
                Invoke-KeycloakAdminJson `
                    -BaseUrl $KeycloakBaseUrl `
                    -AccessToken $adminToken `
                    -Method Get `
                    -Path "/admin/realms/$RealmName/users?search=$([Uri]::EscapeDataString($lookupValue))"
            )
            $suggestions = @(
                $candidates |
                    Where-Object { $_ -and $_.PSObject.Properties["id"] } |
                    Select-Object -First 5 |
                    ForEach-Object {
                        $u = if ($_.PSObject.Properties["username"]) { [string]$_.username } else { "" }
                        $e = if ($_.PSObject.Properties["email"]) { [string]$_.email } else { "" }
                        if (-not [string]::IsNullOrWhiteSpace($u) -or -not [string]::IsNullOrWhiteSpace($e)) {
                            "$u <$e>"
                        }
                    } |
                    Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
            )
        }
        catch {
            $suggestions = @()
        }
    }

    if ((Get-SafeCount -Value $suggestions) -gt 0) {
        throw "Operator user was not found in realm '$RealmName'. Search candidates: $($suggestions -join '; ')"
    }

    throw "Operator user was not found in realm '$RealmName'."
}

$resolvedUser = Get-KeycloakUserById `
    -BaseUrl $KeycloakBaseUrl `
    -AccessToken $adminToken `
    -RealmName $RealmName `
    -UserId ([string]$resolvedUser.id)

if ($null -eq $resolvedUser -or -not $resolvedUser.PSObject.Properties["id"]) {
    throw "Failed to load full user representation for onboarding."
}

$desiredContract = [ordered]@{
    realm = $RealmName
    user = [ordered]@{
        id = [string]$resolvedUser.id
        username = if ($resolvedUser.PSObject.Properties["username"]) { [string]$resolvedUser.username } else { "" }
        email = if ($resolvedUser.PSObject.Properties["email"]) { [string]$resolvedUser.email } else { "" }
    }
    identityDefaults = [ordered]@{
        tenant_id = $TenantId
        org_id = $OrganizationId
        principal_id = if (-not [string]::IsNullOrWhiteSpace($PrincipalId)) { $PrincipalId } elseif ($resolvedUser.PSObject.Properties["username"]) { [string]$resolvedUser.username } else { [string](Get-ObjectPropertyValue -Object $resolvedUser -PropertyName "email") }
    }
    groupAssignment = [ordered]@{
        name = $GroupName
        requiredRealmRoles = @($RequiredRealmRoles | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique)
    }
}

$groupLookup = Find-KeycloakGroupByName `
    -BaseUrl $KeycloakBaseUrl `
    -AccessToken $adminToken `
    -RealmName $RealmName `
    -GroupName $GroupName

$groupExists = $null -ne $groupLookup
$fullGroup = if ($groupExists) {
    Get-KeycloakGroupById `
        -BaseUrl $KeycloakBaseUrl `
        -AccessToken $adminToken `
        -RealmName $RealmName `
        -GroupId ([string]$groupLookup.id)
}
else {
    $null
}

$desiredGroupAttributes = @{}
if (-not [string]::IsNullOrWhiteSpace($TenantId)) {
    $desiredGroupAttributes["tenant_id"] = @($TenantId)
}

if (-not [string]::IsNullOrWhiteSpace($OrganizationId)) {
    $desiredGroupAttributes["org_id"] = @($OrganizationId)
}

$currentGroupAttributes = if ($null -ne $fullGroup) {
    ConvertTo-KeycloakAttributeHashtable -Attributes (Get-ObjectPropertyValue -Object $fullGroup -PropertyName "attributes")
}
else {
    @{}
}
$groupAttributesNeedUpdate = $false
if ($desiredGroupAttributes.Count -gt 0) {
    $groupAttributesNeedUpdate = (-not $groupExists) -or (-not (Test-KeycloakAttributeHashtableEqual -Left $currentGroupAttributes -Right $desiredGroupAttributes))
}

$missingGroupRoles = @()
if (-not $groupExists) {
    $missingGroupRoles = @($desiredContract.groupAssignment.requiredRealmRoles)
}
else {
    $existingGroupRoles = @(Get-KeycloakCollectionItems -Response (
        Invoke-KeycloakAdminJson `
            -BaseUrl $KeycloakBaseUrl `
            -AccessToken $adminToken `
            -Method Get `
            -Path "/admin/realms/$RealmName/groups/$([Uri]::EscapeDataString([string]$fullGroup.id))/role-mappings/realm"
    )) | Where-Object { $_ -and $_.PSObject.Properties["name"] } | ForEach-Object { [string]$_.name }

    foreach ($roleName in @($desiredContract.groupAssignment.requiredRealmRoles)) {
        if ($existingGroupRoles -notcontains $roleName) {
            $missingGroupRoles += ,$roleName
        }
    }
}

$currentUserAttributes = ConvertTo-KeycloakAttributeHashtable -Attributes (Get-ObjectPropertyValue -Object $resolvedUser -PropertyName "attributes")
$desiredUserAttributes = Get-DesiredUserAttributes `
    -User $resolvedUser `
    -DesiredTenantId $TenantId `
    -DesiredOrganizationId $OrganizationId `
    -DesiredPrincipalId $PrincipalId
$changedUserAttributeKeys = @(Get-AttributeChangeKeys -Current $currentUserAttributes -Desired $desiredUserAttributes)
$userAttributesNeedUpdate = (Get-SafeCount -Value $changedUserAttributeKeys) -gt 0

$userInGroup = $false
$userMembershipNeedsUpdate = $true
if ($groupExists) {
    $userInGroup = Test-KeycloakUserInGroup `
        -BaseUrl $KeycloakBaseUrl `
        -AccessToken $adminToken `
        -RealmName $RealmName `
        -UserId ([string]$resolvedUser.id) `
        -GroupId ([string]$fullGroup.id)
    $userMembershipNeedsUpdate = -not $userInGroup
}

$plan = [ordered]@{
    createGroup = -not $groupExists
    updateGroupAttributes = $groupAttributesNeedUpdate
    assignGroupRealmRoles = @($missingGroupRoles)
    updateUserAttributes = $userAttributesNeedUpdate
    changedUserAttributeKeys = @($changedUserAttributeKeys)
    addUserToGroup = $userMembershipNeedsUpdate
}

$applied = [ordered]@{
    groupCreated = $false
    groupAttributesUpdated = $false
    groupRolesAssigned = @()
    userAttributesUpdated = $false
    userAddedToGroup = $false
}

$changesPlanned = $plan.createGroup -or $plan.updateGroupAttributes -or ((Get-SafeCount -Value $plan.assignGroupRealmRoles) -gt 0) -or $plan.updateUserAttributes -or $plan.addUserToGroup

$skipApply = $PrintOnly.IsPresent
if ($skipApply) {
    Write-Host "PrintOnly mode enabled. No Keycloak updates will be applied."
}

$effectiveGroup = $fullGroup
if (-not $skipApply -and $changesPlanned) {
    if ($plan.createGroup -or $plan.updateGroupAttributes -or (Get-SafeCount -Value $plan.assignGroupRealmRoles) -gt 0) {
        if ($PSCmdlet.ShouldProcess("Group '$GroupName'", "Ensure group, attributes, and realm role mapping")) {
            $groupResult = Ensure-KeycloakGroup `
                -BaseUrl $KeycloakBaseUrl `
                -AccessToken $adminToken `
                -RealmName $RealmName `
                -GroupName $GroupName `
                -RealmRoleNames @($desiredContract.groupAssignment.requiredRealmRoles) `
                -Attributes $desiredGroupAttributes

            $effectiveGroup = $groupResult.Group
            $applied.groupCreated = [bool]$groupResult.Created
            $applied.groupAttributesUpdated = [bool]$groupResult.AttributesUpdated
            $applied.groupRolesAssigned = @($groupResult.AssignedRealmRoles)
        }
    }

    if ($plan.updateUserAttributes) {
        # Keep onboarding update minimal: only attributes are required here.
        # This avoids 400 errors for IdP-managed users where full user PUT can be rejected.
        $updateBody = @{
            attributes = $desiredUserAttributes
        }

        if ($PSCmdlet.ShouldProcess("User '$([string]$resolvedUser.id)'", "Update operator attributes")) {
            Invoke-KeycloakAdminNoContent `
                -BaseUrl $KeycloakBaseUrl `
                -AccessToken $adminToken `
                -Method Put `
                -Path "/admin/realms/$RealmName/users/$([Uri]::EscapeDataString([string]$resolvedUser.id))" `
                -Body $updateBody
            $applied.userAttributesUpdated = $true
            $resolvedUser = Get-KeycloakUserById `
                -BaseUrl $KeycloakBaseUrl `
                -AccessToken $adminToken `
                -RealmName $RealmName `
                -UserId ([string]$resolvedUser.id)
        }
    }

    if ($plan.addUserToGroup) {
        if ($null -eq $effectiveGroup -or -not $effectiveGroup.PSObject.Properties["id"]) {
            throw "Cannot assign user to group '$GroupName': group was not resolved."
        }

        if ($PSCmdlet.ShouldProcess("User '$([string]$resolvedUser.id)'", "Assign to group '$GroupName'")) {
            $added = Ensure-KeycloakUserInGroup `
                -BaseUrl $KeycloakBaseUrl `
                -AccessToken $adminToken `
                -RealmName $RealmName `
                -UserId ([string]$resolvedUser.id) `
                -GroupId ([string]$effectiveGroup.id)
            $applied.userAddedToGroup = [bool]$added
        }
    }
}

$summary = [ordered]@{
    contract = $desiredContract
    plan = $plan
    mode = [ordered]@{
        printOnly = $PrintOnly.IsPresent
        whatIf = [bool]$WhatIfPreference
    }
    applied = $applied
}

Write-Host ""
Write-Host "=== Operator Onboarding Summary ==="
Write-Host "Realm: $RealmName"
Write-Host "User: $($desiredContract.user.username) ($($desiredContract.user.id))"
Write-Host "Group: $GroupName"
Write-Host "Required roles: $(@($desiredContract.groupAssignment.requiredRealmRoles) -join ', ')"
Write-Host "Planned group create: $($plan.createGroup)"
Write-Host "Planned group attribute update: $($plan.updateGroupAttributes)"
Write-Host "Planned missing group roles: $(@($plan.assignGroupRealmRoles) -join ', ')"
Write-Host "Planned user attribute update: $($plan.updateUserAttributes)"
Write-Host "Planned user attribute keys: $(@($plan.changedUserAttributeKeys) -join ', ')"
Write-Host "Planned group membership add: $($plan.addUserToGroup)"
Write-Host "Applied group create: $($applied.groupCreated)"
Write-Host "Applied group attribute update: $($applied.groupAttributesUpdated)"
Write-Host "Applied group roles: $(@($applied.groupRolesAssigned) -join ', ')"
Write-Host "Applied user attribute update: $($applied.userAttributesUpdated)"
Write-Host "Applied group membership add: $($applied.userAddedToGroup)"

if (-not [string]::IsNullOrWhiteSpace($OutputJsonPath)) {
    $outputDir = Split-Path -Parent $OutputJsonPath
    if (-not [string]::IsNullOrWhiteSpace($outputDir)) {
        New-Item -ItemType Directory -Force -Path $outputDir | Out-Null
    }

    $summary | ConvertTo-Json -Depth 30 | Set-Content -Path $OutputJsonPath -Encoding UTF8
    Write-Host "Summary JSON: $OutputJsonPath"
}

exit 0

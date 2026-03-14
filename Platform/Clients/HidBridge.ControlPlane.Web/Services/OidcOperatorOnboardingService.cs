using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using HidBridge.ControlPlane.Web.Configuration;
using HidBridge.ControlPlane.Web.Identity;
using Microsoft.Extensions.Options;

namespace HidBridge.ControlPlane.Web.Services;

/// <summary>
/// Performs automatic Keycloak operator onboarding for freshly authenticated OIDC principals.
/// </summary>
public sealed class OidcOperatorOnboardingService
{
    private readonly IdentityOptions _identityOptions;
    private readonly ILogger<OidcOperatorOnboardingService> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="OidcOperatorOnboardingService"/> class.
    /// </summary>
    public OidcOperatorOnboardingService(
        IOptions<IdentityOptions> identityOptions,
        ILogger<OidcOperatorOnboardingService> logger)
    {
        _identityOptions = identityOptions.Value;
        _logger = logger;
    }

    /// <summary>
    /// Tries to auto-onboard one authenticated OIDC principal in Keycloak.
    /// </summary>
    public async Task<OidcOperatorOnboardingResult?> TryEnsureOnboardingAsync(
        ClaimsPrincipal principal,
        CancellationToken cancellationToken)
    {
        var onboarding = _identityOptions.Onboarding;
        if (!_identityOptions.Enabled || !onboarding.Enabled)
        {
            return null;
        }

        var adminUser = ResolveAdminUser(onboarding);
        var adminPassword = ResolveAdminPassword(onboarding);
        if (string.IsNullOrWhiteSpace(adminUser) || string.IsNullOrWhiteSpace(adminPassword))
        {
            _logger.LogWarning("OIDC onboarding skipped: admin credentials are not configured.");
            return null;
        }

        try
        {
            using var client = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(15),
            };

            var adminToken = await GetAdminAccessTokenAsync(
                client,
                onboarding.KeycloakBaseUrl,
                onboarding.AdminRealm,
                adminUser,
                adminPassword,
                cancellationToken);

            var user = await ResolveUserAsync(
                client,
                onboarding,
                adminToken,
                principal,
                cancellationToken);

            if (user is null)
            {
                _logger.LogWarning("OIDC onboarding skipped: principal user was not found in realm {RealmName}.", onboarding.RealmName);
                return null;
            }

            var desiredAttributes = BuildDesiredUserAttributes(onboarding, user);
            var group = await EnsureOperatorGroupAsync(client, onboarding, adminToken, cancellationToken);
            var groupMembershipAdded = await EnsureUserInGroupAsync(client, onboarding, adminToken, user.Id, group.Id, cancellationToken);
            var userAttributesUpdated = await EnsureUserAttributesAsync(client, onboarding, adminToken, user.Id, user.Attributes, desiredAttributes, cancellationToken);

            EnsurePrincipalClaims(principal, onboarding, desiredAttributes);

            return new OidcOperatorOnboardingResult(
                user.Id,
                user.Username,
                user.Email,
                desiredAttributes,
                groupMembershipAdded,
                userAttributesUpdated);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "OIDC onboarding failed. Login flow continues without hard failure.");
            return null;
        }
    }

    private static string ResolveAdminUser(IdentityOnboardingOptions onboarding)
    {
        if (!string.IsNullOrWhiteSpace(onboarding.AdminUser))
        {
            return onboarding.AdminUser;
        }

        return Environment.GetEnvironmentVariable("HIDBRIDGE_KEYCLOAK_ADMIN_USER")
            ?? Environment.GetEnvironmentVariable("KEYCLOAK_ADMIN")
            ?? "admin";
    }

    private static string ResolveAdminPassword(IdentityOnboardingOptions onboarding)
    {
        if (!string.IsNullOrWhiteSpace(onboarding.AdminPassword))
        {
            return onboarding.AdminPassword;
        }

        return Environment.GetEnvironmentVariable("HIDBRIDGE_KEYCLOAK_ADMIN_PASSWORD")
            ?? Environment.GetEnvironmentVariable("KEYCLOAK_ADMIN_PASSWORD")
            ?? "1q-p2w0o";
    }

    private static async Task<string> GetAdminAccessTokenAsync(
        HttpClient client,
        string baseUrl,
        string adminRealm,
        string adminUser,
        string adminPassword,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{baseUrl.TrimEnd('/')}/realms/{Uri.EscapeDataString(adminRealm)}/protocol/openid-connect/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "password",
                ["client_id"] = "admin-cli",
                ["username"] = adminUser,
                ["password"] = adminPassword,
            }),
        };

        using var response = await client.SendAsync(request, cancellationToken);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Keycloak admin auth failed with {(int)response.StatusCode}: {payload}");
        }

        using var document = JsonDocument.Parse(payload);
        var token = GetStringProperty(document.RootElement, "access_token");
        if (string.IsNullOrWhiteSpace(token))
        {
            throw new InvalidOperationException("Keycloak admin auth response did not include access_token.");
        }

        return token;
    }

    private static async Task<KeycloakUser?> ResolveUserAsync(
        HttpClient client,
        IdentityOnboardingOptions onboarding,
        string adminToken,
        ClaimsPrincipal principal,
        CancellationToken cancellationToken)
    {
        var subjectId = principal.FindFirstValue("sub") ?? principal.FindFirstValue(ClaimTypes.NameIdentifier);
        var preferredUsername = principal.FindFirstValue("preferred_username");
        var email = principal.FindFirstValue(ClaimTypes.Email) ?? principal.FindFirstValue("email");

        if (!string.IsNullOrWhiteSpace(subjectId))
        {
            var byId = await TryGetUserByIdAsync(client, onboarding, adminToken, subjectId, cancellationToken);
            if (byId is not null)
            {
                return byId;
            }
        }

        foreach (var candidate in new[] { preferredUsername, email })
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            var byUsername = await TryFindUserByUsernameAsync(client, onboarding, adminToken, candidate, cancellationToken);
            if (byUsername is not null)
            {
                return byUsername;
            }
        }

        if (!string.IsNullOrWhiteSpace(email))
        {
            var byEmail = await TryFindUserByEmailAsync(client, onboarding, adminToken, email, cancellationToken);
            if (byEmail is not null)
            {
                return byEmail;
            }
        }

        return null;
    }

    private static async Task<KeycloakGroup> EnsureOperatorGroupAsync(
        HttpClient client,
        IdentityOnboardingOptions onboarding,
        string adminToken,
        CancellationToken cancellationToken)
    {
        var group = await TryFindGroupByNameAsync(client, onboarding, adminToken, onboarding.GroupName, cancellationToken);
        if (group is null)
        {
            await SendAdminNoContentAsync(
                client,
                onboarding,
                adminToken,
                HttpMethod.Post,
                $"/admin/realms/{Uri.EscapeDataString(onboarding.RealmName)}/groups",
                new { name = onboarding.GroupName },
                cancellationToken,
                HttpStatusCode.Conflict);

            group = await TryFindGroupByNameAsync(client, onboarding, adminToken, onboarding.GroupName, cancellationToken);
            if (group is null)
            {
                throw new InvalidOperationException($"Failed to resolve group '{onboarding.GroupName}' after creation.");
            }
        }

        var desiredGroupAttributes = new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["tenant_id"] = [onboarding.DefaultTenantId],
            ["org_id"] = [onboarding.DefaultOrganizationId],
        };

        if (!AttributesEqual(group.Attributes, desiredGroupAttributes))
        {
            await SendAdminNoContentAsync(
                client,
                onboarding,
                adminToken,
                HttpMethod.Put,
                $"/admin/realms/{Uri.EscapeDataString(onboarding.RealmName)}/groups/{Uri.EscapeDataString(group.Id)}",
                new
                {
                    name = group.Name,
                    attributes = desiredGroupAttributes,
                },
                cancellationToken);
        }

        await EnsureGroupRealmRolesAsync(client, onboarding, adminToken, group.Id, cancellationToken);

        return group;
    }

    private static async Task EnsureGroupRealmRolesAsync(
        HttpClient client,
        IdentityOnboardingOptions onboarding,
        string adminToken,
        string groupId,
        CancellationToken cancellationToken)
    {
        var existingRolesElement = await SendAdminJsonAsync(
            client,
            onboarding,
            adminToken,
            HttpMethod.Get,
            $"/admin/realms/{Uri.EscapeDataString(onboarding.RealmName)}/groups/{Uri.EscapeDataString(groupId)}/role-mappings/realm",
            null,
            cancellationToken);

        var existingRoleNames = EnumerateItems(existingRolesElement)
            .Select(item => GetStringProperty(item, "name"))
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var missingRolePayload = new List<object>();
        foreach (var roleName in onboarding.RequiredRealmRoles ?? [])
        {
            if (string.IsNullOrWhiteSpace(roleName) || existingRoleNames.Contains(roleName, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            var roleElement = await SendAdminJsonAsync(
                client,
                onboarding,
                adminToken,
                HttpMethod.Get,
                $"/admin/realms/{Uri.EscapeDataString(onboarding.RealmName)}/roles/{Uri.EscapeDataString(roleName)}",
                null,
                cancellationToken);

            var roleId = GetStringProperty(roleElement, "id");
            if (string.IsNullOrWhiteSpace(roleId))
            {
                continue;
            }

            missingRolePayload.Add(new
            {
                id = roleId,
                name = GetStringProperty(roleElement, "name") ?? roleName,
                description = GetStringProperty(roleElement, "description"),
                composite = GetBoolProperty(roleElement, "composite"),
                clientRole = GetBoolProperty(roleElement, "clientRole"),
                containerId = GetStringProperty(roleElement, "containerId"),
            });
        }

        if (missingRolePayload.Count == 0)
        {
            return;
        }

        await SendAdminNoContentAsync(
            client,
            onboarding,
            adminToken,
            HttpMethod.Post,
            $"/admin/realms/{Uri.EscapeDataString(onboarding.RealmName)}/groups/{Uri.EscapeDataString(groupId)}/role-mappings/realm",
            missingRolePayload,
            cancellationToken,
            HttpStatusCode.Conflict);
    }

    private static async Task<bool> EnsureUserAttributesAsync(
        HttpClient client,
        IdentityOnboardingOptions onboarding,
        string adminToken,
        string userId,
        IReadOnlyDictionary<string, string[]> existingAttributes,
        IReadOnlyDictionary<string, string[]> desiredAttributes,
        CancellationToken cancellationToken)
    {
        if (AttributesEqual(existingAttributes, desiredAttributes))
        {
            return false;
        }

        await SendAdminNoContentAsync(
            client,
            onboarding,
            adminToken,
            HttpMethod.Put,
            $"/admin/realms/{Uri.EscapeDataString(onboarding.RealmName)}/users/{Uri.EscapeDataString(userId)}",
            new
            {
                attributes = desiredAttributes,
            },
            cancellationToken);

        return true;
    }

    private static async Task<bool> EnsureUserInGroupAsync(
        HttpClient client,
        IdentityOnboardingOptions onboarding,
        string adminToken,
        string userId,
        string groupId,
        CancellationToken cancellationToken)
    {
        var groupsElement = await SendAdminJsonAsync(
            client,
            onboarding,
            adminToken,
            HttpMethod.Get,
            $"/admin/realms/{Uri.EscapeDataString(onboarding.RealmName)}/users/{Uri.EscapeDataString(userId)}/groups",
            null,
            cancellationToken);

        var alreadyInGroup = EnumerateItems(groupsElement)
            .Select(item => GetStringProperty(item, "id"))
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Any(id => string.Equals(id, groupId, StringComparison.Ordinal));

        if (alreadyInGroup)
        {
            return false;
        }

        await SendAdminNoContentAsync(
            client,
            onboarding,
            adminToken,
            HttpMethod.Put,
            $"/admin/realms/{Uri.EscapeDataString(onboarding.RealmName)}/users/{Uri.EscapeDataString(userId)}/groups/{Uri.EscapeDataString(groupId)}",
            null,
            cancellationToken,
            HttpStatusCode.Conflict);

        return true;
    }

    private void EnsurePrincipalClaims(
        ClaimsPrincipal principal,
        IdentityOnboardingOptions onboarding,
        IReadOnlyDictionary<string, string[]> desiredAttributes)
    {
        if (principal.Identity is not ClaimsIdentity identity)
        {
            return;
        }

        var tenant = FirstAttributeValue(desiredAttributes, "tenant_id");
        var organization = FirstAttributeValue(desiredAttributes, "org_id");
        var principalId = FirstAttributeValue(desiredAttributes, "principal_id");

        UpsertClaim(identity, _identityOptions.TenantClaimType, tenant);
        UpsertClaim(identity, OperatorClaimTypes.TenantId, tenant);
        UpsertClaim(identity, _identityOptions.OrganizationClaimType, organization);
        UpsertClaim(identity, OperatorClaimTypes.OrganizationId, organization);
        UpsertClaim(identity, _identityOptions.PrincipalClaimType, principalId);
        UpsertClaim(identity, OperatorClaimTypes.PrincipalId, principalId);

        if (!onboarding.InjectViewerRoleClaimWhenMissing)
        {
            return;
        }

        var hasOperatorRole = principal.FindAll(_identityOptions.RoleClaimType)
            .Concat(principal.FindAll(ClaimTypes.Role))
            .Select(static claim => claim.Value)
            .Any(static value => !string.IsNullOrWhiteSpace(value) && value.StartsWith("operator.", StringComparison.OrdinalIgnoreCase));

        if (!hasOperatorRole)
        {
            identity.AddClaim(new Claim(_identityOptions.RoleClaimType, "operator.viewer"));
            identity.AddClaim(new Claim(ClaimTypes.Role, "operator.viewer"));
        }
    }

    private static void UpsertClaim(ClaimsIdentity identity, string claimType, string? value)
    {
        if (string.IsNullOrWhiteSpace(claimType) || string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var existingClaims = identity.FindAll(claimType).ToArray();
        if (existingClaims.Any(claim => string.Equals(claim.Value, value, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        foreach (var claim in existingClaims)
        {
            identity.RemoveClaim(claim);
        }

        identity.AddClaim(new Claim(claimType, value));
    }

    private static IReadOnlyDictionary<string, string[]> BuildDesiredUserAttributes(IdentityOnboardingOptions onboarding, KeycloakUser user)
    {
        var result = new Dictionary<string, string[]>(user.Attributes, StringComparer.Ordinal);

        if (!string.IsNullOrWhiteSpace(onboarding.DefaultTenantId))
        {
            result["tenant_id"] = [onboarding.DefaultTenantId];
        }

        if (!string.IsNullOrWhiteSpace(onboarding.DefaultOrganizationId))
        {
            result["org_id"] = [onboarding.DefaultOrganizationId];
        }

        var principalId = !string.IsNullOrWhiteSpace(user.Username)
            ? user.Username
            : user.Email;
        if (!string.IsNullOrWhiteSpace(principalId))
        {
            result["principal_id"] = [principalId!];
        }

        return result;
    }

    private static string? FirstAttributeValue(IReadOnlyDictionary<string, string[]> attributes, string key)
        => attributes.TryGetValue(key, out var values)
            ? values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))
            : null;

    private static bool AttributesEqual(
        IReadOnlyDictionary<string, string[]> left,
        IReadOnlyDictionary<string, string[]> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        foreach (var (key, leftValuesRaw) in left)
        {
            if (!right.TryGetValue(key, out var rightValuesRaw))
            {
                return false;
            }

            var leftValues = leftValuesRaw.Where(static value => !string.IsNullOrWhiteSpace(value)).ToArray();
            var rightValues = rightValuesRaw.Where(static value => !string.IsNullOrWhiteSpace(value)).ToArray();
            if (leftValues.Length != rightValues.Length)
            {
                return false;
            }

            for (var index = 0; index < leftValues.Length; index++)
            {
                if (!string.Equals(leftValues[index], rightValues[index], StringComparison.Ordinal))
                {
                    return false;
                }
            }
        }

        return true;
    }

    private static async Task<KeycloakUser?> TryGetUserByIdAsync(
        HttpClient client,
        IdentityOnboardingOptions onboarding,
        string adminToken,
        string userId,
        CancellationToken cancellationToken)
    {
        var element = await SendAdminJsonAsync(
            client,
            onboarding,
            adminToken,
            HttpMethod.Get,
            $"/admin/realms/{Uri.EscapeDataString(onboarding.RealmName)}/users/{Uri.EscapeDataString(userId)}",
            null,
            cancellationToken,
            HttpStatusCode.NotFound);

        if (element.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return null;
        }

        return ParseKeycloakUser(element);
    }

    private static async Task<KeycloakUser?> TryFindUserByUsernameAsync(
        HttpClient client,
        IdentityOnboardingOptions onboarding,
        string adminToken,
        string username,
        CancellationToken cancellationToken)
    {
        var exactElement = await SendAdminJsonAsync(
            client,
            onboarding,
            adminToken,
            HttpMethod.Get,
            $"/admin/realms/{Uri.EscapeDataString(onboarding.RealmName)}/users?username={Uri.EscapeDataString(username)}&exact=true",
            null,
            cancellationToken);

        var exactUsers = EnumerateItems(exactElement)
            .Select(ParseKeycloakUser)
            .Where(static user => user is not null)
            .Cast<KeycloakUser>()
            .ToArray();

        var exactMatch = exactUsers.FirstOrDefault(user => string.Equals(user.Username, username, StringComparison.OrdinalIgnoreCase));
        if (exactMatch is not null)
        {
            return exactMatch;
        }

        var searchElement = await SendAdminJsonAsync(
            client,
            onboarding,
            adminToken,
            HttpMethod.Get,
            $"/admin/realms/{Uri.EscapeDataString(onboarding.RealmName)}/users?search={Uri.EscapeDataString(username)}",
            null,
            cancellationToken);

        return EnumerateItems(searchElement)
            .Select(ParseKeycloakUser)
            .Where(static user => user is not null)
            .Cast<KeycloakUser>()
            .FirstOrDefault(user => string.Equals(user.Username, username, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<KeycloakUser?> TryFindUserByEmailAsync(
        HttpClient client,
        IdentityOnboardingOptions onboarding,
        string adminToken,
        string email,
        CancellationToken cancellationToken)
    {
        var exactElement = await SendAdminJsonAsync(
            client,
            onboarding,
            adminToken,
            HttpMethod.Get,
            $"/admin/realms/{Uri.EscapeDataString(onboarding.RealmName)}/users?email={Uri.EscapeDataString(email)}&exact=true",
            null,
            cancellationToken);

        var exactUsers = EnumerateItems(exactElement)
            .Select(ParseKeycloakUser)
            .Where(static user => user is not null)
            .Cast<KeycloakUser>()
            .ToArray();

        var exactMatch = exactUsers.FirstOrDefault(user => string.Equals(user.Email, email, StringComparison.OrdinalIgnoreCase));
        if (exactMatch is not null)
        {
            return exactMatch;
        }

        var searchElement = await SendAdminJsonAsync(
            client,
            onboarding,
            adminToken,
            HttpMethod.Get,
            $"/admin/realms/{Uri.EscapeDataString(onboarding.RealmName)}/users?search={Uri.EscapeDataString(email)}",
            null,
            cancellationToken);

        return EnumerateItems(searchElement)
            .Select(ParseKeycloakUser)
            .Where(static user => user is not null)
            .Cast<KeycloakUser>()
            .FirstOrDefault(user =>
                string.Equals(user.Email, email, StringComparison.OrdinalIgnoreCase)
                || string.Equals(user.Username, email, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<KeycloakGroup?> TryFindGroupByNameAsync(
        HttpClient client,
        IdentityOnboardingOptions onboarding,
        string adminToken,
        string groupName,
        CancellationToken cancellationToken)
    {
        var searchElement = await SendAdminJsonAsync(
            client,
            onboarding,
            adminToken,
            HttpMethod.Get,
            $"/admin/realms/{Uri.EscapeDataString(onboarding.RealmName)}/groups?search={Uri.EscapeDataString(groupName)}",
            null,
            cancellationToken);

        var briefGroup = EnumerateItems(searchElement)
            .Select(ParseKeycloakGroup)
            .Where(static group => group is not null)
            .Cast<KeycloakGroup>()
            .FirstOrDefault(group => string.Equals(group.Name, groupName, StringComparison.OrdinalIgnoreCase));

        if (briefGroup is null)
        {
            return null;
        }

        var fullGroupElement = await SendAdminJsonAsync(
            client,
            onboarding,
            adminToken,
            HttpMethod.Get,
            $"/admin/realms/{Uri.EscapeDataString(onboarding.RealmName)}/groups/{Uri.EscapeDataString(briefGroup.Id)}",
            null,
            cancellationToken,
            HttpStatusCode.NotFound);

        if (fullGroupElement.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return briefGroup;
        }

        return ParseKeycloakGroup(fullGroupElement) ?? briefGroup;
    }

    private static KeycloakUser? ParseKeycloakUser(JsonElement element)
    {
        var id = GetStringProperty(element, "id");
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        var username = GetStringProperty(element, "username") ?? string.Empty;
        var email = GetStringProperty(element, "email") ?? string.Empty;
        var attributes = ParseAttributes(element, "attributes");

        return new KeycloakUser(id, username, email, attributes);
    }

    private static KeycloakGroup? ParseKeycloakGroup(JsonElement element)
    {
        var id = GetStringProperty(element, "id");
        var name = GetStringProperty(element, "name");
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var attributes = ParseAttributes(element, "attributes");
        return new KeycloakGroup(id, name, attributes);
    }

    private static IReadOnlyDictionary<string, string[]> ParseAttributes(JsonElement element, string propertyName)
    {
        if (!TryGetPropertyIgnoreCase(element, propertyName, out var attributesElement)
            || attributesElement.ValueKind != JsonValueKind.Object)
        {
            return new Dictionary<string, string[]>(StringComparer.Ordinal);
        }

        var result = new Dictionary<string, string[]>(StringComparer.Ordinal);
        foreach (var property in attributesElement.EnumerateObject())
        {
            var values = property.Value.ValueKind switch
            {
                JsonValueKind.Array => property.Value.EnumerateArray()
                    .Select(static value => value.ValueKind == JsonValueKind.String ? value.GetString() : value.ToString())
                    .Where(static value => !string.IsNullOrWhiteSpace(value))
                    .Select(static value => value!)
                    .ToArray(),
                JsonValueKind.String => [property.Value.GetString()!],
                _ => [property.Value.ToString()],
            };

            result[property.Name] = values;
        }

        return result;
    }

    private static string? GetStringProperty(JsonElement element, string propertyName)
    {
        if (!TryGetPropertyIgnoreCase(element, propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => bool.TrueString,
            JsonValueKind.False => bool.FalseString,
            _ => null,
        };
    }

    private static bool GetBoolProperty(JsonElement element, string propertyName)
    {
        if (!TryGetPropertyIgnoreCase(element, propertyName, out var value))
        {
            return false;
        }

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var parsed) => parsed,
            _ => false,
        };
    }

    private static bool TryGetPropertyIgnoreCase(JsonElement element, string propertyName, out JsonElement value)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static IEnumerable<JsonElement> EnumerateItems(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                yield return item;
            }

            yield break;
        }

        if (element.ValueKind == JsonValueKind.Object
            && TryGetPropertyIgnoreCase(element, "items", out var itemsElement)
            && itemsElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in itemsElement.EnumerateArray())
            {
                yield return item;
            }

            yield break;
        }

        if (element.ValueKind != JsonValueKind.Undefined && element.ValueKind != JsonValueKind.Null)
        {
            yield return element;
        }
    }

    private static async Task<JsonElement> SendAdminJsonAsync(
        HttpClient client,
        IdentityOnboardingOptions onboarding,
        string adminToken,
        HttpMethod method,
        string path,
        object? body,
        CancellationToken cancellationToken,
        params HttpStatusCode[] acceptedErrorCodes)
    {
        using var request = new HttpRequestMessage(method, $"{onboarding.KeycloakBaseUrl.TrimEnd('/')}{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        request.Headers.Accept.ParseAdd("application/json");
        if (body is not null)
        {
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        }

        using var response = await client.SendAsync(request, cancellationToken);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            if (acceptedErrorCodes.Contains(response.StatusCode))
            {
                return default;
            }

            throw new InvalidOperationException($"Keycloak API {method} {path} failed with {(int)response.StatusCode}: {payload}");
        }

        if (string.IsNullOrWhiteSpace(payload))
        {
            return default;
        }

        using var document = JsonDocument.Parse(payload);
        return document.RootElement.Clone();
    }

    private static async Task SendAdminNoContentAsync(
        HttpClient client,
        IdentityOnboardingOptions onboarding,
        string adminToken,
        HttpMethod method,
        string path,
        object? body,
        CancellationToken cancellationToken,
        params HttpStatusCode[] acceptedErrorCodes)
    {
        _ = await SendAdminJsonAsync(
            client,
            onboarding,
            adminToken,
            method,
            path,
            body,
            cancellationToken,
            acceptedErrorCodes);
    }

    private sealed record KeycloakUser(
        string Id,
        string Username,
        string Email,
        IReadOnlyDictionary<string, string[]> Attributes);

    private sealed record KeycloakGroup(
        string Id,
        string Name,
        IReadOnlyDictionary<string, string[]> Attributes);
}

/// <summary>
/// Represents one completed auto-onboarding run.
/// </summary>
public sealed record OidcOperatorOnboardingResult(
    string UserId,
    string Username,
    string Email,
    IReadOnlyDictionary<string, string[]> AppliedAttributes,
    bool AddedUserToGroup,
    bool UpdatedUserAttributes);

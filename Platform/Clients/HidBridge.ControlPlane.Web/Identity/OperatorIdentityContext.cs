using System.Security.Claims;
using HidBridge.ControlPlane.Web.Configuration;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace HidBridge.ControlPlane.Web.Identity;

/// <summary>
/// Represents the current operator identity projected into the web shell.
/// </summary>
public sealed record OperatorIdentityContext(
    bool IsAuthenticated,
    string SubjectId,
    string PrincipalId,
    string DisplayName,
    string TenantId,
    string OrganizationId,
    IReadOnlyList<string> Roles,
    string AuthenticationMode,
    DateTimeOffset? UserCreatedAtUtc)
{
    private const string UnassignedValue = "unassigned";

    /// <summary>
    /// Gets whether the current operator has the moderator role or inherits it via the admin role.
    /// </summary>
    public bool IsModerator => HasRole("operator.moderator") || IsAdmin;

    /// <summary>
    /// Gets whether the current operator has the administrator role.
    /// </summary>
    public bool IsAdmin => HasRole("operator.admin");

    /// <summary>
    /// Gets the roles that belong to the HidBridge application namespace.
    /// </summary>
    public IReadOnlyList<string> OperatorRoles => Roles
        .Where(static role => role.StartsWith("operator.", StringComparison.OrdinalIgnoreCase))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    /// <summary>
    /// Gets a compact representation of the current operator roles.
    /// </summary>
    public string RoleSummary => OperatorRoles.Count == 0 ? "authenticated" : string.Join(", ", OperatorRoles);

    /// <summary>
    /// Creates an operator identity context from the current HTTP user or the configured development fallback.
    /// </summary>
    /// <param name="httpContextAccessor">The HTTP context accessor.</param>
    /// <param name="options">The configured identity options.</param>
    public static OperatorIdentityContext Create(IHttpContextAccessor httpContextAccessor, IOptions<IdentityOptions> options)
    {
        var user = httpContextAccessor.HttpContext?.User;
        if (user?.Identity?.IsAuthenticated == true)
        {
            return FromPrincipal(user, options.Value);
        }

        return FromDevelopment(options.Value.Development, options.Value);
    }

    /// <summary>
    /// Creates a context from a claims principal.
    /// </summary>
    /// <param name="principal">The authenticated principal.</param>
    /// <param name="options">The configured identity settings.</param>
    public static OperatorIdentityContext FromPrincipal(ClaimsPrincipal principal, IdentityOptions options)
    {
        var roles = principal.FindAll(options.RoleClaimType)
            .Concat(principal.FindAll(ClaimTypes.Role))
            .Select(static claim => claim.Value)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var fallback = options.Development;
        var useDevelopmentFallback = !options.Enabled
            || string.Equals(principal.Identity?.AuthenticationType, DevelopmentOperatorAuthenticationHandler.SchemeName, StringComparison.Ordinal);
        var subjectId = principal.FindFirstValue(options.SubjectClaimType)
            ?? principal.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? principal.FindFirstValue("sub")
            ?? fallback.SubjectId;
        var principalId = principal.FindFirstValue(options.PrincipalClaimType)
            ?? principal.FindFirstValue(OperatorClaimTypes.PrincipalId)
            ?? principal.FindFirstValue("preferred_username")
            ?? principal.FindFirstValue(ClaimTypes.Upn)
            ?? principal.FindFirstValue(ClaimTypes.Email)
            ?? principal.FindFirstValue("email")
            ?? subjectId;
        var displayName = principal.FindFirstValue(options.DisplayNameClaimType)
            ?? principal.FindFirstValue(ClaimTypes.Name)
            ?? principal.FindFirstValue("name")
            ?? principalId;
        var tenantId = principal.FindFirstValue(options.TenantClaimType)
            ?? principal.FindFirstValue(OperatorClaimTypes.TenantId)
            ?? (useDevelopmentFallback ? fallback.TenantId : UnassignedValue);
        var organizationId = principal.FindFirstValue(options.OrganizationClaimType)
            ?? principal.FindFirstValue(OperatorClaimTypes.OrganizationId)
            ?? (useDevelopmentFallback ? fallback.OrganizationId : UnassignedValue);

        return new OperatorIdentityContext(
            true,
            subjectId,
            principalId,
            displayName,
            tenantId,
            organizationId,
            roles,
            string.IsNullOrWhiteSpace(principal.Identity?.AuthenticationType) ? "OIDC" : principal.Identity!.AuthenticationType!,
            ParseUserCreatedAt(
                principal.FindFirstValue("user_created_at")
                ?? principal.FindFirstValue("created_at")
                ?? principal.FindFirstValue("createdTimestamp")));
    }

    private static OperatorIdentityContext FromDevelopment(DevelopmentIdentityOptions development, IdentityOptions options)
        => new(
            true,
            development.SubjectId,
            development.PrincipalId,
            development.DisplayName,
            development.TenantId,
            development.OrganizationId,
            development.Roles,
            options.Enabled ? "OIDC" : DevelopmentOperatorAuthenticationHandler.SchemeName,
            null);

    /// <summary>
    /// Determines whether the operator has one explicit role.
    /// </summary>
    /// <param name="role">The role name to test.</param>
    public bool HasRole(string role)
        => Roles.Any(candidate => string.Equals(candidate, role, StringComparison.OrdinalIgnoreCase));

    private static DateTimeOffset? ParseUserCreatedAt(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (DateTimeOffset.TryParse(value, out var parsedDateTimeOffset))
        {
            return parsedDateTimeOffset;
        }

        if (long.TryParse(value, out var numericValue))
        {
            try
            {
                return value.Length >= 13
                    ? DateTimeOffset.FromUnixTimeMilliseconds(numericValue)
                    : DateTimeOffset.FromUnixTimeSeconds(numericValue);
            }
            catch (ArgumentOutOfRangeException)
            {
                return null;
            }
        }

        return null;
    }
}

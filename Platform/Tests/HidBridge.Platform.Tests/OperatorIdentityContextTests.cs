using System.Security.Claims;
using HidBridge.ControlPlane.Web.Configuration;
using HidBridge.ControlPlane.Web.Identity;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Xunit;

namespace HidBridge.Platform.Tests;

/// <summary>
/// Verifies identity projection for the operator web shell.
/// </summary>
public sealed class OperatorIdentityContextTests
{
    /// <summary>
    /// Verifies that authenticated claims are projected into the operator identity context.
    /// </summary>
    [Fact]
    public void FromPrincipal_UsesClaimsWhenAvailable()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, "sub-1"),
            new Claim(OperatorClaimTypes.PrincipalId, "operator-1"),
            new Claim(ClaimTypes.Name, "Operator One"),
            new Claim(OperatorClaimTypes.TenantId, "tenant-a"),
            new Claim(OperatorClaimTypes.OrganizationId, "org-a"),
            new Claim(ClaimTypes.Role, "operator.viewer"),
            new Claim(ClaimTypes.Role, "operator.moderator"),
            new Claim(ClaimTypes.Role, "offline_access"),
        ], "test"));

        var context = OperatorIdentityContext.FromPrincipal(principal, new IdentityOptions());

        Assert.True(context.IsAuthenticated);
        Assert.Equal("sub-1", context.SubjectId);
        Assert.Equal("operator-1", context.PrincipalId);
        Assert.Equal("Operator One", context.DisplayName);
        Assert.Equal("tenant-a", context.TenantId);
        Assert.Equal("org-a", context.OrganizationId);
        Assert.Contains("operator.moderator", context.Roles);
        Assert.True(context.IsModerator);
        Assert.False(context.IsAdmin);
        Assert.Equal("test", context.AuthenticationMode);
        Assert.Equal("operator.viewer, operator.moderator", context.RoleSummary);
        Assert.Null(context.UserCreatedAtUtc);
    }

    /// <summary>
    /// Verifies that the development identity is used when no authenticated user exists.
    /// </summary>
    [Fact]
    public void Create_UsesDevelopmentFallbackWhenHttpContextIsAnonymous()
    {
        var httpContextAccessor = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext(),
        };
        var options = Options.Create(new IdentityOptions
        {
            Development = new DevelopmentIdentityOptions
            {
                SubjectId = "dev-subject",
                PrincipalId = "dev-principal",
                DisplayName = "Dev Operator",
                TenantId = "tenant-dev",
                OrganizationId = "org-dev",
                Roles = ["operator.admin"],
            }
        });

        var context = OperatorIdentityContext.Create(httpContextAccessor, options);

        Assert.True(context.IsAuthenticated);
        Assert.Equal("dev-principal", context.PrincipalId);
        Assert.Equal("Dev Operator", context.DisplayName);
        Assert.Equal("tenant-dev", context.TenantId);
        Assert.Equal("org-dev", context.OrganizationId);
        Assert.Contains("operator.admin", context.Roles);
        Assert.True(context.IsAdmin);
        Assert.True(context.IsModerator);
        Assert.Equal("operator.admin", context.RoleSummary);
        Assert.Equal(DevelopmentOperatorAuthenticationHandler.SchemeName, context.AuthenticationMode);
        Assert.Null(context.UserCreatedAtUtc);
    }

    /// <summary>
    /// Verifies that configurable claim mappings are honored for external OIDC providers.
    /// </summary>
    [Fact]
    public void FromPrincipal_UsesConfiguredClaimMappings()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("sub_alt", "sub-oidc"),
            new Claim("user_code", "operator-oidc"),
            new Claim("display_name", "OIDC Operator"),
            new Claim("tenant_code", "tenant-oidc"),
            new Claim("org_code", "org-oidc"),
            new Claim("roles", "operator.admin"),
        ], "oidc"));

        var context = OperatorIdentityContext.FromPrincipal(principal, new IdentityOptions
        {
            SubjectClaimType = "sub_alt",
            PrincipalClaimType = "user_code",
            DisplayNameClaimType = "display_name",
            TenantClaimType = "tenant_code",
            OrganizationClaimType = "org_code",
            RoleClaimType = "roles",
        });

        Assert.Equal("sub-oidc", context.SubjectId);
        Assert.Equal("operator-oidc", context.PrincipalId);
        Assert.Equal("OIDC Operator", context.DisplayName);
        Assert.Equal("tenant-oidc", context.TenantId);
        Assert.Equal("org-oidc", context.OrganizationId);
        Assert.True(context.IsAdmin);
        Assert.Equal("oidc", context.AuthenticationMode);
        Assert.Null(context.UserCreatedAtUtc);
    }

    /// <summary>
    /// Verifies that external OIDC principals do not inherit development tenant and organization fallbacks.
    /// </summary>
    [Fact]
    public void FromPrincipal_DoesNotUseDevelopmentTenantFallbackForOidcUsers()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("sub", "google-subject"),
            new Claim("email", "user@example.com"),
            new Claim("name", "Google User"),
            new Claim("role", "offline_access"),
        ], "AuthenticationTypes.Federation"));

        var context = OperatorIdentityContext.FromPrincipal(principal, new IdentityOptions
        {
            Enabled = true,
            RoleClaimType = "role",
            Development = new DevelopmentIdentityOptions
            {
                TenantId = "local-tenant",
                OrganizationId = "local-org",
            }
        });

        Assert.Equal("user@example.com", context.PrincipalId);
        Assert.Equal("unassigned", context.TenantId);
        Assert.Equal("unassigned", context.OrganizationId);
        Assert.Contains("offline_access", context.Roles);
        Assert.Equal("authenticated", context.RoleSummary);
    }

    /// <summary>
    /// Verifies that only HidBridge operator roles are shown in the UI role summary.
    /// </summary>
    [Fact]
    public void FromPrincipal_FiltersInfrastructureRolesFromVisibleRoleSummary()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("sub", "sub-filter"),
            new Claim("email", "viewer@example.com"),
            new Claim("role", "operator.viewer"),
            new Claim("role", "offline_access"),
            new Claim("role", "default-roles-hidbridge-dev"),
            new Claim("role", "uma_authorization"),
        ], "AuthenticationTypes.Federation"));

        var context = OperatorIdentityContext.FromPrincipal(principal, new IdentityOptions
        {
            Enabled = true,
            RoleClaimType = "role",
        });

        Assert.Equal(["operator.viewer"], context.OperatorRoles);
        Assert.Equal("operator.viewer", context.RoleSummary);
    }



    /// <summary>
    /// Verifies that user creation timestamps are parsed from numeric claims.
    /// </summary>
    [Fact]
    public void FromPrincipal_ParsesUserCreatedAtFromUnixMilliseconds()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("sub", "sub-created"),
            new Claim("email", "created@example.com"),
            new Claim("createdTimestamp", "1760000000000"),
        ], "oidc"));

        var context = OperatorIdentityContext.FromPrincipal(principal, new IdentityOptions { Enabled = true });

        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1760000000000), context.UserCreatedAtUtc);
    }
}

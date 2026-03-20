using HidBridge.ControlPlane.Api;
using Microsoft.AspNetCore.Http;
using System.Security.Claims;
using Xunit;

namespace HidBridge.Platform.Tests;

public sealed class ApiCallerContextTests
{
    [Fact]
    public void FromHttpContext_CollectsOperatorHeadersAndRoles()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers["X-HidBridge-UserId"] = "user-1";
        httpContext.Request.Headers["X-HidBridge-PrincipalId"] = "operator@example.com";
        httpContext.Request.Headers["X-HidBridge-TenantId"] = "tenant-a";
        httpContext.Request.Headers["X-HidBridge-OrganizationId"] = "org-a";
        httpContext.Request.Headers["X-HidBridge-Role"] = "operator.viewer, offline_access, operator.moderator";

        var caller = ApiCallerContext.FromHttpContext(httpContext);

        Assert.True(caller.IsPresent);
        Assert.Equal("user-1", caller.SubjectId);
        Assert.Equal("operator@example.com", caller.PrincipalId);
        Assert.Equal("tenant-a", caller.TenantId);
        Assert.Equal("org-a", caller.OrganizationId);
        Assert.Equal(
            new[] { "operator.moderator", "operator.viewer" },
            caller.OperatorRoles.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray());
        Assert.True(caller.CanView);
        Assert.True(caller.CanModerate);
    }

    [Fact]
    public void EnsureViewerAccess_RejectsCallerWithoutOperatorRole()
    {
        var caller = new ApiCallerContext("user-1", "operator@example.com", "tenant-a", "org-a", ["offline_access"]);

        var exception = Assert.Throws<ApiAuthorizationException>(() => caller.EnsureViewerAccess());

        Assert.Equal("viewer_access_required", exception.Code);
        Assert.Contains("operator.viewer", exception.RequiredRoles ?? []);
        Assert.Contains("viewer access", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EnsureModeratorAccess_AllowsAdministratorRole()
    {
        var caller = new ApiCallerContext("user-1", "operator@example.com", "tenant-a", "org-a", ["operator.admin"]);

        caller.EnsureModeratorAccess();

        Assert.True(caller.IsAdmin);
        Assert.True(caller.CanModerate);
    }

    [Fact]
    public void EnsureEdgeRelayAccess_AllowsOperatorEdgeRole()
    {
        var caller = new ApiCallerContext("user-1", "edge@example.com", "tenant-a", "org-a", ["operator.edge"]);

        caller.EnsureEdgeRelayAccess();

        Assert.True(caller.CanAccessEdgeRelay);
    }

    [Fact]
    public void EnsureEdgeRelayAccess_RejectsViewerOnlyRole()
    {
        var caller = new ApiCallerContext("user-1", "viewer@example.com", "tenant-a", "org-a", ["operator.viewer"]);

        var exception = Assert.Throws<ApiAuthorizationException>(() => caller.EnsureEdgeRelayAccess());

        Assert.Equal("edge_relay_access_required", exception.Code);
        Assert.Contains("operator.edge", exception.RequiredRoles ?? []);
    }

    [Fact]
    public void EnsureViewerOrEdgeRelayAccess_AllowsEdgeRole()
    {
        var caller = new ApiCallerContext("user-1", "edge@example.com", "tenant-a", "org-a", ["operator.edge"]);

        caller.EnsureViewerOrEdgeRelayAccess();
    }

    [Fact]
    public void EnsureViewerOrEdgeRelayAccess_RejectsCallerWithoutViewerOrEdgeRole()
    {
        var caller = new ApiCallerContext("user-1", "edge@example.com", "tenant-a", "org-a", ["offline_access"]);

        var exception = Assert.Throws<ApiAuthorizationException>(() => caller.EnsureViewerOrEdgeRelayAccess());

        Assert.Equal("viewer_or_edge_relay_access_required", exception.Code);
        Assert.Contains("operator.viewer", exception.RequiredRoles ?? []);
        Assert.Contains("operator.edge", exception.RequiredRoles ?? []);
    }

    [Fact]
    public void EnsureAdminAccess_RejectsModeratorRole()
    {
        var caller = new ApiCallerContext("user-1", "operator@example.com", "tenant-a", "org-a", ["operator.moderator"]);

        var exception = Assert.Throws<ApiAuthorizationException>(() => caller.EnsureAdminAccess());

        Assert.Equal("admin_access_required", exception.Code);
        Assert.Equal(["operator.admin"], exception.RequiredRoles);
    }

    [Fact]
    public void EnsureSessionScope_ReportsTenantMismatch()
    {
        var caller = new ApiCallerContext("user-1", "operator@example.com", "tenant-a", "org-a", ["operator.viewer"]);
        var session = new HidBridge.Abstractions.SessionSnapshot(
            "session-1",
            "agent-1",
            "endpoint-1",
            HidBridge.Contracts.SessionProfile.UltraLowLatency,
            "operator@example.com",
            HidBridge.Contracts.SessionRole.Owner,
            HidBridge.Contracts.SessionState.Active,
            DateTimeOffset.UtcNow,
            [],
            [],
            null,
            null,
            null,
            "tenant-b",
            "org-a");

        var exception = Assert.Throws<ApiAuthorizationException>(() => caller.EnsureSessionScope(session));

        Assert.Equal("tenant_scope_mismatch", exception.Code);
        Assert.Equal("tenant-b", exception.RequiredTenantId);
        Assert.Equal("org-a", exception.RequiredOrganizationId);
    }

    [Fact]
    public void Apply_SessionRequest_StampsOperatorRoles()
    {
        var caller = new ApiCallerContext("user-1", "operator@example.com", "tenant-a", "org-a", ["operator.viewer", "offline_access"]);

        var request = caller.Apply(new HidBridge.Contracts.SessionOpenBody(
            "session-1",
            HidBridge.Contracts.SessionProfile.UltraLowLatency,
            "original",
            "agent-1",
            "endpoint-1"));

        Assert.Equal("operator@example.com", request.RequestedBy);
        Assert.Equal("tenant-a", request.TenantId);
        Assert.Equal("org-a", request.OrganizationId);
        Assert.Equal(["operator.viewer"], request.OperatorRoles);
    }

    [Fact]
    public void FromHttpContext_UsesConfiguredScopeDefaultsForAuthenticatedCaller()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("sub", "user-1"),
            new Claim("preferred_username", "operator@example.com"),
            new Claim("role", "operator.viewer"),
        ], "Bearer"));

        var caller = ApiCallerContext.FromHttpContext(
            httpContext,
            allowHeaderFallback: false,
            defaultTenantId: "local-tenant",
            defaultOrganizationId: "local-org");

        Assert.Equal("local-tenant", caller.TenantId);
        Assert.Equal("local-org", caller.OrganizationId);
    }

    [Fact]
    public void FromHttpContext_TreatsUnassignedScopeAsMissingAndAppliesDefaults()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("sub", "user-1"),
            new Claim("preferred_username", "operator@example.com"),
            new Claim("tenant_id", "unassigned"),
            new Claim("org_id", "unassigned"),
            new Claim("role", "operator.viewer"),
        ], "Bearer"));

        var caller = ApiCallerContext.FromHttpContext(
            httpContext,
            allowHeaderFallback: false,
            defaultTenantId: "local-tenant",
            defaultOrganizationId: "local-org");

        Assert.Equal("local-tenant", caller.TenantId);
        Assert.Equal("local-org", caller.OrganizationId);
    }
}

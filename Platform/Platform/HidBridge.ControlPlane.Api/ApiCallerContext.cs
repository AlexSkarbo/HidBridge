using HidBridge.Abstractions;
using HidBridge.Contracts;
using Microsoft.Extensions.Primitives;

namespace HidBridge.ControlPlane.Api;

/// <summary>
/// Represents the caller identity propagated to the ControlPlane API.
/// </summary>
public sealed record ApiCallerContext(
    string? SubjectId,
    string? PrincipalId,
    string? TenantId,
    string? OrganizationId,
    IReadOnlyList<string> Roles)
{
    internal const string ItemKey = "hidbridge.api.caller";
    internal const string SubjectIdHeader = "X-HidBridge-UserId";
    internal const string PrincipalIdHeader = "X-HidBridge-PrincipalId";
    internal const string TenantIdHeader = "X-HidBridge-TenantId";
    internal const string OrganizationIdHeader = "X-HidBridge-OrganizationId";
    internal const string RoleHeader = "X-HidBridge-Role";

    /// <summary>
    /// Gets whether any caller identity data was propagated.
    /// </summary>
    public bool IsPresent =>
        !string.IsNullOrWhiteSpace(SubjectId)
        || !string.IsNullOrWhiteSpace(PrincipalId)
        || !string.IsNullOrWhiteSpace(TenantId)
        || !string.IsNullOrWhiteSpace(OrganizationId)
        || Roles.Count > 0;

    /// <summary>
    /// Gets the effective principal identifier used for backend authorization.
    /// </summary>
    public string? EffectivePrincipalId => PrincipalId ?? SubjectId;

    /// <summary>
    /// Gets whether the caller carries tenant or organization scope information.
    /// </summary>
    public bool HasScope => !string.IsNullOrWhiteSpace(TenantId) || !string.IsNullOrWhiteSpace(OrganizationId);

    /// <summary>
    /// Gets the HidBridge application roles projected for the caller.
    /// </summary>
    public IReadOnlyList<string> OperatorRoles => OperatorPolicyRoles.Normalize(Roles);

    /// <summary>
    /// Gets whether the caller has at least viewer-level operator access.
    /// </summary>
    public bool CanView => OperatorPolicyRoles.HasViewerAccess(Roles);

    /// <summary>
    /// Gets whether the caller has moderator-level operator access.
    /// </summary>
    public bool CanModerate => OperatorPolicyRoles.HasModerationAccess(OperatorRoles);

    /// <summary>
    /// Gets whether the caller has administrator-level operator access.
    /// </summary>
    public bool IsAdmin => OperatorPolicyRoles.HasAdminAccess(OperatorRoles);

    /// <summary>
    /// Gets whether the caller has edge relay transport access.
    /// </summary>
    public bool CanAccessEdgeRelay => OperatorPolicyRoles.HasEdgeRelayAccess(OperatorRoles);

    /// <summary>
    /// Creates a context from the current HTTP request.
    /// </summary>
    public static ApiCallerContext FromHttpContext(HttpContext httpContext)
        => FromHttpContext(httpContext, allowHeaderFallback: true, defaultTenantId: null, defaultOrganizationId: null);

    /// <summary>
    /// Creates a context from the current HTTP request while controlling whether legacy caller-scope headers may contribute fallback context.
    /// </summary>
    public static ApiCallerContext FromHttpContext(
        HttpContext httpContext,
        bool allowHeaderFallback,
        string? defaultTenantId = null,
        string? defaultOrganizationId = null)
    {
        if (httpContext.Items.TryGetValue(ItemKey, out var enriched)
            && enriched is ApiCallerContext callerContext)
        {
            return callerContext;
        }

        var user = httpContext.User;
        var isAuthenticated = user.Identity?.IsAuthenticated == true;
        var roles = user.FindAll("role")
            .Concat(user.FindAll(System.Security.Claims.ClaimTypes.Role))
            .Select(static claim => claim.Value)
            .Concat(allowHeaderFallback ? ReadHeaderValues(httpContext.Request.Headers, RoleHeader) : Array.Empty<string>())
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var tenantId = NormalizeScopeValue(FirstNonEmpty(
            user.FindFirst("tenant_id")?.Value,
            allowHeaderFallback ? ReadHeaderValue(httpContext.Request.Headers, TenantIdHeader) : null));
        var organizationId = NormalizeScopeValue(FirstNonEmpty(
            user.FindFirst("org_id")?.Value,
            allowHeaderFallback ? ReadHeaderValue(httpContext.Request.Headers, OrganizationIdHeader) : null));
        if (isAuthenticated)
        {
            tenantId ??= defaultTenantId;
            organizationId ??= defaultOrganizationId;
        }

        return new ApiCallerContext(
            FirstNonEmpty(
                user.FindFirst("sub")?.Value,
                user.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value,
                allowHeaderFallback ? ReadHeaderValue(httpContext.Request.Headers, SubjectIdHeader) : null),
            FirstNonEmpty(
                user.FindFirst("principal_id")?.Value,
                user.FindFirst("preferred_username")?.Value,
                user.FindFirst(System.Security.Claims.ClaimTypes.Upn)?.Value,
                user.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value,
                allowHeaderFallback ? ReadHeaderValue(httpContext.Request.Headers, PrincipalIdHeader) : null),
            tenantId,
            organizationId,
            roles);
    }

    /// <summary>
    /// Applies one resolved operator policy projection to the caller context.
    /// </summary>
    public ApiCallerContext Apply(OperatorPolicyResolution resolution)
        => this with
        {
            PrincipalId = resolution.PrincipalId ?? PrincipalId,
            TenantId = resolution.TenantId ?? TenantId,
            OrganizationId = resolution.OrganizationId ?? OrganizationId,
            Roles = resolution.OperatorRoles,
        };

    /// <summary>
    /// Verifies that the caller has at least viewer-level operator access.
    /// </summary>
    public void EnsureViewerAccess()
    {
        if (!IsPresent)
        {
            return;
        }

        if (!OperatorPolicyRoles.HasViewerAccess(Roles))
        {
            throw new ApiAuthorizationException(
                "viewer_access_required",
                "Caller does not have an operator role that grants viewer access.",
                ["operator.viewer", "operator.moderator", "operator.admin"]);
        }
    }

    /// <summary>
    /// Verifies that the caller can either view session data or access edge relay transport paths.
    /// </summary>
    public void EnsureViewerOrEdgeRelayAccess()
    {
        if (!IsPresent)
        {
            return;
        }

        if (!CanView && !CanAccessEdgeRelay)
        {
            throw new ApiAuthorizationException(
                "viewer_or_edge_relay_access_required",
                "Caller does not have an operator role that grants viewer or edge relay access.",
                ["operator.viewer", "operator.moderator", "operator.admin", OperatorPolicyRoles.EdgeRelay]);
        }
    }

    /// <summary>
    /// Verifies that the caller has moderator-level operator access.
    /// </summary>
    public void EnsureModeratorAccess()
    {
        if (!IsPresent)
        {
            return;
        }

        if (!CanModerate)
        {
            throw new ApiAuthorizationException(
                "moderation_access_required",
                "Caller does not have an operator role that grants moderation access.",
                ["operator.moderator", "operator.admin"]);
        }
    }

    /// <summary>
    /// Verifies that the caller has edge relay transport access.
    /// </summary>
    public void EnsureEdgeRelayAccess()
    {
        if (!IsPresent)
        {
            return;
        }

        if (!CanAccessEdgeRelay)
        {
            throw new ApiAuthorizationException(
                "edge_relay_access_required",
                "Caller does not have an operator role that grants edge relay access.",
                [OperatorPolicyRoles.EdgeRelay, "operator.moderator", "operator.admin"]);
        }
    }

    /// <summary>
    /// Verifies that the caller has administrator-level operator access.
    /// </summary>
    public void EnsureAdminAccess()
    {
        if (!IsPresent)
        {
            return;
        }

        if (!IsAdmin)
        {
            throw new ApiAuthorizationException(
                "admin_access_required",
                "Caller does not have an operator role that grants administrator access.",
                ["operator.admin"]);
        }
    }

    /// <summary>
    /// Applies the caller scope to a session-open request.
    /// </summary>
    public SessionOpenBody Apply(SessionOpenBody request)
        => request with
        {
            RequestedBy = EffectivePrincipalId ?? request.RequestedBy,
            TenantId = TenantId ?? request.TenantId,
            OrganizationId = OrganizationId ?? request.OrganizationId,
            OperatorRoles = MergeOperatorRoles(request.OperatorRoles),
        };

    /// <summary>
    /// Applies the caller scope to a participant mutation request.
    /// </summary>
    public SessionParticipantUpsertBody Apply(SessionParticipantUpsertBody request)
        => request with
        {
            AddedBy = EffectivePrincipalId ?? request.AddedBy,
            TenantId = TenantId ?? request.TenantId,
            OrganizationId = OrganizationId ?? request.OrganizationId,
            OperatorRoles = MergeOperatorRoles(request.OperatorRoles),
        };

    /// <summary>
    /// Applies the caller scope to a participant removal request.
    /// </summary>
    public SessionParticipantRemoveBody Apply(SessionParticipantRemoveBody request)
        => request with
        {
            RemovedBy = EffectivePrincipalId ?? request.RemovedBy,
            TenantId = TenantId ?? request.TenantId,
            OrganizationId = OrganizationId ?? request.OrganizationId,
            OperatorRoles = MergeOperatorRoles(request.OperatorRoles),
        };

    /// <summary>
    /// Applies the caller scope to a share grant request.
    /// </summary>
    public SessionShareGrantBody Apply(SessionShareGrantBody request)
        => request with
        {
            GrantedBy = EffectivePrincipalId ?? request.GrantedBy,
            TenantId = TenantId ?? request.TenantId,
            OrganizationId = OrganizationId ?? request.OrganizationId,
            OperatorRoles = MergeOperatorRoles(request.OperatorRoles),
        };

    /// <summary>
    /// Applies the caller scope to an invitation request.
    /// </summary>
    public SessionInvitationRequestBody Apply(SessionInvitationRequestBody request)
        => request with
        {
            RequestedBy = EffectivePrincipalId ?? request.RequestedBy,
            TenantId = TenantId ?? request.TenantId,
            OrganizationId = OrganizationId ?? request.OrganizationId,
            OperatorRoles = MergeOperatorRoles(request.OperatorRoles),
        };

    /// <summary>
    /// Applies the caller scope to an invitation decision request.
    /// </summary>
    public SessionInvitationDecisionBody Apply(SessionInvitationDecisionBody request)
        => request with
        {
            ActedBy = EffectivePrincipalId ?? request.ActedBy,
            TenantId = TenantId ?? request.TenantId,
            OrganizationId = OrganizationId ?? request.OrganizationId,
            OperatorRoles = MergeOperatorRoles(request.OperatorRoles),
        };

    /// <summary>
    /// Applies the caller scope to a share transition request.
    /// </summary>
    public SessionShareTransitionBody Apply(SessionShareTransitionBody request)
        => request with
        {
            ActedBy = EffectivePrincipalId ?? request.ActedBy,
            TenantId = TenantId ?? request.TenantId,
            OrganizationId = OrganizationId ?? request.OrganizationId,
            OperatorRoles = MergeOperatorRoles(request.OperatorRoles),
        };

    /// <summary>
    /// Applies the caller scope to a control request.
    /// </summary>
    public SessionControlRequestBody Apply(SessionControlRequestBody request)
        => request with
        {
            RequestedBy = EffectivePrincipalId ?? request.RequestedBy,
            TenantId = TenantId ?? request.TenantId,
            OrganizationId = OrganizationId ?? request.OrganizationId,
            OperatorRoles = MergeOperatorRoles(request.OperatorRoles),
        };

    /// <summary>
    /// Applies the caller scope to a control-ensure request.
    /// </summary>
    public SessionControlEnsureBody Apply(SessionControlEnsureBody request)
        => request with
        {
            RequestedBy = EffectivePrincipalId ?? request.RequestedBy,
            TenantId = TenantId ?? request.TenantId,
            OrganizationId = OrganizationId ?? request.OrganizationId,
            OperatorRoles = MergeOperatorRoles(request.OperatorRoles),
        };

    /// <summary>
    /// Applies the caller scope to a control grant request.
    /// </summary>
    public SessionControlGrantBody Apply(SessionControlGrantBody request)
        => request with
        {
            GrantedBy = EffectivePrincipalId ?? request.GrantedBy,
            TenantId = TenantId ?? request.TenantId,
            OrganizationId = OrganizationId ?? request.OrganizationId,
            OperatorRoles = MergeOperatorRoles(request.OperatorRoles),
        };

    /// <summary>
    /// Applies the caller scope to a control release request.
    /// </summary>
    public SessionControlReleaseBody Apply(SessionControlReleaseBody request)
        => request with
        {
            ActedBy = EffectivePrincipalId ?? request.ActedBy,
            TenantId = TenantId ?? request.TenantId,
            OrganizationId = OrganizationId ?? request.OrganizationId,
            OperatorRoles = MergeOperatorRoles(request.OperatorRoles),
        };

    /// <summary>
    /// Applies the caller scope to a command request and injects the principal id into the command arguments.
    /// </summary>
    public CommandRequestBody Apply(CommandRequestBody request)
    {
        var args = request.Args.ToDictionary(entry => entry.Key, entry => entry.Value, StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(EffectivePrincipalId))
        {
            args["principalId"] = EffectivePrincipalId;
        }

        return request with
        {
            Args = args,
            TenantId = TenantId ?? request.TenantId,
            OrganizationId = OrganizationId ?? request.OrganizationId,
            OperatorRoles = MergeOperatorRoles(request.OperatorRoles),
        };
    }

    /// <summary>
    /// Verifies that the caller may access the specified session snapshot.
    /// </summary>
    public void EnsureSessionScope(SessionSnapshot snapshot)
    {
        if (IsAdmin)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(snapshot.TenantId) && string.IsNullOrWhiteSpace(snapshot.OrganizationId))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(TenantId) && string.IsNullOrWhiteSpace(OrganizationId))
        {
            throw new ApiAuthorizationException(
                "session_scope_required",
                $"Session {snapshot.SessionId} requires tenant and organization scope.",
                requiredTenantId: snapshot.TenantId,
                requiredOrganizationId: snapshot.OrganizationId);
        }

        if (!string.IsNullOrWhiteSpace(snapshot.TenantId)
            && !string.Equals(snapshot.TenantId, TenantId, StringComparison.OrdinalIgnoreCase))
        {
            throw new ApiAuthorizationException(
                "tenant_scope_mismatch",
                $"Tenant {TenantId ?? "<missing>"} is not allowed to access session {snapshot.SessionId}.",
                requiredTenantId: snapshot.TenantId,
                requiredOrganizationId: snapshot.OrganizationId);
        }

        if (!string.IsNullOrWhiteSpace(snapshot.OrganizationId)
            && !string.Equals(snapshot.OrganizationId, OrganizationId, StringComparison.OrdinalIgnoreCase))
        {
            throw new ApiAuthorizationException(
                "organization_scope_mismatch",
                $"Organization {OrganizationId ?? "<missing>"} is not allowed to access session {snapshot.SessionId}.",
                requiredTenantId: snapshot.TenantId,
                requiredOrganizationId: snapshot.OrganizationId);
        }
    }

    /// <summary>
    /// Gets whether the caller may access the specified session snapshot.
    /// </summary>
    public bool CanAccessSession(SessionSnapshot snapshot)
    {
        if (!IsPresent)
        {
            return true;
        }

        try
        {
            EnsureSessionScope(snapshot);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Gets whether the caller may view fleet-global diagnostics records that are not bound to one session.
    /// </summary>
    public bool CanAccessGlobalFleetData => !IsPresent || IsAdmin || !HasScope;

    private static string? ReadHeaderValue(IHeaderDictionary headers, string name)
        => headers.TryGetValue(name, out var values)
            ? values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value))
            : null;

    private static IEnumerable<string> ReadHeaderValues(IHeaderDictionary headers, string name)
        => headers.TryGetValue(name, out var values)
            ? values
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .SelectMany(static value => value!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                .Where(static value => !string.IsNullOrWhiteSpace(value))
            : Array.Empty<string>();

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value));

    private static string? NormalizeScopeValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return string.Equals(value, "unassigned", StringComparison.OrdinalIgnoreCase)
            ? null
            : value;
    }

    private IReadOnlyList<string>? MergeOperatorRoles(IReadOnlyList<string>? requestRoles)
    {
        if (OperatorRoles.Count == 0 && (requestRoles is null || requestRoles.Count == 0))
        {
            return requestRoles;
        }

        if (OperatorRoles.Count == 0)
        {
            return requestRoles;
        }

        return OperatorRoles
            .Concat(requestRoles ?? [])
            .Where(static role => !string.IsNullOrWhiteSpace(role))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}

/// <summary>
/// Provides helpers that bind the caller context to persisted session snapshots.
/// </summary>
public static class ApiCallerContextSessionExtensions
{
    /// <summary>
    /// Resolves one session snapshot and verifies that the caller may access it.
    /// </summary>
    public static async Task<SessionSnapshot> RequireScopedSessionAsync(
        this ApiCallerContext caller,
        ISessionStore sessionStore,
        string sessionId,
        CancellationToken cancellationToken)
    {
        var snapshot = (await sessionStore.ListAsync(cancellationToken))
            .FirstOrDefault(x => string.Equals(x.SessionId, sessionId, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException($"Session {sessionId} was not found.");

        caller.EnsureSessionScope(snapshot);
        return snapshot;
    }
}

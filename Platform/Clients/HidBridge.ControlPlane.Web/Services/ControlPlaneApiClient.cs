using System.Net.Http.Json;
using System.Text.Json;
using HidBridge.ControlPlane.Web.Models;

namespace HidBridge.ControlPlane.Web.Services;

/// <summary>
/// Provides typed access to the HidBridge ControlPlane operator read-model endpoints.
/// </summary>
public sealed class ControlPlaneApiClient
{
    private static readonly JsonSerializerOptions AuthorizationJsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _httpClient;

    /// <summary>
    /// Creates the ControlPlane API client.
    /// </summary>
    /// <param name="httpClient">The configured HTTP client.</param>
    public ControlPlaneApiClient(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    /// <summary>
    /// Reads ControlPlane liveness.
    /// </summary>
    public async Task<ApiHealthViewModel?> GetHealthAsync(CancellationToken cancellationToken = default)
        => await GetJsonAsync<ApiHealthViewModel>("/health", cancellationToken);

    /// <summary>
    /// Reads ControlPlane runtime configuration.
    /// </summary>
    public async Task<ApiRuntimeViewModel?> GetRuntimeAsync(CancellationToken cancellationToken = default)
        => await GetJsonAsync<ApiRuntimeViewModel>("/api/v1/runtime/uart", cancellationToken);

    /// <summary>
    /// Reads the fleet inventory dashboard.
    /// </summary>
    public async Task<InventoryDashboardViewModel?> GetInventoryDashboardAsync(CancellationToken cancellationToken = default)
        => await GetJsonAsync<InventoryDashboardViewModel>("/api/v1/dashboards/inventory", cancellationToken);

    /// <summary>
    /// Reads one page of session projections.
    /// </summary>
    public async Task<ProjectionPageViewModel<SessionProjectionItemViewModel>?> GetSessionProjectionAsync(
        int take,
        CancellationToken cancellationToken = default)
        => await GetJsonAsync<ProjectionPageViewModel<SessionProjectionItemViewModel>>(
            $"/api/v1/projections/sessions?take={take}",
            cancellationToken);

    /// <summary>
    /// Opens one new session bound to the specified fleet endpoint.
    /// </summary>
    public async Task<SessionOpenRequestViewModel?> OpenSessionAsync(SessionOpenRequestViewModel body, CancellationToken cancellationToken = default)
        => await SendJsonAsync<SessionOpenRequestViewModel>(HttpMethod.Post, "/api/v1/sessions", body, cancellationToken);

    /// <summary>
    /// Dispatches one command through the specified session room.
    /// </summary>
    public async Task<CommandAckViewModel?> DispatchCommandAsync(
        string sessionId,
        SessionCommandDispatchRequestViewModel body,
        CancellationToken cancellationToken = default)
        => await SendJsonAsync<CommandAckViewModel>(
            HttpMethod.Post,
            $"/api/v1/sessions/{Uri.EscapeDataString(sessionId)}/commands",
            body,
            cancellationToken);

    /// <summary>
    /// Reads one collaboration dashboard for the specified session.
    /// </summary>
    public async Task<SessionDashboardViewModel?> GetSessionDashboardAsync(string sessionId, CancellationToken cancellationToken = default)
        => await GetJsonAsync<SessionDashboardViewModel>(
            $"/api/v1/collaboration/sessions/{Uri.EscapeDataString(sessionId)}/dashboard",
            cancellationToken);

    /// <summary>
    /// Reads the control dashboard for the specified session.
    /// </summary>
    public async Task<ControlDashboardViewModel?> GetControlDashboardAsync(string sessionId, CancellationToken cancellationToken = default)
        => await GetJsonAsync<ControlDashboardViewModel>(
            $"/api/v1/collaboration/sessions/{Uri.EscapeDataString(sessionId)}/control/dashboard",
            cancellationToken);

    /// <summary>
    /// Reads the invitation lobby view for the specified session.
    /// </summary>
    public async Task<SessionLobbyViewModel?> GetLobbyAsync(string sessionId, CancellationToken cancellationToken = default)
        => await GetJsonAsync<SessionLobbyViewModel>(
            $"/api/v1/collaboration/sessions/{Uri.EscapeDataString(sessionId)}/lobby",
            cancellationToken);

    /// <summary>
    /// Creates one pending share invitation from the session room.
    /// </summary>
    public async Task<SessionShareViewModel?> CreateShareAsync(
        string sessionId,
        SessionShareGrantRequestViewModel body,
        CancellationToken cancellationToken = default)
        => await SendJsonAsync<SessionShareViewModel>(
            HttpMethod.Post,
            $"/api/v1/sessions/{Uri.EscapeDataString(sessionId)}/shares",
            body,
            cancellationToken);

    /// <summary>
    /// Creates one requested invitation from the session room.
    /// </summary>
    public async Task<SessionShareViewModel?> RequestInvitationAsync(
        string sessionId,
        SessionInvitationRequestViewModel body,
        CancellationToken cancellationToken = default)
        => await SendJsonAsync<SessionShareViewModel>(
            HttpMethod.Post,
            $"/api/v1/sessions/{Uri.EscapeDataString(sessionId)}/invitations/requests",
            body,
            cancellationToken);

    /// <summary>
    /// Reads participant activity analytics for one session room.
    /// </summary>
    public async Task<IReadOnlyList<ParticipantActivityViewModel>?> GetParticipantActivityAsync(string sessionId, CancellationToken cancellationToken = default)
        => await GetJsonAsync<IReadOnlyList<ParticipantActivityViewModel>>(
            $"/api/v1/collaboration/sessions/{Uri.EscapeDataString(sessionId)}/participants/activity",
            cancellationToken);

    /// <summary>
    /// Reads the operator-focused timeline for one session room.
    /// </summary>
    public async Task<OperatorTimelineViewModel?> GetOperatorTimelineAsync(string sessionId, int take = 20, CancellationToken cancellationToken = default)
        => await GetJsonAsync<OperatorTimelineViewModel>(
            $"/api/v1/collaboration/sessions/{Uri.EscapeDataString(sessionId)}/operators/timeline?take={take}",
            cancellationToken);

    /// <summary>
    /// Requests active control for the specified participant.
    /// </summary>
    public async Task<SessionControlLeaseViewModel?> RequestControlAsync(
        string sessionId,
        string participantId,
        string requestedBy,
        int leaseSeconds,
        string reason,
        CancellationToken cancellationToken = default)
        => await SendJsonAsync<SessionControlLeaseViewModel>(
            HttpMethod.Post,
            $"/api/v1/sessions/{Uri.EscapeDataString(sessionId)}/control/request",
            new
            {
                participantId,
                requestedBy,
                leaseSeconds,
                reason,
            },
            cancellationToken);

    /// <summary>
    /// Releases the active control lease for the specified participant.
    /// </summary>
    public async Task<SessionControlLeaseViewModel?> ReleaseControlAsync(
        string sessionId,
        string participantId,
        string actedBy,
        string reason,
        CancellationToken cancellationToken = default)
        => await SendJsonAsync<SessionControlLeaseViewModel>(
            HttpMethod.Post,
            $"/api/v1/sessions/{Uri.EscapeDataString(sessionId)}/control/release",
            new
            {
                participantId,
                actedBy,
                reason,
            },
            cancellationToken);

    /// <summary>
    /// Force-transfers active control to the specified participant.
    /// </summary>
    public async Task<SessionControlLeaseViewModel?> ForceTakeoverControlAsync(
        string sessionId,
        string participantId,
        string grantedBy,
        int leaseSeconds,
        string reason,
        CancellationToken cancellationToken = default)
        => await SendJsonAsync<SessionControlLeaseViewModel>(
            HttpMethod.Post,
            $"/api/v1/sessions/{Uri.EscapeDataString(sessionId)}/control/force-takeover",
            new
            {
                participantId,
                grantedBy,
                leaseSeconds,
                reason,
            },
            cancellationToken);

    /// <summary>
    /// Approves one requested invitation.
    /// </summary>
    public async Task<SessionShareViewModel?> ApproveInvitationAsync(
        string sessionId,
        string shareId,
        string actedBy,
        string grantedRole,
        string reason,
        CancellationToken cancellationToken = default)
        => await SendJsonAsync<SessionShareViewModel>(
            HttpMethod.Post,
            $"/api/v1/sessions/{Uri.EscapeDataString(sessionId)}/invitations/{Uri.EscapeDataString(shareId)}/approve",
            new
            {
                shareId,
                actedBy,
                grantedRole,
                reason,
            },
            cancellationToken);

    /// <summary>
    /// Declines one requested invitation.
    /// </summary>
    public async Task<SessionShareViewModel?> DeclineInvitationAsync(
        string sessionId,
        string shareId,
        string actedBy,
        string reason,
        CancellationToken cancellationToken = default)
        => await SendJsonAsync<SessionShareViewModel>(
            HttpMethod.Post,
            $"/api/v1/sessions/{Uri.EscapeDataString(sessionId)}/invitations/{Uri.EscapeDataString(shareId)}/decline",
            new
            {
                shareId,
                actedBy,
                reason,
            },
            cancellationToken);

    /// <summary>
    /// Accepts one pending share invitation for the current operator.
    /// </summary>
    public async Task<SessionShareViewModel?> AcceptShareAsync(
        string sessionId,
        string shareId,
        string actedBy,
        string? reason,
        string? tenantId,
        string? organizationId,
        IReadOnlyList<string> operatorRoles,
        CancellationToken cancellationToken = default)
        => await SendJsonAsync<SessionShareViewModel>(
            HttpMethod.Post,
            $"/api/v1/sessions/{Uri.EscapeDataString(sessionId)}/shares/{Uri.EscapeDataString(shareId)}/accept",
            new
            {
                shareId,
                actedBy,
                reason,
                tenantId,
                organizationId,
                operatorRoles,
            },
            cancellationToken);

    /// <summary>
    /// Rejects one pending share invitation for the current operator.
    /// </summary>
    public async Task<SessionShareViewModel?> RejectShareAsync(
        string sessionId,
        string shareId,
        string actedBy,
        string? reason,
        string? tenantId,
        string? organizationId,
        IReadOnlyList<string> operatorRoles,
        CancellationToken cancellationToken = default)
        => await SendJsonAsync<SessionShareViewModel>(
            HttpMethod.Post,
            $"/api/v1/sessions/{Uri.EscapeDataString(sessionId)}/shares/{Uri.EscapeDataString(shareId)}/reject",
            new
            {
                shareId,
                actedBy,
                reason,
                tenantId,
                organizationId,
                operatorRoles,
            },
            cancellationToken);

    /// <summary>
    /// Revokes one issued share invitation.
    /// </summary>
    public async Task<SessionShareViewModel?> RevokeShareAsync(
        string sessionId,
        string shareId,
        string actedBy,
        string? reason,
        string? tenantId,
        string? organizationId,
        IReadOnlyList<string> operatorRoles,
        CancellationToken cancellationToken = default)
        => await SendJsonAsync<SessionShareViewModel>(
            HttpMethod.Post,
            $"/api/v1/sessions/{Uri.EscapeDataString(sessionId)}/shares/{Uri.EscapeDataString(shareId)}/revoke",
            new
            {
                shareId,
                actedBy,
                reason,
                tenantId,
                organizationId,
                operatorRoles,
            },
            cancellationToken);

    /// <summary>
    /// Reads the audit diagnostics dashboard.
    /// </summary>
    public async Task<AuditDashboardViewModel?> GetAuditDashboardAsync(int take, CancellationToken cancellationToken = default)
        => await GetJsonAsync<AuditDashboardViewModel>(
            $"/api/v1/dashboards/audit?take={take}",
            cancellationToken);

    /// <summary>
    /// Reads the telemetry diagnostics dashboard.
    /// </summary>
    public async Task<TelemetryDashboardViewModel?> GetTelemetryDashboardAsync(int take, CancellationToken cancellationToken = default)
        => await GetJsonAsync<TelemetryDashboardViewModel>(
            $"/api/v1/dashboards/telemetry?take={take}",
            cancellationToken);

    /// <summary>
    /// Reads the policy governance summary.
    /// </summary>
    public async Task<PolicyDiagnosticsSummaryViewModel?> GetPolicyDiagnosticsSummaryAsync(CancellationToken cancellationToken = default)
        => await GetJsonAsync<PolicyDiagnosticsSummaryViewModel>("/api/v1/diagnostics/policies/summary", cancellationToken);

    /// <summary>
    /// Reads filtered policy revision snapshots.
    /// </summary>
    public async Task<IReadOnlyList<PolicyRevisionSnapshotViewModel>?> GetPolicyRevisionsAsync(
        string? scopeId,
        string? entityType,
        string? principalId,
        int take,
        CancellationToken cancellationToken = default)
        => await GetJsonAsync<IReadOnlyList<PolicyRevisionSnapshotViewModel>>(
            BuildPolicyRevisionQuery(scopeId, entityType, principalId, take),
            cancellationToken);

    /// <summary>
    /// Executes one manual prune against policy revision history.
    /// </summary>
    public async Task<PolicyRevisionPruneResultViewModel?> PrunePolicyRevisionsAsync(CancellationToken cancellationToken = default)
        => await SendJsonAsync<PolicyRevisionPruneResultViewModel>(HttpMethod.Post, "/api/v1/diagnostics/policies/prune", new { }, cancellationToken);

    /// <summary>
    /// Reads visible policy scopes.
    /// </summary>
    public async Task<IReadOnlyList<PolicyScopeViewModel>?> GetPolicyScopesAsync(CancellationToken cancellationToken = default)
        => await GetJsonAsync<IReadOnlyList<PolicyScopeViewModel>>("/api/v1/diagnostics/policies/scopes", cancellationToken);

    /// <summary>
    /// Reads visible policy assignments.
    /// </summary>
    public async Task<IReadOnlyList<PolicyAssignmentViewModel>?> GetPolicyAssignmentsAsync(CancellationToken cancellationToken = default)
        => await GetJsonAsync<IReadOnlyList<PolicyAssignmentViewModel>>("/api/v1/diagnostics/policies/assignments", cancellationToken);

    /// <summary>
    /// Creates or updates one policy scope.
    /// </summary>
    public async Task<PolicyScopeViewModel?> UpsertPolicyScopeAsync(PolicyScopeUpsertRequestViewModel body, CancellationToken cancellationToken = default)
        => await SendJsonAsync<PolicyScopeViewModel>(HttpMethod.Post, "/api/v1/diagnostics/policies/scopes", body, cancellationToken);

    /// <summary>
    /// Activates one policy scope.
    /// </summary>
    public async Task<PolicyScopeViewModel?> ActivatePolicyScopeAsync(string scopeId, CancellationToken cancellationToken = default)
        => await SendJsonAsync<PolicyScopeViewModel>(HttpMethod.Post, $"/api/v1/diagnostics/policies/scopes/{Uri.EscapeDataString(scopeId)}/activate", new { }, cancellationToken);

    /// <summary>
    /// Deactivates one policy scope.
    /// </summary>
    public async Task<PolicyScopeViewModel?> DeactivatePolicyScopeAsync(string scopeId, CancellationToken cancellationToken = default)
        => await SendJsonAsync<PolicyScopeViewModel>(HttpMethod.Post, $"/api/v1/diagnostics/policies/scopes/{Uri.EscapeDataString(scopeId)}/deactivate", new { }, cancellationToken);

    /// <summary>
    /// Deletes one policy scope.
    /// </summary>
    public async Task DeletePolicyScopeAsync(string scopeId, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(() => _httpClient.DeleteAsync($"/api/v1/diagnostics/policies/scopes/{Uri.EscapeDataString(scopeId)}", cancellationToken), $"/api/v1/diagnostics/policies/scopes/{scopeId}");
        await EnsureSuccessAsync(response, $"/api/v1/diagnostics/policies/scopes/{scopeId}", cancellationToken);
    }

    /// <summary>
    /// Creates or updates one policy assignment.
    /// </summary>
    public async Task<PolicyAssignmentViewModel?> UpsertPolicyAssignmentAsync(PolicyAssignmentUpsertRequestViewModel body, CancellationToken cancellationToken = default)
        => await SendJsonAsync<PolicyAssignmentViewModel>(HttpMethod.Post, "/api/v1/diagnostics/policies/assignments", body, cancellationToken);

    /// <summary>
    /// Deactivates one policy assignment.
    /// </summary>
    public async Task<PolicyAssignmentViewModel?> DeactivatePolicyAssignmentAsync(string assignmentId, CancellationToken cancellationToken = default)
        => await SendJsonAsync<PolicyAssignmentViewModel>(HttpMethod.Post, $"/api/v1/diagnostics/policies/assignments/{Uri.EscapeDataString(assignmentId)}/deactivate", new { }, cancellationToken);

    /// <summary>
    /// Activates one policy assignment.
    /// </summary>
    public async Task<PolicyAssignmentViewModel?> ActivatePolicyAssignmentAsync(string assignmentId, CancellationToken cancellationToken = default)
        => await SendJsonAsync<PolicyAssignmentViewModel>(HttpMethod.Post, $"/api/v1/diagnostics/policies/assignments/{Uri.EscapeDataString(assignmentId)}/activate", new { }, cancellationToken);

    /// <summary>
    /// Deletes one policy assignment.
    /// </summary>
    public async Task DeletePolicyAssignmentAsync(string assignmentId, CancellationToken cancellationToken = default)
    {
        using var response = await SendAsync(() => _httpClient.DeleteAsync($"/api/v1/diagnostics/policies/assignments/{Uri.EscapeDataString(assignmentId)}", cancellationToken), $"/api/v1/diagnostics/policies/assignments/{assignmentId}");
        await EnsureSuccessAsync(response, $"/api/v1/diagnostics/policies/assignments/{assignmentId}", cancellationToken);
    }

    private static string BuildPolicyRevisionQuery(string? scopeId, string? entityType, string? principalId, int take)
    {
        var query = new List<string>();
        if (!string.IsNullOrWhiteSpace(scopeId))
        {
            query.Add($"scopeId={Uri.EscapeDataString(scopeId)}");
        }

        if (!string.IsNullOrWhiteSpace(entityType))
        {
            query.Add($"entityType={Uri.EscapeDataString(entityType)}");
        }

        if (!string.IsNullOrWhiteSpace(principalId))
        {
            query.Add($"principalId={Uri.EscapeDataString(principalId)}");
        }

        query.Add($"take={take}");
        return $"/api/v1/diagnostics/policies/revisions?{string.Join("&", query)}";
    }

    private async Task<TResponse?> GetJsonAsync<TResponse>(
        string relativeUri,
        CancellationToken cancellationToken)
    {
        using var response = await SendAsync(() => _httpClient.GetAsync(relativeUri, cancellationToken), relativeUri);
        await EnsureSuccessAsync(response, relativeUri, cancellationToken);
        return await response.Content.ReadFromJsonAsync<TResponse>(cancellationToken);
    }

    private async Task<TResponse?> SendJsonAsync<TResponse>(
        HttpMethod method,
        string relativeUri,
        object body,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, relativeUri)
        {
            Content = JsonContent.Create(body),
        };

        using var response = await SendAsync(() => _httpClient.SendAsync(request, cancellationToken), relativeUri);
        await EnsureSuccessAsync(response, relativeUri, cancellationToken);

        return await response.Content.ReadFromJsonAsync<TResponse>(cancellationToken);
    }

    private static async Task<HttpResponseMessage> SendAsync(
        Func<Task<HttpResponseMessage>> send,
        string relativeUri)
    {
        try
        {
            return await send();
        }
        catch (HttpRequestException exception)
        {
            throw new ControlPlaneApiUnavailableException(relativeUri, exception);
        }
        catch (TaskCanceledException exception) when (exception.InnerException is null)
        {
            throw new ControlPlaneApiUnavailableException(relativeUri, exception);
        }
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, string relativeUri, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var rawBody = await response.Content.ReadAsStringAsync(cancellationToken);
        if (response.StatusCode is System.Net.HttpStatusCode.ServiceUnavailable
            or System.Net.HttpStatusCode.BadGateway
            or System.Net.HttpStatusCode.GatewayTimeout)
        {
            throw new ControlPlaneApiUnavailableException(relativeUri, rawBody);
        }

        AuthorizationDeniedViewModel? denial = null;
        if (((int)response.StatusCode == 401 || (int)response.StatusCode == 403) && !string.IsNullOrWhiteSpace(rawBody))
        {
            denial = JsonSerializer.Deserialize<AuthorizationDeniedViewModel>(rawBody, AuthorizationJsonOptions);
        }

        throw new ControlPlaneApiException(response.StatusCode, rawBody, denial);
    }
}

/// <summary>
/// Represents one typed backend error returned by the ControlPlane API.
/// </summary>
public sealed class ControlPlaneApiException : InvalidOperationException
{
    /// <summary>
    /// Initializes a typed ControlPlane API exception.
    /// </summary>
    public ControlPlaneApiException(
        System.Net.HttpStatusCode statusCode,
        string rawBody,
        AuthorizationDeniedViewModel? authorizationDenied = null)
        : base(BuildMessage(statusCode, rawBody, authorizationDenied))
    {
        StatusCode = statusCode;
        RawBody = rawBody;
        AuthorizationDenied = authorizationDenied;
    }

    /// <summary>
    /// Gets the HTTP status code returned by the API.
    /// </summary>
    public System.Net.HttpStatusCode StatusCode { get; }

    /// <summary>
    /// Gets the raw response body.
    /// </summary>
    public string RawBody { get; }

    /// <summary>
    /// Gets the structured authorization denial payload when the backend returned one.
    /// </summary>
    public AuthorizationDeniedViewModel? AuthorizationDenied { get; }

    private static string BuildMessage(
        System.Net.HttpStatusCode statusCode,
        string rawBody,
        AuthorizationDeniedViewModel? authorizationDenied)
    {
        if (authorizationDenied is null)
        {
            return $"ControlPlane request failed with status {(int)statusCode}: {rawBody}";
        }

        var segments = new List<string>
        {
            $"{authorizationDenied.Message} ({authorizationDenied.Code})",
        };

        if (authorizationDenied.RequiredRoles is { Count: > 0 })
        {
            segments.Add($"required roles: {string.Join(", ", authorizationDenied.RequiredRoles)}");
        }

        if (!string.IsNullOrWhiteSpace(authorizationDenied.RequiredTenantId)
            || !string.IsNullOrWhiteSpace(authorizationDenied.RequiredOrganizationId))
        {
            segments.Add(
                $"required scope: tenant={authorizationDenied.RequiredTenantId ?? "any"}, org={authorizationDenied.RequiredOrganizationId ?? "any"}");
        }

        return string.Join(" | ", segments);
    }
}

/// <summary>
/// Represents one backend-unavailable failure during ControlPlane API calls.
/// </summary>
public sealed class ControlPlaneApiUnavailableException : InvalidOperationException
{
    /// <summary>
    /// Initializes one typed unavailable exception.
    /// </summary>
    public ControlPlaneApiUnavailableException(string relativeUri, Exception innerException)
        : base($"ControlPlane backend is unavailable for '{relativeUri}'. Start the API or check connectivity/auth configuration.", innerException)
    {
        RelativeUri = relativeUri;
    }

    /// <summary>
    /// Initializes one typed unavailable exception from a synthetic backend-unavailable response.
    /// </summary>
    public ControlPlaneApiUnavailableException(string relativeUri, string detail)
        : base(BuildMessage(relativeUri, detail))
    {
        RelativeUri = relativeUri;
    }

    /// <summary>
    /// Gets the requested relative URI.
    /// </summary>
    public string RelativeUri { get; }

    private static string BuildMessage(string relativeUri, string detail)
        => string.IsNullOrWhiteSpace(detail)
            ? $"ControlPlane backend is unavailable for '{relativeUri}'. Start the API or check connectivity/auth configuration."
            : $"ControlPlane backend is unavailable for '{relativeUri}'. {detail}";
}

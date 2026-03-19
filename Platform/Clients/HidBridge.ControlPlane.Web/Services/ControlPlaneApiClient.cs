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
    /// Closes one session room and releases its endpoint binding.
    /// </summary>
    public async Task CloseSessionAsync(
        string sessionId,
        string reason,
        CancellationToken cancellationToken = default)
    {
        var relativeUri = $"/api/v1/sessions/{Uri.EscapeDataString(sessionId)}/close";
        using var request = new HttpRequestMessage(HttpMethod.Post, relativeUri)
        {
            Content = JsonContent.Create(new
            {
                sessionId,
                reason,
            }),
        };

        using var response = await SendAsync(() => _httpClient.SendAsync(request, cancellationToken), relativeUri, cancellationToken);
        await EnsureSuccessAsync(response, relativeUri, cancellationToken);
    }

    /// <summary>
    /// Closes all failed sessions visible to the caller.
    /// </summary>
    public async Task<SessionBulkCloseResultViewModel?> CloseFailedSessionsAsync(
        string reason,
        bool dryRun = false,
        CancellationToken cancellationToken = default)
        => await SendJsonAsync<SessionBulkCloseResultViewModel>(
            HttpMethod.Post,
            "/api/v1/sessions/actions/close-failed",
            new
            {
                dryRun,
                reason,
            },
            cancellationToken);

    /// <summary>
    /// Closes stale sessions visible to the caller.
    /// </summary>
    public async Task<SessionBulkCloseResultViewModel?> CloseStaleSessionsAsync(
        string reason,
        int staleAfterMinutes = 30,
        bool dryRun = false,
        CancellationToken cancellationToken = default)
        => await SendJsonAsync<SessionBulkCloseResultViewModel>(
            HttpMethod.Post,
            "/api/v1/sessions/actions/close-stale",
            new
            {
                dryRun,
                reason,
                staleAfterMinutes,
            },
            cancellationToken);

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
    /// Reads transport routing health and latest command ack diagnostics for one session.
    /// </summary>
    public async Task<SessionTransportHealthViewModel?> GetSessionTransportHealthAsync(
        string sessionId,
        string? provider = null,
        CancellationToken cancellationToken = default)
        => await GetJsonAsync<SessionTransportHealthViewModel>(
            BuildTransportHealthQuery(sessionId, provider),
            cancellationToken);

    /// <summary>
    /// Reads transport readiness for one session route using server-side policy.
    /// </summary>
    public async Task<SessionTransportReadinessViewModel?> GetSessionTransportReadinessAsync(
        string sessionId,
        string? provider = null,
        CancellationToken cancellationToken = default)
        => await GetJsonAsync<SessionTransportReadinessViewModel>(
            BuildTransportReadinessQuery(sessionId, provider),
            cancellationToken);

    /// <summary>
    /// Reads session-scoped WebRTC signaling messages.
    /// </summary>
    public async Task<IReadOnlyList<WebRtcSignalMessageViewModel>?> GetWebRtcSignalsAsync(
        string sessionId,
        string? recipientPeerId = null,
        int? afterSequence = null,
        int? limit = null,
        CancellationToken cancellationToken = default)
        => await GetJsonAsync<IReadOnlyList<WebRtcSignalMessageViewModel>>(
            BuildWebRtcSignalsQuery(sessionId, recipientPeerId, afterSequence, limit),
            cancellationToken);

    /// <summary>
    /// Publishes one WebRTC signaling message for the specified session.
    /// </summary>
    public async Task<WebRtcSignalMessageViewModel?> PublishWebRtcSignalAsync(
        string sessionId,
        WebRtcSignalPublishRequestViewModel body,
        CancellationToken cancellationToken = default)
        => await SendJsonAsync<WebRtcSignalMessageViewModel>(
            HttpMethod.Post,
            $"/api/v1/sessions/{Uri.EscapeDataString(sessionId)}/transport/webrtc/signals",
            body,
            cancellationToken);

    /// <summary>
    /// Marks one WebRTC relay peer online for the specified session.
    /// </summary>
    public async Task<WebRtcPeerStateViewModel?> MarkWebRtcPeerOnlineAsync(
        string sessionId,
        string peerId,
        WebRtcPeerPresenceRequestViewModel? body = null,
        CancellationToken cancellationToken = default)
        => await SendJsonAsync<WebRtcPeerStateViewModel>(
            HttpMethod.Post,
            $"/api/v1/sessions/{Uri.EscapeDataString(sessionId)}/transport/webrtc/peers/{Uri.EscapeDataString(peerId)}/online",
            body ?? new WebRtcPeerPresenceRequestViewModel(),
            cancellationToken);

    /// <summary>
    /// Marks one WebRTC relay peer offline for the specified session.
    /// </summary>
    public async Task<WebRtcPeerStateViewModel?> MarkWebRtcPeerOfflineAsync(
        string sessionId,
        string peerId,
        CancellationToken cancellationToken = default)
        => await SendJsonAsync<WebRtcPeerStateViewModel>(
            HttpMethod.Post,
            $"/api/v1/sessions/{Uri.EscapeDataString(sessionId)}/transport/webrtc/peers/{Uri.EscapeDataString(peerId)}/offline",
            new { },
            cancellationToken);

    /// <summary>
    /// Lists WebRTC relay peers for one session.
    /// </summary>
    public async Task<IReadOnlyList<WebRtcPeerStateViewModel>?> GetWebRtcPeersAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
        => await GetJsonAsync<IReadOnlyList<WebRtcPeerStateViewModel>>(
            $"/api/v1/sessions/{Uri.EscapeDataString(sessionId)}/transport/webrtc/peers",
            cancellationToken);

    /// <summary>
    /// Lists queued WebRTC relay command envelopes for one peer.
    /// </summary>
    public async Task<IReadOnlyList<WebRtcCommandEnvelopeViewModel>?> GetWebRtcCommandsAsync(
        string sessionId,
        string peerId,
        int? afterSequence = null,
        int? limit = null,
        CancellationToken cancellationToken = default)
        => await GetJsonAsync<IReadOnlyList<WebRtcCommandEnvelopeViewModel>>(
            BuildWebRtcCommandsQuery(sessionId, peerId, afterSequence, limit),
            cancellationToken);

    /// <summary>
    /// Publishes one command acknowledgment for the WebRTC relay path.
    /// </summary>
    public async Task<WebRtcCommandAckPublishResultViewModel?> PublishWebRtcCommandAckAsync(
        string sessionId,
        string commandId,
        WebRtcCommandAckPublishRequestViewModel body,
        CancellationToken cancellationToken = default)
        => await SendJsonAsync<WebRtcCommandAckPublishResultViewModel>(
            HttpMethod.Post,
            $"/api/v1/sessions/{Uri.EscapeDataString(sessionId)}/transport/webrtc/commands/{Uri.EscapeDataString(commandId)}/ack",
            body,
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
    /// Ensures control lease using server-side session resolution and bounded retry policy.
    /// </summary>
    public async Task<SessionControlEnsureResultViewModel?> EnsureControlAsync(
        string sessionId,
        SessionControlEnsureRequestViewModel body,
        CancellationToken cancellationToken = default)
        => await SendJsonAsync<SessionControlEnsureResultViewModel>(
            HttpMethod.Post,
            $"/api/v1/sessions/{Uri.EscapeDataString(sessionId)}/control/ensure",
            body,
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
        using var response = await SendAsync(() => _httpClient.DeleteAsync($"/api/v1/diagnostics/policies/scopes/{Uri.EscapeDataString(scopeId)}", cancellationToken), $"/api/v1/diagnostics/policies/scopes/{scopeId}", cancellationToken);
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
        using var response = await SendAsync(() => _httpClient.DeleteAsync($"/api/v1/diagnostics/policies/assignments/{Uri.EscapeDataString(assignmentId)}", cancellationToken), $"/api/v1/diagnostics/policies/assignments/{assignmentId}", cancellationToken);
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

    private static string BuildTransportHealthQuery(string sessionId, string? provider)
    {
        var route = $"/api/v1/sessions/{Uri.EscapeDataString(sessionId)}/transport/health";
        if (string.IsNullOrWhiteSpace(provider))
        {
            return route;
        }

        return $"{route}?provider={Uri.EscapeDataString(provider)}";
    }

    /// <summary>
    /// Builds session transport-readiness route with optional provider override query.
    /// </summary>
    private static string BuildTransportReadinessQuery(string sessionId, string? provider)
    {
        var route = $"/api/v1/sessions/{Uri.EscapeDataString(sessionId)}/transport/readiness";
        if (string.IsNullOrWhiteSpace(provider))
        {
            return route;
        }

        return $"{route}?provider={Uri.EscapeDataString(provider)}";
    }

    private static string BuildWebRtcSignalsQuery(
        string sessionId,
        string? recipientPeerId,
        int? afterSequence,
        int? limit)
    {
        var query = new List<string>();
        if (!string.IsNullOrWhiteSpace(recipientPeerId))
        {
            query.Add($"recipientPeerId={Uri.EscapeDataString(recipientPeerId)}");
        }

        if (afterSequence.HasValue)
        {
            query.Add($"afterSequence={afterSequence.Value}");
        }

        if (limit.HasValue)
        {
            query.Add($"limit={limit.Value}");
        }

        var route = $"/api/v1/sessions/{Uri.EscapeDataString(sessionId)}/transport/webrtc/signals";
        return query.Count == 0 ? route : $"{route}?{string.Join("&", query)}";
    }

    private static string BuildWebRtcCommandsQuery(
        string sessionId,
        string peerId,
        int? afterSequence,
        int? limit)
    {
        var query = new List<string>
        {
            $"peerId={Uri.EscapeDataString(peerId)}",
        };

        if (afterSequence.HasValue)
        {
            query.Add($"afterSequence={afterSequence.Value}");
        }

        if (limit.HasValue)
        {
            query.Add($"limit={limit.Value}");
        }

        var route = $"/api/v1/sessions/{Uri.EscapeDataString(sessionId)}/transport/webrtc/commands";
        return $"{route}?{string.Join("&", query)}";
    }

    private async Task<TResponse?> GetJsonAsync<TResponse>(
        string relativeUri,
        CancellationToken cancellationToken)
    {
        using var response = await SendAsync(() => _httpClient.GetAsync(relativeUri, cancellationToken), relativeUri, cancellationToken);
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

        using var response = await SendAsync(() => _httpClient.SendAsync(request, cancellationToken), relativeUri, cancellationToken);
        await EnsureSuccessAsync(response, relativeUri, cancellationToken);

        return await response.Content.ReadFromJsonAsync<TResponse>(cancellationToken);
    }

    private static async Task<HttpResponseMessage> SendAsync(
        Func<Task<HttpResponseMessage>> send,
        string relativeUri,
        CancellationToken cancellationToken)
    {
        try
        {
            return await send();
        }
        catch (HttpRequestException exception)
        {
            throw new ControlPlaneApiUnavailableException(relativeUri, exception);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
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

        ControlArbitrationConflictViewModel? controlConflict = null;
        if ((int)response.StatusCode == 409 && !string.IsNullOrWhiteSpace(rawBody))
        {
            controlConflict = JsonSerializer.Deserialize<ControlArbitrationConflictViewModel>(rawBody, AuthorizationJsonOptions);
            if (string.IsNullOrWhiteSpace(controlConflict?.Code))
            {
                controlConflict = null;
            }
        }

        throw new ControlPlaneApiException(response.StatusCode, rawBody, denial, controlConflict);
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
        AuthorizationDeniedViewModel? authorizationDenied = null,
        ControlArbitrationConflictViewModel? controlConflict = null)
        : base(BuildMessage(statusCode, rawBody, authorizationDenied, controlConflict))
    {
        StatusCode = statusCode;
        RawBody = rawBody;
        AuthorizationDenied = authorizationDenied;
        ControlConflict = controlConflict;
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

    /// <summary>
    /// Gets the structured control-arbitration conflict when the backend returned one.
    /// </summary>
    public ControlArbitrationConflictViewModel? ControlConflict { get; }

    private static string BuildMessage(
        System.Net.HttpStatusCode statusCode,
        string rawBody,
        AuthorizationDeniedViewModel? authorizationDenied,
        ControlArbitrationConflictViewModel? controlConflict)
    {
        if (controlConflict is not null)
        {
            var conflictSegments = new List<string>
            {
                $"{controlConflict.Error} ({controlConflict.Code})",
            };

            if (!string.IsNullOrWhiteSpace(controlConflict.RequestedParticipantId))
            {
                conflictSegments.Add($"requested participant: {controlConflict.RequestedParticipantId}");
            }

            if (!string.IsNullOrWhiteSpace(controlConflict.ActedBy))
            {
                conflictSegments.Add($"acted by: {controlConflict.ActedBy}");
            }

            if (controlConflict.CurrentController is not null)
            {
                if (!string.IsNullOrWhiteSpace(controlConflict.CurrentController.PrincipalId))
                {
                    conflictSegments.Add($"current controller: {controlConflict.CurrentController.PrincipalId}");
                }

                if (controlConflict.CurrentController.ExpiresAtUtc.HasValue)
                {
                    conflictSegments.Add($"lease expires: {controlConflict.CurrentController.ExpiresAtUtc.Value:O}");
                }
            }

            return string.Join(" | ", conflictSegments);
        }

        if (authorizationDenied is null)
        {
            return $"ControlPlane request failed with status {(int)statusCode}: {rawBody}";
        }

        var denialSegments = new List<string>
        {
            $"{authorizationDenied.Message} ({authorizationDenied.Code})",
        };

        if (authorizationDenied.RequiredRoles is { Count: > 0 })
        {
            denialSegments.Add($"required roles: {string.Join(", ", authorizationDenied.RequiredRoles)}");
        }

        if (!string.IsNullOrWhiteSpace(authorizationDenied.RequiredTenantId)
            || !string.IsNullOrWhiteSpace(authorizationDenied.RequiredOrganizationId))
        {
            denialSegments.Add(
                $"required scope: tenant={authorizationDenied.RequiredTenantId ?? "any"}, org={authorizationDenied.RequiredOrganizationId ?? "any"}");
        }

        return string.Join(" | ", denialSegments);
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

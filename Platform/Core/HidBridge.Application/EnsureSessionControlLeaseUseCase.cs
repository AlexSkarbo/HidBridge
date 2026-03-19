using HidBridge.Abstractions;
using HidBridge.Contracts;

namespace HidBridge.Application;

/// <summary>
/// Ensures an effective session exists and acquires a control lease with bounded retry semantics.
/// </summary>
public sealed class EnsureSessionControlLeaseUseCase
{
    private readonly ISessionStore _sessionStore;
    private readonly ISessionOrchestrator _orchestrator;
    private readonly IConnectorRegistry _connectorRegistry;
    private readonly WebRtcCommandRelayService _relayService;
    private readonly IEventWriter _eventWriter;

    /// <summary>
    /// Creates the ensure-control use case.
    /// </summary>
    public EnsureSessionControlLeaseUseCase(
        ISessionStore sessionStore,
        ISessionOrchestrator orchestrator,
        IConnectorRegistry connectorRegistry,
        WebRtcCommandRelayService relayService,
        IEventWriter eventWriter)
    {
        _sessionStore = sessionStore;
        _orchestrator = orchestrator;
        _connectorRegistry = connectorRegistry;
        _relayService = relayService;
        _eventWriter = eventWriter;
    }

    /// <summary>
    /// Ensures control lease for the supplied session/request.
    /// </summary>
    /// <remarks>
    /// Strategy:
    /// 1) reuse requested session when it exists;
    /// 2) optionally reuse a live relay session on same endpoint;
    /// 3) optionally create missing session;
    /// 4) request lease and retry once when creation resolved a not-found race.
    /// </remarks>
    public async Task<SessionControlEnsureResultBody> ExecuteAsync(
        string requestedSessionId,
        SessionControlEnsureBody request,
        CancellationToken cancellationToken)
    {
        var effectiveSessionId = requestedSessionId;
        var sessionCreated = false;
        var sessionReused = false;

        var snapshots = await _sessionStore.ListAsync(cancellationToken);
        var existingSnapshot = snapshots.FirstOrDefault(x =>
            string.Equals(x.SessionId, requestedSessionId, StringComparison.OrdinalIgnoreCase));
        if (existingSnapshot is null)
        {
            if (request.PreferLiveRelaySession)
            {
                var endpointId = ResolveEndpointId(request, existingSnapshot);
                var liveRelaySessionId = FindLiveRelaySessionId(snapshots, endpointId);
                if (!string.IsNullOrWhiteSpace(liveRelaySessionId))
                {
                    effectiveSessionId = liveRelaySessionId;
                    sessionReused = true;
                    existingSnapshot = snapshots.FirstOrDefault(x =>
                        string.Equals(x.SessionId, effectiveSessionId, StringComparison.OrdinalIgnoreCase));
                }
            }

            if (existingSnapshot is null && request.AutoCreateSessionIfMissing)
            {
                effectiveSessionId = requestedSessionId;
                await EnsureSessionExistsAsync(effectiveSessionId, request, existingSnapshot, cancellationToken);
                sessionCreated = true;
            }
        }

        var leaseRequest = new SessionControlRequestBody(
            ParticipantId: request.ParticipantId,
            RequestedBy: request.RequestedBy,
            LeaseSeconds: request.LeaseSeconds,
            Reason: request.Reason,
            TenantId: request.TenantId,
            OrganizationId: request.OrganizationId,
            OperatorRoles: request.OperatorRoles);

        var leaseResult = await RequestLeaseWithRetryAsync(
            effectiveSessionId,
            leaseRequest,
            request,
            cancellationToken);
        sessionCreated = sessionCreated || leaseResult.SessionCreated;
        var resolvedAtUtc = DateTimeOffset.UtcNow;

        await _eventWriter.WriteAuditAsync(
            new AuditEventBody(
                Category: "session.control.ensure",
                Message: $"Control lease ensured for session {effectiveSessionId}.",
                SessionId: effectiveSessionId,
                Data: new Dictionary<string, object?>
                {
                    ["requestedSessionId"] = requestedSessionId,
                    ["effectiveSessionId"] = effectiveSessionId,
                    ["sessionCreated"] = sessionCreated,
                    ["sessionReused"] = sessionReused,
                    ["participantId"] = request.ParticipantId,
                    ["requestedBy"] = request.RequestedBy,
                    ["leaseSeconds"] = request.LeaseSeconds,
                    ["endpointId"] = request.EndpointId,
                },
                CreatedAtUtc: resolvedAtUtc),
            cancellationToken);

        return new SessionControlEnsureResultBody(
            RequestedSessionId: requestedSessionId,
            EffectiveSessionId: effectiveSessionId,
            Lease: leaseResult.Lease,
            SessionCreated: sessionCreated,
            SessionReused: sessionReused,
            ResolvedAtUtc: resolvedAtUtc);
    }

    /// <summary>
    /// Requests lease and retries once by creating missing session when enabled.
    /// </summary>
    private async Task<(SessionControlLeaseBody Lease, bool SessionCreated)> RequestLeaseWithRetryAsync(
        string sessionId,
        SessionControlRequestBody leaseRequest,
        SessionControlEnsureBody ensureRequest,
        CancellationToken cancellationToken)
    {
        try
        {
            return (await _orchestrator.RequestControlAsync(sessionId, leaseRequest, cancellationToken), false);
        }
        catch (KeyNotFoundException) when (ensureRequest.AutoCreateSessionIfMissing)
        {
            var sessionCreated = false;
            if (!await SessionExistsAsync(sessionId, cancellationToken))
            {
                await EnsureSessionExistsAsync(sessionId, ensureRequest, existingSnapshot: null, cancellationToken);
                sessionCreated = true;
            }

            return (await _orchestrator.RequestControlAsync(sessionId, leaseRequest, cancellationToken), sessionCreated);
        }
    }

    /// <summary>
    /// Returns true when one session snapshot currently exists for the supplied id.
    /// </summary>
    private async Task<bool> SessionExistsAsync(string sessionId, CancellationToken cancellationToken)
    {
        return (await _sessionStore.ListAsync(cancellationToken))
            .Any(snapshot => string.Equals(snapshot.SessionId, sessionId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Creates a missing session for the requested endpoint route.
    /// </summary>
    private async Task EnsureSessionExistsAsync(
        string sessionId,
        SessionControlEnsureBody request,
        SessionSnapshot? existingSnapshot,
        CancellationToken cancellationToken)
    {
        var endpointId = ResolveEndpointId(request, existingSnapshot);
        if (string.IsNullOrWhiteSpace(endpointId))
        {
            throw new InvalidOperationException("EndpointId is required to auto-create a missing session.");
        }

        var agentId = await ResolveAgentIdForEndpointAsync(endpointId, cancellationToken);
        var openRequest = new SessionOpenBody(
            SessionId: sessionId,
            Profile: request.Profile,
            RequestedBy: request.RequestedBy,
            TargetAgentId: agentId,
            TargetEndpointId: endpointId,
            ShareMode: SessionRole.Owner,
            TenantId: request.TenantId,
            OrganizationId: request.OrganizationId,
            OperatorRoles: request.OperatorRoles,
            TransportProvider: RealtimeTransportProvider.WebRtcDataChannel.ToString());
        _ = await _orchestrator.OpenAsync(openRequest, cancellationToken);
    }

    /// <summary>
    /// Resolves endpoint identifier from request/session context.
    /// </summary>
    private static string? ResolveEndpointId(SessionControlEnsureBody request, SessionSnapshot? existingSnapshot)
        => !string.IsNullOrWhiteSpace(request.EndpointId)
            ? request.EndpointId
            : existingSnapshot?.EndpointId;

    /// <summary>
    /// Finds newest non-terminal session on endpoint that has at least one live relay peer.
    /// </summary>
    private string? FindLiveRelaySessionId(IReadOnlyList<SessionSnapshot> snapshots, string? endpointId)
    {
        if (string.IsNullOrWhiteSpace(endpointId))
        {
            return null;
        }

        return snapshots
            .Where(snapshot =>
                string.Equals(snapshot.EndpointId, endpointId, StringComparison.OrdinalIgnoreCase)
                && snapshot.State is not (SessionState.Ended or SessionState.Failed)
                && _relayService.HasOnlinePeer(snapshot.SessionId, endpointId))
            .OrderByDescending(snapshot => snapshot.UpdatedAtUtc)
            .Select(snapshot => snapshot.SessionId)
            .FirstOrDefault();
    }

    /// <summary>
    /// Resolves target agent for endpoint from active connector registry or latest historical session.
    /// </summary>
    private async Task<string> ResolveAgentIdForEndpointAsync(string endpointId, CancellationToken cancellationToken)
    {
        var connectorAgentId = (await _connectorRegistry.ListAsync(cancellationToken))
            .FirstOrDefault(descriptor =>
                string.Equals(descriptor.EndpointId, endpointId, StringComparison.OrdinalIgnoreCase))
            ?.AgentId;
        if (!string.IsNullOrWhiteSpace(connectorAgentId))
        {
            return connectorAgentId;
        }

        var fallbackAgentId = (await _sessionStore.ListAsync(cancellationToken))
            .Where(snapshot => string.Equals(snapshot.EndpointId, endpointId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(snapshot => snapshot.UpdatedAtUtc)
            .Select(snapshot => snapshot.AgentId)
            .FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(fallbackAgentId))
        {
            return fallbackAgentId;
        }

        throw new InvalidOperationException($"No agent was found for endpoint '{endpointId}'.");
    }
}

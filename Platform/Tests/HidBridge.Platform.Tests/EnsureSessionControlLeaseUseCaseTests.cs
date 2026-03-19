using HidBridge.Abstractions;
using HidBridge.Application;
using HidBridge.Contracts;
using Xunit;

namespace HidBridge.Platform.Tests;

/// <summary>
/// Verifies server-side ensure/reuse/create lease orchestration without script-side policy.
/// </summary>
public sealed class EnsureSessionControlLeaseUseCaseTests
{
    /// <summary>
    /// Uses the requested session directly when it already exists.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_UsesRequestedSession_WhenSessionExists()
    {
        var sessionStore = new TestSessionStore();
        await sessionStore.UpsertAsync(CreateSession("session-existing", "agent-1", "endpoint-1"), TestContext.Current.CancellationToken);
        var orchestrator = new RecordingSessionOrchestrator(sessionStore);
        var connectorRegistry = new StubConnectorRegistry([]);
        var relay = new WebRtcCommandRelayService();
        var useCase = new EnsureSessionControlLeaseUseCase(sessionStore, orchestrator, connectorRegistry, relay);

        var result = await useCase.ExecuteAsync(
            "session-existing",
            CreateEnsureRequest(endpointId: "endpoint-1"),
            TestContext.Current.CancellationToken);

        Assert.Equal("session-existing", result.EffectiveSessionId);
        Assert.False(result.SessionCreated);
        Assert.False(result.SessionReused);
        Assert.Single(orchestrator.RequestControlCalls);
        Assert.Equal("session-existing", orchestrator.RequestControlCalls[0]);
        Assert.Empty(orchestrator.OpenCalls);
    }

    /// <summary>
    /// Reuses one live relay session on the same endpoint when requested session is missing.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_ReusesLiveRelaySession_WhenRequestedSessionIsMissing()
    {
        var sessionStore = new TestSessionStore();
        await sessionStore.UpsertAsync(CreateSession("session-live", "agent-1", "endpoint-1"), TestContext.Current.CancellationToken);
        var orchestrator = new RecordingSessionOrchestrator(sessionStore);
        var connectorRegistry = new StubConnectorRegistry([]);
        var relay = new WebRtcCommandRelayService();
        _ = await relay.MarkPeerOnlineAsync(
            "session-live",
            "peer-1",
            "endpoint-1",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["state"] = "Connected",
            },
            TestContext.Current.CancellationToken);
        var useCase = new EnsureSessionControlLeaseUseCase(sessionStore, orchestrator, connectorRegistry, relay);

        var result = await useCase.ExecuteAsync(
            "session-missing",
            CreateEnsureRequest(endpointId: "endpoint-1"),
            TestContext.Current.CancellationToken);

        Assert.Equal("session-live", result.EffectiveSessionId);
        Assert.True(result.SessionReused);
        Assert.False(result.SessionCreated);
        Assert.Single(orchestrator.RequestControlCalls);
        Assert.Equal("session-live", orchestrator.RequestControlCalls[0]);
        Assert.Empty(orchestrator.OpenCalls);
    }

    /// <summary>
    /// Auto-creates one missing session before requesting control lease.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_CreatesMissingSession_WhenNoLiveRelaySessionExists()
    {
        var sessionStore = new TestSessionStore();
        var orchestrator = new RecordingSessionOrchestrator(sessionStore);
        var connectorRegistry = new StubConnectorRegistry(
            [
                new ConnectorDescriptor(
                    AgentId: "agent-1",
                    EndpointId: "endpoint-1",
                    ConnectorType: ConnectorType.HidBridge,
                    Capabilities: []),
            ]);
        var relay = new WebRtcCommandRelayService();
        var useCase = new EnsureSessionControlLeaseUseCase(sessionStore, orchestrator, connectorRegistry, relay);

        var result = await useCase.ExecuteAsync(
            "session-created",
            CreateEnsureRequest(endpointId: "endpoint-1"),
            TestContext.Current.CancellationToken);

        Assert.Equal("session-created", result.EffectiveSessionId);
        Assert.True(result.SessionCreated);
        Assert.False(result.SessionReused);
        Assert.Single(orchestrator.OpenCalls);
        Assert.Equal("session-created", orchestrator.OpenCalls[0].SessionId);
        Assert.Equal("WebRtcDataChannel", orchestrator.OpenCalls[0].TransportProvider);
        Assert.Single(orchestrator.RequestControlCalls);
        Assert.Equal("session-created", orchestrator.RequestControlCalls[0]);
    }

    /// <summary>
    /// Retries the control request once after auto-creating a missing session.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_RetriesControlRequestOnce_AfterAutoCreate()
    {
        var sessionStore = new TestSessionStore();
        var orchestrator = new RecordingSessionOrchestrator(sessionStore)
        {
            FailFirstRequestControlWithNotFound = true,
        };
        var connectorRegistry = new StubConnectorRegistry(
            [
                new ConnectorDescriptor(
                    AgentId: "agent-1",
                    EndpointId: "endpoint-1",
                    ConnectorType: ConnectorType.HidBridge,
                    Capabilities: []),
            ]);
        var relay = new WebRtcCommandRelayService();
        var useCase = new EnsureSessionControlLeaseUseCase(sessionStore, orchestrator, connectorRegistry, relay);

        var result = await useCase.ExecuteAsync(
            "session-retry",
            CreateEnsureRequest(endpointId: "endpoint-1"),
            TestContext.Current.CancellationToken);

        Assert.Equal("session-retry", result.EffectiveSessionId);
        Assert.True(result.SessionCreated);
        Assert.Equal(2, orchestrator.RequestControlCalls.Count);
        Assert.Single(orchestrator.OpenCalls);
    }

    /// <summary>
    /// Creates one default ensure request for tests.
    /// </summary>
    private static SessionControlEnsureBody CreateEnsureRequest(string endpointId)
        => new(
            ParticipantId: "owner:smoke-runner",
            RequestedBy: "smoke-runner",
            EndpointId: endpointId,
            Profile: SessionProfile.UltraLowLatency,
            LeaseSeconds: 120,
            Reason: "test",
            AutoCreateSessionIfMissing: true,
            PreferLiveRelaySession: true,
            TenantId: "local-tenant",
            OrganizationId: "local-org",
            OperatorRoles: ["operator.viewer", "operator.moderator", "operator.admin"]);

    /// <summary>
    /// Creates one active session snapshot.
    /// </summary>
    private static SessionSnapshot CreateSession(string sessionId, string agentId, string endpointId)
    {
        var now = DateTimeOffset.UtcNow;
        return new SessionSnapshot(
            SessionId: sessionId,
            AgentId: agentId,
            EndpointId: endpointId,
            Profile: SessionProfile.UltraLowLatency,
            RequestedBy: "smoke-runner",
            Role: SessionRole.Owner,
            State: SessionState.Active,
            UpdatedAtUtc: now,
            Participants:
            [
                new SessionParticipantSnapshot("owner:smoke-runner", "smoke-runner", SessionRole.Owner, now, now),
            ],
            Shares: [],
            ControlLease: null,
            LastHeartbeatAtUtc: now,
            LeaseExpiresAtUtc: now.AddMinutes(1),
            TenantId: "local-tenant",
            OrganizationId: "local-org");
    }

    /// <summary>
    /// Minimal in-memory session store for use-case tests.
    /// </summary>
    private sealed class TestSessionStore : ISessionStore
    {
        private readonly List<SessionSnapshot> _items = [];

        public Task UpsertAsync(SessionSnapshot snapshot, CancellationToken cancellationToken)
        {
            _items.RemoveAll(x => string.Equals(x.SessionId, snapshot.SessionId, StringComparison.OrdinalIgnoreCase));
            _items.Add(snapshot);
            return Task.CompletedTask;
        }

        public Task RemoveAsync(string sessionId, CancellationToken cancellationToken)
        {
            _items.RemoveAll(x => string.Equals(x.SessionId, sessionId, StringComparison.OrdinalIgnoreCase));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<SessionSnapshot>> ListAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<SessionSnapshot>>(_items.ToArray());
    }

    /// <summary>
    /// Session orchestrator stub that records open/control calls.
    /// </summary>
    private sealed class RecordingSessionOrchestrator : ISessionOrchestrator
    {
        private readonly TestSessionStore _sessionStore;
        private int _requestControlAttempts;

        public RecordingSessionOrchestrator(TestSessionStore sessionStore)
        {
            _sessionStore = sessionStore;
        }

        public bool FailFirstRequestControlWithNotFound { get; set; }

        public List<SessionOpenBody> OpenCalls { get; } = [];

        public List<string> RequestControlCalls { get; } = [];

        public async Task<SessionOpenBody> OpenAsync(SessionOpenBody request, CancellationToken cancellationToken)
        {
            OpenCalls.Add(request);
            await _sessionStore.UpsertAsync(
                CreateSession(request.SessionId, request.TargetAgentId, request.TargetEndpointId ?? "endpoint-unknown"),
                cancellationToken);
            return request;
        }

        public Task<SessionControlLeaseBody> RequestControlAsync(string sessionId, SessionControlRequestBody request, CancellationToken cancellationToken)
        {
            RequestControlCalls.Add(sessionId);
            _requestControlAttempts++;

            if (FailFirstRequestControlWithNotFound && _requestControlAttempts == 1)
            {
                throw new KeyNotFoundException($"Session {sessionId} was not found.");
            }

            var now = DateTimeOffset.UtcNow;
            return Task.FromResult(new SessionControlLeaseBody(
                ParticipantId: request.ParticipantId,
                PrincipalId: request.RequestedBy,
                GrantedBy: request.RequestedBy,
                GrantedAtUtc: now,
                ExpiresAtUtc: now.AddSeconds(request.LeaseSeconds ?? 120)));
        }

        public Task<SessionCloseBody> CloseAsync(SessionCloseBody request, CancellationToken cancellationToken) => throw new NotImplementedException();

        public Task<CommandAckBody> DispatchAsync(CommandRequestBody request, CancellationToken cancellationToken) => throw new NotImplementedException();

        public Task<IReadOnlyList<SessionParticipantBody>> ListParticipantsAsync(string sessionId, CancellationToken cancellationToken) => throw new NotImplementedException();

        public Task<SessionParticipantBody> UpsertParticipantAsync(string sessionId, SessionParticipantUpsertBody request, CancellationToken cancellationToken) => throw new NotImplementedException();

        public Task<SessionParticipantRemoveBody> RemoveParticipantAsync(string sessionId, SessionParticipantRemoveBody request, CancellationToken cancellationToken) => throw new NotImplementedException();

        public Task<IReadOnlyList<SessionShareBody>> ListSharesAsync(string sessionId, CancellationToken cancellationToken) => throw new NotImplementedException();

        public Task<SessionShareBody> GrantShareAsync(string sessionId, SessionShareGrantBody request, CancellationToken cancellationToken) => throw new NotImplementedException();

        public Task<SessionShareBody> RequestInvitationAsync(string sessionId, SessionInvitationRequestBody request, CancellationToken cancellationToken) => throw new NotImplementedException();

        public Task<SessionShareBody> ApproveInvitationAsync(string sessionId, SessionInvitationDecisionBody request, CancellationToken cancellationToken) => throw new NotImplementedException();

        public Task<SessionShareBody> DeclineInvitationAsync(string sessionId, SessionInvitationDecisionBody request, CancellationToken cancellationToken) => throw new NotImplementedException();

        public Task<SessionShareBody> AcceptShareAsync(string sessionId, SessionShareTransitionBody request, CancellationToken cancellationToken) => throw new NotImplementedException();

        public Task<SessionShareBody> RejectShareAsync(string sessionId, SessionShareTransitionBody request, CancellationToken cancellationToken) => throw new NotImplementedException();

        public Task<SessionShareBody> RevokeShareAsync(string sessionId, SessionShareTransitionBody request, CancellationToken cancellationToken) => throw new NotImplementedException();

        public Task<SessionControlLeaseBody?> GetControlLeaseAsync(string sessionId, CancellationToken cancellationToken) => throw new NotImplementedException();

        public Task<SessionControlLeaseBody> GrantControlAsync(string sessionId, SessionControlGrantBody request, CancellationToken cancellationToken) => throw new NotImplementedException();

        public Task<SessionControlLeaseBody?> ReleaseControlAsync(string sessionId, SessionControlReleaseBody request, CancellationToken cancellationToken) => throw new NotImplementedException();

        public Task<SessionControlLeaseBody> ForceTakeoverControlAsync(string sessionId, SessionControlGrantBody request, CancellationToken cancellationToken) => throw new NotImplementedException();
    }

    /// <summary>
    /// Connector registry stub used to resolve endpoint-to-agent mapping.
    /// </summary>
    private sealed class StubConnectorRegistry : IConnectorRegistry
    {
        private readonly IReadOnlyList<ConnectorDescriptor> _descriptors;

        public StubConnectorRegistry(IReadOnlyList<ConnectorDescriptor> descriptors)
        {
            _descriptors = descriptors;
        }

        public Task RegisterAsync(IConnector connector, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyList<ConnectorDescriptor>> ListAsync(CancellationToken cancellationToken)
            => Task.FromResult(_descriptors);

        public Task<IConnector?> ResolveAsync(string agentId, CancellationToken cancellationToken)
            => Task.FromResult<IConnector?>(null);
    }
}

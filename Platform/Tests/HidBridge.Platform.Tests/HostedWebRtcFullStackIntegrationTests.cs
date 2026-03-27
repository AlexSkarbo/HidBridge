using HidBridge.Abstractions;
using HidBridge.Application;
using HidBridge.Contracts;
using HidBridge.SessionOrchestrator;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace HidBridge.Platform.Tests;

/// <summary>
/// Hosted integration coverage for API-side lease orchestration + relay/media readiness projection.
/// </summary>
public sealed class HostedWebRtcFullStackIntegrationTests
{
    [Fact]
    public async Task HostedPipeline_HappyPath_EnsuresLease_AndProjectsReadyMediaState()
    {
        using var host = BuildHost(throwLeaseConflict: false);
        await host.StartAsync(TestContext.Current.CancellationToken);

        var ensure = host.Services.GetRequiredService<EnsureSessionControlLeaseUseCase>();
        var relay = host.Services.GetRequiredService<WebRtcCommandRelayService>();
        var mediaRegistry = host.Services.GetRequiredService<SessionMediaRegistryService>();
        var readiness = host.Services.GetRequiredService<WebRtcRelayReadinessService>();
        var audits = host.Services.GetRequiredService<RecordingEventWriter>();

        var ensured = await ensure.ExecuteAsync(
            "hosted-webrtc-happy",
            CreateEnsureRequest(endpointId: "endpoint-1"),
            TestContext.Current.CancellationToken);

        await relay.MarkPeerOnlineAsync(
            ensured.EffectiveSessionId,
            "peer-hosted-1",
            "endpoint-1",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["state"] = "Connected",
                ["mediaReady"] = "true",
                ["mediaState"] = "Ready",
                ["mediaStreamId"] = "edge-main",
                ["mediaSource"] = "edge-capture",
            },
            TestContext.Current.CancellationToken);

        await mediaRegistry.UpsertAsync(
            ensured.EffectiveSessionId,
            new SessionMediaStreamRegistrationBody(
                PeerId: "peer-hosted-1",
                EndpointId: "endpoint-1",
                StreamId: "edge-main",
                Ready: true,
                State: "Ready",
                ReportedAtUtc: DateTimeOffset.UtcNow,
                FailureReason: null,
                Source: "edge-capture",
                PlaybackUrl: "http://127.0.0.1:18110/media/edge-main",
                StreamKind: "audio-video",
                Video: new MediaVideoDescriptorBody("h264", 1280, 720, 30d, 2500),
                Audio: new MediaAudioDescriptorBody("opus", 2, 48000, 128)),
            TestContext.Current.CancellationToken);

        var snapshot = await readiness.EvaluateAsync(
            ensured.EffectiveSessionId,
            RealtimeTransportProvider.WebRtcDataChannel,
            TestContext.Current.CancellationToken);

        Assert.True(snapshot.Ready);
        Assert.Equal("ready", snapshot.ReasonCode);
        Assert.Equal("edge-main", snapshot.MediaStreamId);
        Assert.Equal("edge-capture", snapshot.MediaSource);
        Assert.Equal("http://127.0.0.1:18110/media/edge-main", snapshot.MediaPlaybackUrl);
        Assert.Contains(audits.AuditEvents, x => x.Category == "session.control.ensure");
    }

    [Fact]
    public async Task HostedPipeline_LeaseConflict_PropagatesDeterministicConflictCode()
    {
        using var host = BuildHost(throwLeaseConflict: true, seedSession: true);
        await host.StartAsync(TestContext.Current.CancellationToken);

        var ensure = host.Services.GetRequiredService<EnsureSessionControlLeaseUseCase>();

        var conflict = await Assert.ThrowsAsync<ControlArbitrationException>(() => ensure.ExecuteAsync(
            "hosted-webrtc-conflict",
            CreateEnsureRequest(endpointId: "endpoint-1"),
            TestContext.Current.CancellationToken));

        Assert.Equal("E_CONTROL_LEASE_HELD_BY_OTHER", conflict.Code);
        Assert.Equal("hosted-webrtc-conflict", conflict.SessionId);
        Assert.Equal("owner:other", conflict.CurrentControllerParticipantId);
    }

    [Fact]
    public async Task HostedPipeline_StrictMediaPolicy_RunningRuntimeWithPlaybackAndTracks_IsReady()
    {
        using var host = BuildHost(
            throwLeaseConflict: false,
            readinessOptions: new WebRtcRelayReadinessOptions
            {
                RequireMediaReady = true,
            },
            healthMetrics: new Dictionary<string, object?>
            {
                ["onlinePeerCount"] = 1,
                ["lastPeerState"] = "Connected",
                ["lastPeerMediaReady"] = true,
                ["lastPeerMediaState"] = "Ready",
                ["lastPeerMediaRuntimeRunning"] = true,
                ["lastPeerMediaRuntimeState"] = "running",
                ["lastPeerMediaSessionEvidence"] = true,
                ["lastPeerMediaRuntimeSessionEvidence"] = true,
                ["lastPeerMediaSessionState"] = "active",
                ["lastPeerMediaVideoTrackState"] = "active",
                ["lastPeerMediaAudioTrackState"] = "active",
                ["lastPeerMediaPlaybackUrl"] = "http://127.0.0.1:18110/media/edge-main",
                ["lastPeerMediaStreamId"] = "edge-main",
                ["lastPeerMediaSource"] = "edge-capture",
            });
        await host.StartAsync(TestContext.Current.CancellationToken);

        var ensure = host.Services.GetRequiredService<EnsureSessionControlLeaseUseCase>();
        var readiness = host.Services.GetRequiredService<WebRtcRelayReadinessService>();
        var ensured = await ensure.ExecuteAsync(
            "hosted-webrtc-strict-media-ready",
            CreateEnsureRequest(endpointId: "endpoint-1"),
            TestContext.Current.CancellationToken);

        var snapshot = await readiness.EvaluateAsync(
            ensured.EffectiveSessionId,
            RealtimeTransportProvider.WebRtcDataChannel,
            TestContext.Current.CancellationToken);

        Assert.True(snapshot.Ready);
        Assert.Equal("ready", snapshot.ReasonCode);
    }

    [Fact]
    public async Task HostedPipeline_StrictMediaPolicy_RuntimeDown_ReturnsRuntimeNotRunning()
    {
        using var host = BuildHost(
            throwLeaseConflict: false,
            readinessOptions: new WebRtcRelayReadinessOptions
            {
                RequireMediaReady = true,
            },
            healthMetrics: new Dictionary<string, object?>
            {
                ["onlinePeerCount"] = 1,
                ["lastPeerState"] = "Connected",
                ["lastPeerMediaReady"] = true,
                ["lastPeerMediaState"] = "Ready",
                ["lastPeerMediaRuntimeRunning"] = false,
                ["lastPeerMediaRuntimeState"] = "degraded",
                ["lastPeerMediaRuntimeFailureReason"] = "ffmpeg exited",
                ["lastPeerMediaPlaybackUrl"] = "http://127.0.0.1:18110/media/edge-main",
            });
        await host.StartAsync(TestContext.Current.CancellationToken);

        var ensure = host.Services.GetRequiredService<EnsureSessionControlLeaseUseCase>();
        var readiness = host.Services.GetRequiredService<WebRtcRelayReadinessService>();
        var ensured = await ensure.ExecuteAsync(
            "hosted-webrtc-strict-runtime-down",
            CreateEnsureRequest(endpointId: "endpoint-1"),
            TestContext.Current.CancellationToken);

        var snapshot = await readiness.EvaluateAsync(
            ensured.EffectiveSessionId,
            RealtimeTransportProvider.WebRtcDataChannel,
            TestContext.Current.CancellationToken);

        Assert.False(snapshot.Ready);
        Assert.Equal("media_runtime_not_running", snapshot.ReasonCode);
    }

    [Fact]
    public async Task HostedPipeline_StrictMediaPolicy_MissingPlaybackUrl_ReturnsPlaybackUrlMissing()
    {
        using var host = BuildHost(
            throwLeaseConflict: false,
            readinessOptions: new WebRtcRelayReadinessOptions
            {
                RequireMediaReady = true,
            },
            healthMetrics: new Dictionary<string, object?>
            {
                ["onlinePeerCount"] = 1,
                ["lastPeerState"] = "Connected",
                ["lastPeerMediaReady"] = true,
                ["lastPeerMediaState"] = "Ready",
                ["lastPeerMediaRuntimeRunning"] = true,
                ["lastPeerMediaRuntimeState"] = "running",
                ["lastPeerMediaSessionEvidence"] = true,
                ["lastPeerMediaRuntimeSessionEvidence"] = true,
                ["lastPeerMediaSessionState"] = "active",
                ["lastPeerMediaVideoTrackState"] = "active",
            });
        await host.StartAsync(TestContext.Current.CancellationToken);

        var ensure = host.Services.GetRequiredService<EnsureSessionControlLeaseUseCase>();
        var readiness = host.Services.GetRequiredService<WebRtcRelayReadinessService>();
        var ensured = await ensure.ExecuteAsync(
            "hosted-webrtc-strict-no-playback",
            CreateEnsureRequest(endpointId: "endpoint-1"),
            TestContext.Current.CancellationToken);

        var snapshot = await readiness.EvaluateAsync(
            ensured.EffectiveSessionId,
            RealtimeTransportProvider.WebRtcDataChannel,
            TestContext.Current.CancellationToken);

        Assert.False(snapshot.Ready);
        Assert.Equal("media_playback_url_missing", snapshot.ReasonCode);
    }

    [Fact]
    public async Task HostedPipeline_StrictMediaPolicy_TrackAttachTimeout_ReturnsVideoTrackUnhealthy()
    {
        using var host = BuildHost(
            throwLeaseConflict: false,
            readinessOptions: new WebRtcRelayReadinessOptions
            {
                RequireMediaReady = true,
            },
            healthMetrics: new Dictionary<string, object?>
            {
                ["onlinePeerCount"] = 1,
                ["lastPeerState"] = "Connected",
                ["lastPeerMediaReady"] = true,
                ["lastPeerMediaState"] = "Ready",
                ["lastPeerMediaRuntimeRunning"] = true,
                ["lastPeerMediaRuntimeState"] = "running",
                ["lastPeerMediaSessionEvidence"] = true,
                ["lastPeerMediaRuntimeSessionEvidence"] = true,
                ["lastPeerMediaSessionState"] = "active",
                ["lastPeerMediaVideoTrackState"] = "missing",
                ["lastPeerMediaAudioTrackState"] = "active",
                ["lastPeerMediaPlaybackUrl"] = "http://127.0.0.1:18110/media/edge-main",
            });
        await host.StartAsync(TestContext.Current.CancellationToken);

        var ensure = host.Services.GetRequiredService<EnsureSessionControlLeaseUseCase>();
        var readiness = host.Services.GetRequiredService<WebRtcRelayReadinessService>();
        var ensured = await ensure.ExecuteAsync(
            "hosted-webrtc-strict-track-timeout",
            CreateEnsureRequest(endpointId: "endpoint-1"),
            TestContext.Current.CancellationToken);

        var snapshot = await readiness.EvaluateAsync(
            ensured.EffectiveSessionId,
            RealtimeTransportProvider.WebRtcDataChannel,
            TestContext.Current.CancellationToken);

        Assert.False(snapshot.Ready);
        Assert.Equal("media_video_track_unhealthy", snapshot.ReasonCode);
    }

    [Fact]
    public async Task HostedPipeline_StrictMediaPolicy_FfmpegDcdRuntimeEvidence_AllowsReadyWhenProbeNotReady()
    {
        using var host = BuildHost(
            throwLeaseConflict: false,
            readinessOptions: new WebRtcRelayReadinessOptions
            {
                RequireMediaReady = true,
                RequireMediaSessionEvidence = true,
            },
            healthMetrics: new Dictionary<string, object?>
            {
                ["onlinePeerCount"] = 1,
                ["lastPeerState"] = "Connected",
                ["lastPeerMediaReady"] = false,
                ["lastPeerMediaState"] = "RuntimeNotReady",
                ["lastPeerMediaRuntimeEngine"] = "ffmpeg-dcd",
                ["lastPeerMediaRuntimeRunning"] = true,
                ["lastPeerMediaRuntimeState"] = "Running",
                ["lastPeerMediaSessionEvidence"] = true,
                ["lastPeerMediaRuntimeSessionEvidence"] = true,
                ["lastPeerMediaSessionState"] = "active",
                ["lastPeerMediaVideoTrackState"] = "active",
                ["lastPeerMediaAudioTrackState"] = "active",
                ["lastPeerMediaPlaybackUrl"] = "http://127.0.0.1:19851/rtc/v1/whep/?app=live&stream=cam21",
            });
        await host.StartAsync(TestContext.Current.CancellationToken);

        var ensure = host.Services.GetRequiredService<EnsureSessionControlLeaseUseCase>();
        var readiness = host.Services.GetRequiredService<WebRtcRelayReadinessService>();
        var ensured = await ensure.ExecuteAsync(
            "hosted-webrtc-strict-runtime-evidence",
            CreateEnsureRequest(endpointId: "endpoint-1"),
            TestContext.Current.CancellationToken);

        var snapshot = await readiness.EvaluateAsync(
            ensured.EffectiveSessionId,
            RealtimeTransportProvider.WebRtcDataChannel,
            TestContext.Current.CancellationToken);

        Assert.True(snapshot.Ready);
        Assert.Equal("ready", snapshot.ReasonCode);
    }

    private static IHost BuildHost(
        bool throwLeaseConflict,
        bool seedSession = false,
        WebRtcRelayReadinessOptions? readinessOptions = null,
        IReadOnlyDictionary<string, object?>? healthMetrics = null)
    {
        var sessionStore = new InMemorySessionStore();
        if (seedSession)
        {
            sessionStore.UpsertAsync(
                CreateSession("hosted-webrtc-conflict", "agent-1", "endpoint-1"),
                CancellationToken.None).GetAwaiter().GetResult();
        }

        return new HostBuilder()
            .ConfigureServices(services =>
            {
                services.AddSingleton<ISessionStore>(sessionStore);
                services.AddSingleton<IConnectorRegistry>(new StaticConnectorRegistry(
                [
                    new ConnectorDescriptor(
                        AgentId: "agent-1",
                        EndpointId: "endpoint-1",
                        ConnectorType: ConnectorType.HidBridge,
                        Capabilities: []),
                ]));
                services.AddSingleton<ISessionOrchestrator>(new HostedSessionOrchestrator(sessionStore, throwLeaseConflict));
                services.AddSingleton<IRealtimeTransportFactory>(new HostedTransportFactory(healthMetrics));
                services.AddSingleton<RecordingEventWriter>();
                services.AddSingleton<IEventWriter>(sp => sp.GetRequiredService<RecordingEventWriter>());
                services.AddSingleton<WebRtcCommandRelayService>();
                services.AddSingleton<SessionMediaRegistryService>();
                services.AddSingleton(sp => new WebRtcRelayReadinessService(
                    sp.GetRequiredService<ISessionStore>(),
                    sp.GetRequiredService<IRealtimeTransportFactory>(),
                    sp.GetRequiredService<SessionMediaRegistryService>(),
                    readinessOptions));
                services.AddSingleton<EnsureSessionControlLeaseUseCase>();
            })
            .Build();
    }

    private static SessionControlEnsureBody CreateEnsureRequest(string endpointId)
        => new(
            ParticipantId: "owner:smoke-runner",
            RequestedBy: "smoke-runner",
            EndpointId: endpointId,
            Profile: SessionProfile.UltraLowLatency,
            LeaseSeconds: 120,
            Reason: "hosted-test",
            AutoCreateSessionIfMissing: true,
            PreferLiveRelaySession: true,
            TenantId: "local-tenant",
            OrganizationId: "local-org",
            OperatorRoles: ["operator.viewer", "operator.moderator", "operator.admin"]);

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

    private sealed class RecordingEventWriter : IEventWriter
    {
        public List<AuditEventBody> AuditEvents { get; } = [];

        public Task WriteAuditAsync(AuditEventBody auditEvent, CancellationToken cancellationToken)
        {
            AuditEvents.Add(auditEvent);
            return Task.CompletedTask;
        }

        public Task WriteTelemetryAsync(TelemetryEventBody telemetryEvent, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private sealed class InMemorySessionStore : ISessionStore
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

    private sealed class StaticConnectorRegistry(IReadOnlyList<ConnectorDescriptor> connectors) : IConnectorRegistry
    {
        public Task RegisterAsync(IConnector connector, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task<IReadOnlyList<ConnectorDescriptor>> ListAsync(CancellationToken cancellationToken)
            => Task.FromResult(connectors);

        public Task<IConnector?> ResolveAsync(string agentId, CancellationToken cancellationToken)
            => Task.FromResult<IConnector?>(null);
    }

    private sealed class HostedTransportFactory(IReadOnlyDictionary<string, object?>? metrics) : IRealtimeTransportFactory
    {
        private readonly HostedTransport _transport = new(metrics);

        public RealtimeTransportProvider DefaultProvider => RealtimeTransportProvider.WebRtcDataChannel;

        public IRealtimeTransport Resolve(RealtimeTransportProvider? provider = null) => _transport;

        public RealtimeTransportRouteResolution ResolveRoute(RealtimeTransportRoutePolicyContext context)
            => new(
                Provider: RealtimeTransportProvider.WebRtcDataChannel,
                Source: "session");
    }

    private sealed class HostedTransport(IReadOnlyDictionary<string, object?>? metrics) : IRealtimeTransport
    {
        public RealtimeTransportProvider Provider => RealtimeTransportProvider.WebRtcDataChannel;

        public Task ConnectAsync(RealtimeTransportRouteContext route, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<CommandAckBody> SendCommandAsync(RealtimeTransportRouteContext route, CommandRequestBody command, CancellationToken cancellationToken)
            => Task.FromResult(new CommandAckBody(command.CommandId, CommandStatus.Applied));

        public async IAsyncEnumerable<RealtimeTransportMessage> ReceiveAsync(
            RealtimeTransportRouteContext route,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task CloseAsync(RealtimeTransportRouteContext route, string? reason, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task<RealtimeTransportHealth> GetHealthAsync(RealtimeTransportRouteContext route, CancellationToken cancellationToken)
        {
            var defaultMetrics = new Dictionary<string, object?>
            {
                ["onlinePeerCount"] = 1,
                ["lastPeerState"] = "Connected",
                ["lastRelayAckAtUtc"] = DateTimeOffset.UtcNow.ToString("O"),
            };
            if (metrics is not null)
            {
                foreach (var (key, value) in metrics)
                {
                    defaultMetrics[key] = value;
                }
            }

            return Task.FromResult(new RealtimeTransportHealth(
                Provider: Provider,
                IsConnected: true,
                Status: "Connected",
                Metrics: defaultMetrics));
        }
    }

    private sealed class HostedSessionOrchestrator(InMemorySessionStore sessionStore, bool throwLeaseConflict) : ISessionOrchestrator
    {
        public async Task<SessionOpenBody> OpenAsync(SessionOpenBody request, CancellationToken cancellationToken)
        {
            await sessionStore.UpsertAsync(
                CreateSession(request.SessionId, request.TargetAgentId, request.TargetEndpointId ?? "endpoint-unknown"),
                cancellationToken);
            return request;
        }

        public Task<SessionCloseBody> CloseAsync(SessionCloseBody request, CancellationToken cancellationToken) => Task.FromResult(request);
        public Task<CommandAckBody> DispatchAsync(CommandRequestBody request, CancellationToken cancellationToken) => Task.FromResult(new CommandAckBody(request.CommandId, CommandStatus.Applied));
        public Task<IReadOnlyList<SessionParticipantBody>> ListParticipantsAsync(string sessionId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<SessionParticipantBody>>([]);
        public Task<SessionParticipantBody> UpsertParticipantAsync(string sessionId, SessionParticipantUpsertBody request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SessionParticipantRemoveBody> RemoveParticipantAsync(string sessionId, SessionParticipantRemoveBody request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<SessionShareBody>> ListSharesAsync(string sessionId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<SessionShareBody>>([]);
        public Task<SessionShareBody> GrantShareAsync(string sessionId, SessionShareGrantBody request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SessionShareBody> RequestInvitationAsync(string sessionId, SessionInvitationRequestBody request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SessionShareBody> ApproveInvitationAsync(string sessionId, SessionInvitationDecisionBody request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SessionShareBody> DeclineInvitationAsync(string sessionId, SessionInvitationDecisionBody request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SessionShareBody> AcceptShareAsync(string sessionId, SessionShareTransitionBody request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SessionShareBody> RejectShareAsync(string sessionId, SessionShareTransitionBody request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SessionShareBody> RevokeShareAsync(string sessionId, SessionShareTransitionBody request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SessionControlLeaseBody?> GetControlLeaseAsync(string sessionId, CancellationToken cancellationToken) => Task.FromResult<SessionControlLeaseBody?>(null);

        public Task<SessionControlLeaseBody> RequestControlAsync(string sessionId, SessionControlRequestBody request, CancellationToken cancellationToken)
        {
            if (throwLeaseConflict)
            {
                throw ControlArbitrationException.LeaseHeldByOther(
                    sessionId,
                    new SessionControlLeaseBody(
                        ParticipantId: "owner:other",
                        PrincipalId: "other-user",
                        GrantedBy: "other-user",
                        GrantedAtUtc: DateTimeOffset.UtcNow.AddSeconds(-5),
                        ExpiresAtUtc: DateTimeOffset.UtcNow.AddMinutes(1)),
                    requestedParticipantId: request.ParticipantId,
                    actedBy: request.RequestedBy);
            }

            return Task.FromResult(new SessionControlLeaseBody(
                ParticipantId: request.ParticipantId,
                PrincipalId: request.RequestedBy,
                GrantedBy: request.RequestedBy,
                GrantedAtUtc: DateTimeOffset.UtcNow,
                ExpiresAtUtc: DateTimeOffset.UtcNow.AddSeconds(Math.Max(30, request.LeaseSeconds ?? 30))));
        }

        public Task<SessionControlLeaseBody> GrantControlAsync(string sessionId, SessionControlGrantBody request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SessionControlLeaseBody?> ReleaseControlAsync(string sessionId, SessionControlReleaseBody request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<SessionControlLeaseBody> ForceTakeoverControlAsync(string sessionId, SessionControlGrantBody request, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}

using HidBridge.Abstractions;
using HidBridge.Application;
using HidBridge.Contracts;
using Xunit;

namespace HidBridge.Platform.Tests;

/// <summary>
/// Verifies server-side WebRTC relay readiness policy decisions.
/// </summary>
public sealed class WebRtcRelayReadinessServiceTests
{
    /// <summary>
    /// Returns ready when provider, connectivity, peer count, and peer state satisfy policy.
    /// </summary>
    [Fact]
    public async Task EvaluateAsync_ReturnsReady_WhenRelayIsConnectedAndPeerOnline()
    {
        var sessionStore = new InMemorySessionStore();
        await sessionStore.UpsertAsync(CreateSession("session-ready"), TestContext.Current.CancellationToken);
        var transportFactory = new StubTransportFactory(
            provider: RealtimeTransportProvider.WebRtcDataChannel,
            source: "request",
            health: new RealtimeTransportHealth(
                RealtimeTransportProvider.WebRtcDataChannel,
                IsConnected: true,
                Status: "Connected",
                Metrics: new Dictionary<string, object?>
                {
                    ["onlinePeerCount"] = 1,
                    ["lastPeerState"] = "Connected",
                }));
        var service = new WebRtcRelayReadinessService(sessionStore, transportFactory);

        var readiness = await service.EvaluateAsync(
            "session-ready",
            RealtimeTransportProvider.WebRtcDataChannel,
            TestContext.Current.CancellationToken);

        Assert.True(readiness.Ready);
        Assert.Equal("ready", readiness.ReasonCode);
        Assert.Equal(1, readiness.OnlinePeerCount);
    }

    /// <summary>
    /// Returns peer_offline when route is connected but no online relay peers are available.
    /// </summary>
    [Fact]
    public async Task EvaluateAsync_ReturnsPeerOffline_WhenOnlinePeerCountIsZero()
    {
        var sessionStore = new InMemorySessionStore();
        await sessionStore.UpsertAsync(CreateSession("session-offline"), TestContext.Current.CancellationToken);
        var transportFactory = new StubTransportFactory(
            provider: RealtimeTransportProvider.WebRtcDataChannel,
            source: "request",
            health: new RealtimeTransportHealth(
                RealtimeTransportProvider.WebRtcDataChannel,
                IsConnected: true,
                Status: "Connected",
                Metrics: new Dictionary<string, object?>
                {
                    ["onlinePeerCount"] = 0,
                    ["lastPeerState"] = "Connected",
                }));
        var service = new WebRtcRelayReadinessService(sessionStore, transportFactory);

        var readiness = await service.EvaluateAsync(
            "session-offline",
            RealtimeTransportProvider.WebRtcDataChannel,
            TestContext.Current.CancellationToken);

        Assert.False(readiness.Ready);
        Assert.Equal("peer_offline", readiness.ReasonCode);
    }

    /// <summary>
    /// Returns provider_not_webrtc when route resolves to non-WebRTC provider.
    /// </summary>
    [Fact]
    public async Task EvaluateAsync_ReturnsProviderNotWebRtc_WhenRouteProviderIsUart()
    {
        var sessionStore = new InMemorySessionStore();
        await sessionStore.UpsertAsync(CreateSession("session-uart"), TestContext.Current.CancellationToken);
        var transportFactory = new StubTransportFactory(
            provider: RealtimeTransportProvider.Uart,
            source: "session",
            health: new RealtimeTransportHealth(
                RealtimeTransportProvider.Uart,
                IsConnected: true,
                Status: "Connected",
                Metrics: new Dictionary<string, object?>
                {
                    ["onlinePeerCount"] = 5,
                }));
        var service = new WebRtcRelayReadinessService(sessionStore, transportFactory);

        var readiness = await service.EvaluateAsync(
            "session-uart",
            RealtimeTransportProvider.Uart,
            TestContext.Current.CancellationToken);

        Assert.False(readiness.Ready);
        Assert.Equal("provider_not_webrtc", readiness.ReasonCode);
    }

    /// <summary>
    /// Returns peer_state_unhealthy when the latest peer state is outside configured healthy set.
    /// </summary>
    [Fact]
    public async Task EvaluateAsync_ReturnsPeerStateUnhealthy_WhenLatestPeerStateIsOffline()
    {
        var sessionStore = new InMemorySessionStore();
        await sessionStore.UpsertAsync(CreateSession("session-peer-state"), TestContext.Current.CancellationToken);
        var transportFactory = new StubTransportFactory(
            provider: RealtimeTransportProvider.WebRtcDataChannel,
            source: "request",
            health: new RealtimeTransportHealth(
                RealtimeTransportProvider.WebRtcDataChannel,
                IsConnected: true,
                Status: "Connected",
                Metrics: new Dictionary<string, object?>
                {
                    ["onlinePeerCount"] = 1,
                    ["lastPeerState"] = "Offline",
                    ["lastPeerFailureReason"] = "ws_503",
                }));
        var service = new WebRtcRelayReadinessService(sessionStore, transportFactory);

        var readiness = await service.EvaluateAsync(
            "session-peer-state",
            RealtimeTransportProvider.WebRtcDataChannel,
            TestContext.Current.CancellationToken);

        Assert.False(readiness.Ready);
        Assert.Equal("peer_state_unhealthy", readiness.ReasonCode);
        Assert.Equal("ws_503", readiness.LastPeerFailureReason);
    }

    /// <summary>
    /// Returns media_not_ready when media readiness is required but peer media probe is unhealthy.
    /// </summary>
    [Fact]
    public async Task EvaluateAsync_ReturnsMediaNotReady_WhenMediaPolicyIsRequiredAndProbeIsUnhealthy()
    {
        var sessionStore = new InMemorySessionStore();
        await sessionStore.UpsertAsync(CreateSession("session-media-not-ready"), TestContext.Current.CancellationToken);
        var transportFactory = new StubTransportFactory(
            provider: RealtimeTransportProvider.WebRtcDataChannel,
            source: "request",
            health: new RealtimeTransportHealth(
                RealtimeTransportProvider.WebRtcDataChannel,
                IsConnected: true,
                Status: "Connected",
                Metrics: new Dictionary<string, object?>
                {
                    ["onlinePeerCount"] = 1,
                    ["lastPeerState"] = "Connected",
                    ["lastPeerMediaReady"] = false,
                    ["lastPeerMediaState"] = "Unavailable",
                    ["lastPeerMediaFailureReason"] = "capture-offline",
                }));
        var service = new WebRtcRelayReadinessService(
            sessionStore,
            transportFactory,
            new WebRtcRelayReadinessOptions
            {
                RequireMediaReady = true,
            });

        var readiness = await service.EvaluateAsync(
            "session-media-not-ready",
            RealtimeTransportProvider.WebRtcDataChannel,
            TestContext.Current.CancellationToken);

        Assert.False(readiness.Ready);
        Assert.Equal("media_not_ready", readiness.ReasonCode);
        Assert.False(readiness.MediaReady);
        Assert.Equal("capture-offline", readiness.MediaFailureReason);
    }

    /// <summary>
    /// Returns ready when media readiness is required and media probe reports healthy state.
    /// </summary>
    [Fact]
    public async Task EvaluateAsync_ReturnsReady_WhenMediaPolicyIsRequiredAndProbeIsHealthy()
    {
        var sessionStore = new InMemorySessionStore();
        await sessionStore.UpsertAsync(CreateSession("session-media-ready"), TestContext.Current.CancellationToken);
        var transportFactory = new StubTransportFactory(
            provider: RealtimeTransportProvider.WebRtcDataChannel,
            source: "request",
            health: new RealtimeTransportHealth(
                RealtimeTransportProvider.WebRtcDataChannel,
                IsConnected: true,
                Status: "Connected",
                Metrics: new Dictionary<string, object?>
                {
                    ["onlinePeerCount"] = 1,
                    ["lastPeerState"] = "Connected",
                    ["lastPeerMediaReady"] = true,
                    ["lastPeerMediaState"] = "Streaming",
                    ["lastPeerMediaStreamId"] = "stream-main",
                }));
        var service = new WebRtcRelayReadinessService(
            sessionStore,
            transportFactory,
            new WebRtcRelayReadinessOptions
            {
                RequireMediaReady = true,
            });

        var readiness = await service.EvaluateAsync(
            "session-media-ready",
            RealtimeTransportProvider.WebRtcDataChannel,
            TestContext.Current.CancellationToken);

        Assert.True(readiness.Ready);
        Assert.Equal("ready", readiness.ReasonCode);
        Assert.True(readiness.MediaReady);
        Assert.Equal("stream-main", readiness.MediaStreamId);
    }

    /// <summary>
    /// Prefers platform media registry snapshot over transport metrics when both are present.
    /// </summary>
    [Fact]
    public async Task EvaluateAsync_PrefersMediaRegistrySnapshot_WhenRegistryHasNewerSignal()
    {
        var sessionStore = new InMemorySessionStore();
        await sessionStore.UpsertAsync(CreateSession("session-media-registry"), TestContext.Current.CancellationToken);
        var transportFactory = new StubTransportFactory(
            provider: RealtimeTransportProvider.WebRtcDataChannel,
            source: "request",
            health: new RealtimeTransportHealth(
                RealtimeTransportProvider.WebRtcDataChannel,
                IsConnected: true,
                Status: "Connected",
                Metrics: new Dictionary<string, object?>
                {
                    ["onlinePeerCount"] = 1,
                    ["lastPeerState"] = "Connected",
                    ["lastPeerMediaReady"] = false,
                    ["lastPeerMediaState"] = "Unavailable",
                    ["lastPeerMediaFailureReason"] = "stale-transport-metric",
                }));
        var mediaRegistry = new SessionMediaRegistryService();
        _ = await mediaRegistry.UpsertAsync(
            "session-media-registry",
            new SessionMediaStreamRegistrationBody(
                PeerId: "peer-1",
                EndpointId: "endpoint-1",
                StreamId: "stream-registry",
                Ready: true,
                State: "Streaming",
                ReportedAtUtc: DateTimeOffset.UtcNow,
                Source: "hdmi-usb-capture",
                StreamKind: "audio-video",
                Video: new MediaVideoDescriptorBody("h264", 1920, 1080, 30d, 4000),
                Audio: new MediaAudioDescriptorBody("opus", 2, 48000, 128)),
            TestContext.Current.CancellationToken);
        var service = new WebRtcRelayReadinessService(
            sessionStore,
            transportFactory,
            mediaRegistry,
            new WebRtcRelayReadinessOptions
            {
                RequireMediaReady = true,
            });

        var readiness = await service.EvaluateAsync(
            "session-media-registry",
            RealtimeTransportProvider.WebRtcDataChannel,
            TestContext.Current.CancellationToken);

        Assert.True(readiness.Ready);
        Assert.True(readiness.MediaReady);
        Assert.Equal("stream-registry", readiness.MediaStreamId);
        Assert.Equal("hdmi-usb-capture", readiness.MediaSource);
        Assert.Equal("audio-video", readiness.MediaStreamKind);
        Assert.Equal("h264", readiness.MediaVideo?.Codec);
        Assert.Equal(1920, readiness.MediaVideo?.Width);
        Assert.Equal("opus", readiness.MediaAudio?.Codec);
        Assert.Equal(2, readiness.MediaAudio?.Channels);
    }

    /// <summary>
    /// Creates a session snapshot used by readiness tests.
    /// </summary>
    private static SessionSnapshot CreateSession(string sessionId)
    {
        var now = DateTimeOffset.UtcNow;
        return new SessionSnapshot(
            SessionId: sessionId,
            AgentId: "agent-1",
            EndpointId: "endpoint-1",
            Profile: SessionProfile.UltraLowLatency,
            RequestedBy: "owner",
            Role: SessionRole.Owner,
            State: SessionState.Active,
            UpdatedAtUtc: now,
            Participants:
            [
                new SessionParticipantSnapshot("owner:owner", "owner", SessionRole.Owner, now, now),
            ],
            Shares: [],
            ControlLease: null,
            LastHeartbeatAtUtc: now,
            LeaseExpiresAtUtc: now.AddMinutes(1),
            TenantId: "local-tenant",
            OrganizationId: "local-org");
    }

    /// <summary>
    /// In-memory session store for test setup.
    /// </summary>
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

    /// <summary>
    /// Test transport factory that returns deterministic route resolution and health.
    /// </summary>
    private sealed class StubTransportFactory : IRealtimeTransportFactory
    {
        private readonly RealtimeTransportRouteResolution _resolution;
        private readonly IRealtimeTransport _transport;

        public StubTransportFactory(
            RealtimeTransportProvider provider,
            string source,
            RealtimeTransportHealth health)
        {
            _resolution = new RealtimeTransportRouteResolution(provider, source);
            _transport = new StubTransport(provider, health);
            DefaultProvider = provider;
        }

        public RealtimeTransportProvider DefaultProvider { get; }

        public IRealtimeTransport Resolve(RealtimeTransportProvider? provider = null) => _transport;

        public RealtimeTransportRouteResolution ResolveRoute(RealtimeTransportRoutePolicyContext context) => _resolution;
    }

    /// <summary>
    /// Transport stub that only serves health payloads.
    /// </summary>
    private sealed class StubTransport : IRealtimeTransport
    {
        private readonly RealtimeTransportHealth _health;

        public StubTransport(RealtimeTransportProvider provider, RealtimeTransportHealth health)
        {
            Provider = provider;
            _health = health;
        }

        public RealtimeTransportProvider Provider { get; }

        public Task ConnectAsync(RealtimeTransportRouteContext route, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task<CommandAckBody> SendCommandAsync(RealtimeTransportRouteContext route, CommandRequestBody command, CancellationToken cancellationToken)
            => Task.FromResult(new CommandAckBody(command.CommandId, CommandStatus.Applied));

        public async IAsyncEnumerable<RealtimeTransportMessage> ReceiveAsync(RealtimeTransportRouteContext route, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task CloseAsync(RealtimeTransportRouteContext route, string? reason, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task<RealtimeTransportHealth> GetHealthAsync(RealtimeTransportRouteContext route, CancellationToken cancellationToken)
            => Task.FromResult(_health);
    }
}

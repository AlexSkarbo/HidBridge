using HidBridge.Abstractions;
using HidBridge.Application;
using HidBridge.Contracts;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Xunit;

namespace HidBridge.Platform.Tests;

/// <summary>
/// Verifies WebRTC DataChannel transport adapter routing and bridge behavior.
/// </summary>
public sealed class WebRtcDataChannelRealtimeTransportTests
{
    [Fact]
    public async Task ConnectAsync_ThrowsWhenCapabilityIsRequiredButEndpointDoesNotAdvertiseWebRtc()
    {
        var connector = new FakeConnector("agent-1", "endpoint-1", new ConnectorCommandResult("cmd-1", CommandStatus.Applied));
        var registry = new FakeConnectorRegistry(connector);
        var endpointStore = new FakeEndpointSnapshotStore(
            new EndpointSnapshot(
                "endpoint-1",
                [new CapabilityDescriptor(CapabilityNames.HidKeyboardV1, "1.0")],
                DateTimeOffset.UtcNow));
        var transport = new WebRtcDataChannelRealtimeTransport(
            registry,
            endpointStore,
            new WebRtcCommandRelayService(),
            new FakeWebRtcSignalingStore(),
            new WebRtcTransportRuntimeOptions
            {
                RequireDataChannelCapability = true,
                EnableConnectorBridge = true,
            });

        await Assert.ThrowsAsync<InvalidDataException>(() => transport.ConnectAsync(
            new RealtimeTransportRouteContext("agent-1", EndpointId: "endpoint-1", SessionId: "session-1"),
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task SendCommandAsync_ReturnsDisconnectedWhenRouteWasNotConnected()
    {
        var connector = new FakeConnector("agent-1", "endpoint-1", new ConnectorCommandResult("cmd-1", CommandStatus.Applied));
        var transport = new WebRtcDataChannelRealtimeTransport(
            new FakeConnectorRegistry(connector),
            new FakeEndpointSnapshotStore(
                new EndpointSnapshot(
                    "endpoint-1",
                    [new CapabilityDescriptor(CapabilityNames.TransportWebRtcDataChannelV1, "1.0")],
                    DateTimeOffset.UtcNow)),
            new WebRtcCommandRelayService(),
            new FakeWebRtcSignalingStore(),
            new WebRtcTransportRuntimeOptions());

        var ack = await transport.SendCommandAsync(
            new RealtimeTransportRouteContext("agent-1", EndpointId: "endpoint-1", SessionId: "session-1"),
            new CommandRequestBody(
                CommandId: "cmd-1",
                SessionId: "session-1",
                Channel: CommandChannel.DataChannel,
                Action: "keyboard.shortcut",
                Args: new Dictionary<string, object?> { ["shortcut"] = "CTRL+ALT+M" },
                TimeoutMs: 250,
                IdempotencyKey: "cmd-1"),
            TestContext.Current.CancellationToken);

        Assert.Equal(CommandStatus.Rejected, ack.Status);
        Assert.Equal("E_TRANSPORT_DISCONNECTED", ack.Error?.Code);
    }

    [Fact]
    public async Task ConnectAndSend_BridgesToConnectorAndReportsConnectedHealth()
    {
        var connector = new FakeConnector(
            "agent-1",
            "endpoint-1",
            new ConnectorCommandResult("cmd-1", CommandStatus.Applied, Metrics: new Dictionary<string, double> { ["deviceAckMs"] = 12.5 }));
        var registry = new FakeConnectorRegistry(connector);
        var endpointStore = new FakeEndpointSnapshotStore(
            new EndpointSnapshot(
                "endpoint-1",
                [
                    new CapabilityDescriptor(CapabilityNames.TransportWebRtcDataChannelV1, "1.0"),
                    new CapabilityDescriptor(CapabilityNames.HidKeyboardV1, "1.0"),
                ],
                DateTimeOffset.UtcNow));
        var transport = new WebRtcDataChannelRealtimeTransport(
            registry,
            endpointStore,
            new WebRtcCommandRelayService(),
            new FakeWebRtcSignalingStore(),
            new WebRtcTransportRuntimeOptions
            {
                RequireDataChannelCapability = true,
                EnableConnectorBridge = true,
            });
        var route = new RealtimeTransportRouteContext("agent-1", EndpointId: "endpoint-1", SessionId: "session-1");

        await transport.ConnectAsync(route, TestContext.Current.CancellationToken);
        var ack = await transport.SendCommandAsync(
            route,
            new CommandRequestBody(
                CommandId: "cmd-1",
                SessionId: "session-1",
                Channel: CommandChannel.DataChannel,
                Action: "keyboard.text",
                Args: new Dictionary<string, object?> { ["text"] = "hello" },
                TimeoutMs: 250,
                IdempotencyKey: "cmd-1"),
            TestContext.Current.CancellationToken);
        var health = await transport.GetHealthAsync(route, TestContext.Current.CancellationToken);

        Assert.Equal(CommandStatus.Applied, ack.Status);
        Assert.NotNull(ack.Metrics);
        Assert.True(ack.Metrics!.ContainsKey("transportBridgeMode"));
        Assert.True(health.IsConnected);
        Assert.Equal("Connected", health.Status);
        Assert.Equal(true, health.Metrics["endpointSupportsWebRtc"]);

        await transport.CloseAsync(route, "test-close", TestContext.Current.CancellationToken);
        var afterClose = await transport.GetHealthAsync(route, TestContext.Current.CancellationToken);
        Assert.False(afterClose.IsConnected);
    }

    [Fact]
    public async Task ConnectAsync_UsesConnectorCapabilities_WhenPersistedEndpointSnapshotIsMissing()
    {
        var connector = new FakeConnector(
            "agent-1",
            "endpoint-1",
            new ConnectorCommandResult("cmd-1", CommandStatus.Applied),
            [
                new CapabilityDescriptor(CapabilityNames.TransportWebRtcDataChannelV1, "1.0"),
                new CapabilityDescriptor(CapabilityNames.HidKeyboardV1, "1.0"),
            ]);
        var transport = new WebRtcDataChannelRealtimeTransport(
            new FakeConnectorRegistry(connector),
            new FakeEndpointSnapshotStore(),
            new WebRtcCommandRelayService(),
            new FakeWebRtcSignalingStore(),
            new WebRtcTransportRuntimeOptions
            {
                RequireDataChannelCapability = true,
                EnableConnectorBridge = true,
            });
        var route = new RealtimeTransportRouteContext("agent-1", EndpointId: "endpoint-1", SessionId: "session-1");

        await transport.ConnectAsync(route, TestContext.Current.CancellationToken);
        var health = await transport.GetHealthAsync(route, TestContext.Current.CancellationToken);

        Assert.True(health.IsConnected);
        Assert.Equal(true, health.Metrics["endpointSupportsWebRtc"]);
    }

    [Fact]
    public async Task SendCommandAsync_UsesRelayAckPath_WhenPeerIsOnline()
    {
        var connector = new FakeConnector(
            "agent-1",
            "endpoint-1",
            new ConnectorCommandResult("cmd-relay", CommandStatus.Applied));
        var registry = new FakeConnectorRegistry(connector);
        var endpointStore = new FakeEndpointSnapshotStore(
            new EndpointSnapshot(
                "endpoint-1",
                [
                    new CapabilityDescriptor(CapabilityNames.TransportWebRtcDataChannelV1, "1.0"),
                    new CapabilityDescriptor(CapabilityNames.HidKeyboardV1, "1.0"),
                ],
                DateTimeOffset.UtcNow));
        var relay = new WebRtcCommandRelayService();
        var transport = new WebRtcDataChannelRealtimeTransport(
            registry,
            endpointStore,
            relay,
            new FakeWebRtcSignalingStore(),
            new WebRtcTransportRuntimeOptions
            {
                RequireDataChannelCapability = true,
                EnableConnectorBridge = true,
            });
        var route = new RealtimeTransportRouteContext("agent-1", EndpointId: "endpoint-1", SessionId: "session-relay");
        await relay.MarkPeerOnlineAsync(
            "session-relay",
            "peer-1",
            "endpoint-1",
            new Dictionary<string, string>(),
            TestContext.Current.CancellationToken);
        await transport.ConnectAsync(route, TestContext.Current.CancellationToken);

        var command = new CommandRequestBody(
            CommandId: "cmd-relay",
            SessionId: "session-relay",
            Channel: CommandChannel.DataChannel,
            Action: "keyboard.shortcut",
            Args: new Dictionary<string, object?> { ["shortcut"] = "CTRL+ALT+M" },
            TimeoutMs: 500,
            IdempotencyKey: "cmd-relay");

        var sendTask = transport.SendCommandAsync(route, command, TestContext.Current.CancellationToken);
        _ = await relay.ListCommandsAsync("session-relay", "peer-1", null, 10, TestContext.Current.CancellationToken);
        _ = await relay.PublishAckAsync(
            "session-relay",
            new CommandAckBody("cmd-relay", CommandStatus.Applied),
            TestContext.Current.CancellationToken);

        var ack = await sendTask;
        Assert.Equal(CommandStatus.Applied, ack.Status);
        Assert.NotNull(ack.Metrics);
        Assert.Equal(1, ack.Metrics!["transportRelayMode"]);
        Assert.Equal(0, connector.ExecuteCount);
    }

    [Fact]
    public async Task SendCommandAsync_UsesDirectDcdSignalPath_WhenPeerAdvertisesDcdTransportEngine()
    {
        var connector = new FakeConnector(
            "agent-1",
            "endpoint-1",
            new ConnectorCommandResult("cmd-direct", CommandStatus.Applied));
        var registry = new FakeConnectorRegistry(connector);
        var endpointStore = new FakeEndpointSnapshotStore(
            new EndpointSnapshot(
                "endpoint-1",
                [
                    new CapabilityDescriptor(CapabilityNames.TransportWebRtcDataChannelV1, "1.0"),
                    new CapabilityDescriptor(CapabilityNames.HidKeyboardV1, "1.0"),
                ],
                DateTimeOffset.UtcNow));
        var relay = new WebRtcCommandRelayService();
        var signaling = new FakeWebRtcSignalingStore();
        signaling.EnableAutoAckFromCommandPayload("peer-1");
        var transport = new WebRtcDataChannelRealtimeTransport(
            registry,
            endpointStore,
            relay,
            signaling,
            new WebRtcTransportRuntimeOptions
            {
                RequireDataChannelCapability = true,
                EnableConnectorBridge = true,
            });
        var route = new RealtimeTransportRouteContext("agent-1", EndpointId: "endpoint-1", SessionId: "session-direct");
        await relay.MarkPeerOnlineAsync(
            "session-direct",
            "peer-1",
            "endpoint-1",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["transportEngine"] = "DataChannelDotNet",
            },
            TestContext.Current.CancellationToken);
        await transport.ConnectAsync(route, TestContext.Current.CancellationToken);

        var ack = await transport.SendCommandAsync(
            route,
            new CommandRequestBody(
                CommandId: "cmd-direct",
                SessionId: "session-direct",
                Channel: CommandChannel.DataChannel,
                Action: "keyboard.text",
                Args: new Dictionary<string, object?> { ["text"] = "hello-direct" },
                TimeoutMs: 500,
                IdempotencyKey: "cmd-direct"),
            TestContext.Current.CancellationToken);

        Assert.Equal(CommandStatus.Applied, ack.Status);
        Assert.NotNull(ack.Metrics);
        Assert.Equal(1d, ack.Metrics!["transportEngineDcdDirect"]);
        Assert.False(ack.Metrics.ContainsKey("transportRelayMode"));
        Assert.Equal(0, connector.ExecuteCount);
        Assert.Contains(signaling.Published, x => x.Kind == WebRtcSignalKind.Command);
        Assert.Contains(signaling.Published, x => x.Kind == WebRtcSignalKind.Ack);
    }

    [Fact]
    public async Task SendCommandAsync_UsesDcdControlBridge_WhenEnabledAndBridgeReturnsAck()
    {
        var connector = new FakeConnector(
            "agent-1",
            "endpoint-1",
            new ConnectorCommandResult("cmd-dcd-bridge", CommandStatus.Applied));
        var registry = new FakeConnectorRegistry(connector);
        var endpointStore = new FakeEndpointSnapshotStore(
            new EndpointSnapshot(
                "endpoint-1",
                [
                    new CapabilityDescriptor(CapabilityNames.TransportWebRtcDataChannelV1, "1.0"),
                    new CapabilityDescriptor(CapabilityNames.HidKeyboardV1, "1.0"),
                ],
                DateTimeOffset.UtcNow));
        var relay = new WebRtcCommandRelayService();
        var signaling = new FakeWebRtcSignalingStore();
        var bridge = new FakeDcdControlBridge(
            new CommandAckBody(
                "cmd-dcd-bridge",
                CommandStatus.Applied,
                Metrics: new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
                {
                    ["transportEngineDcdBridge"] = 1d,
                }));

        var transport = new WebRtcDataChannelRealtimeTransport(
            registry,
            endpointStore,
            relay,
            signaling,
            new WebRtcTransportRuntimeOptions
            {
                RequireDataChannelCapability = true,
                EnableConnectorBridge = true,
                EnableDcdControlBridge = true,
            },
            bridge);
        var route = new RealtimeTransportRouteContext("agent-1", EndpointId: "endpoint-1", SessionId: "session-dcd-bridge");
        await transport.ConnectAsync(route, TestContext.Current.CancellationToken);

        var ack = await transport.SendCommandAsync(
            route,
            new CommandRequestBody(
                CommandId: "cmd-dcd-bridge",
                SessionId: "session-dcd-bridge",
                Channel: CommandChannel.DataChannel,
                Action: "keyboard.text",
                Args: new Dictionary<string, object?> { ["text"] = "bridge" },
                TimeoutMs: 500,
                IdempotencyKey: "cmd-dcd-bridge"),
            TestContext.Current.CancellationToken);

        Assert.Equal(CommandStatus.Applied, ack.Status);
        Assert.NotNull(ack.Metrics);
        Assert.Equal(1d, ack.Metrics!["transportEngineDcdBridge"]);
        Assert.Equal(1, bridge.CallCount);
        Assert.Equal(0, connector.ExecuteCount);
        Assert.Empty(signaling.Published);
    }

    [Fact]
    public async Task SendCommandAsync_DcdControlBridgeNull_FallsBackToDirectSignalPath()
    {
        var connector = new FakeConnector(
            "agent-1",
            "endpoint-1",
            new ConnectorCommandResult("cmd-dcd-fallback", CommandStatus.Applied));
        var registry = new FakeConnectorRegistry(connector);
        var endpointStore = new FakeEndpointSnapshotStore(
            new EndpointSnapshot(
                "endpoint-1",
                [
                    new CapabilityDescriptor(CapabilityNames.TransportWebRtcDataChannelV1, "1.0"),
                    new CapabilityDescriptor(CapabilityNames.HidKeyboardV1, "1.0"),
                ],
                DateTimeOffset.UtcNow));
        var relay = new WebRtcCommandRelayService();
        var signaling = new FakeWebRtcSignalingStore();
        signaling.EnableAutoAckFromCommandPayload("peer-1");
        var bridge = new FakeDcdControlBridge(null);
        var transport = new WebRtcDataChannelRealtimeTransport(
            registry,
            endpointStore,
            relay,
            signaling,
            new WebRtcTransportRuntimeOptions
            {
                RequireDataChannelCapability = true,
                EnableConnectorBridge = true,
                EnableDcdControlBridge = true,
            },
            bridge);
        var route = new RealtimeTransportRouteContext("agent-1", EndpointId: "endpoint-1", SessionId: "session-dcd-fallback");
        await relay.MarkPeerOnlineAsync(
            "session-dcd-fallback",
            "peer-1",
            "endpoint-1",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["transportEngine"] = "DataChannelDotNet",
            },
            TestContext.Current.CancellationToken);
        await transport.ConnectAsync(route, TestContext.Current.CancellationToken);

        var ack = await transport.SendCommandAsync(
            route,
            new CommandRequestBody(
                CommandId: "cmd-dcd-fallback",
                SessionId: "session-dcd-fallback",
                Channel: CommandChannel.DataChannel,
                Action: "keyboard.text",
                Args: new Dictionary<string, object?> { ["text"] = "fallback" },
                TimeoutMs: 500,
                IdempotencyKey: "cmd-dcd-fallback"),
            TestContext.Current.CancellationToken);

        Assert.Equal(CommandStatus.Applied, ack.Status);
        Assert.NotNull(ack.Metrics);
        Assert.Equal(1d, ack.Metrics!["transportEngineDcdDirect"]);
        Assert.Equal(1, bridge.CallCount);
        Assert.Equal(0, connector.ExecuteCount);
        Assert.Contains(signaling.Published, x => x.Kind == WebRtcSignalKind.Command);
    }

    [Fact]
    public async Task SendCommandAsync_UsesConnectorBridge_WhenPeerPresenceIsStale()
    {
        var nowUtc = DateTimeOffset.UtcNow;
        var connector = new FakeConnector(
            "agent-1",
            "endpoint-1",
            new ConnectorCommandResult("cmd-stale", CommandStatus.Applied));
        var registry = new FakeConnectorRegistry(connector);
        var endpointStore = new FakeEndpointSnapshotStore(
            new EndpointSnapshot(
                "endpoint-1",
                [
                    new CapabilityDescriptor(CapabilityNames.TransportWebRtcDataChannelV1, "1.0"),
                    new CapabilityDescriptor(CapabilityNames.HidKeyboardV1, "1.0"),
                ],
                DateTimeOffset.UtcNow));
        var relay = new WebRtcCommandRelayService(
            new WebRtcCommandRelayOptions
            {
                PeerStaleAfterSec = 5,
            },
            clock: () => nowUtc);
        var transport = new WebRtcDataChannelRealtimeTransport(
            registry,
            endpointStore,
            relay,
            new FakeWebRtcSignalingStore(),
            new WebRtcTransportRuntimeOptions
            {
                RequireDataChannelCapability = true,
                EnableConnectorBridge = true,
            });
        var route = new RealtimeTransportRouteContext("agent-1", EndpointId: "endpoint-1", SessionId: "session-stale");

        await relay.MarkPeerOnlineAsync(
            "session-stale",
            "peer-1",
            "endpoint-1",
            new Dictionary<string, string>(),
            TestContext.Current.CancellationToken);

        nowUtc = nowUtc.AddSeconds(7);
        await transport.ConnectAsync(route, TestContext.Current.CancellationToken);

        var ack = await transport.SendCommandAsync(
            route,
            new CommandRequestBody(
                CommandId: "cmd-stale",
                SessionId: "session-stale",
                Channel: CommandChannel.DataChannel,
                Action: "keyboard.text",
                Args: new Dictionary<string, object?> { ["text"] = "hello" },
                TimeoutMs: 500,
                IdempotencyKey: "cmd-stale"),
            TestContext.Current.CancellationToken);

        Assert.Equal(CommandStatus.Applied, ack.Status);
        Assert.NotNull(ack.Metrics);
        Assert.True(ack.Metrics!.ContainsKey("transportBridgeMode"));
        Assert.False(ack.Metrics.ContainsKey("transportRelayMode"));
        Assert.Equal(1, connector.ExecuteCount);
    }

    private sealed class FakeConnectorRegistry : IConnectorRegistry
    {
        private readonly IConnector _connector;

        public FakeConnectorRegistry(IConnector connector)
        {
            _connector = connector;
        }

        public Task RegisterAsync(IConnector connector, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyList<ConnectorDescriptor>> ListAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<ConnectorDescriptor>>([_connector.Descriptor]);

        public Task<IConnector?> ResolveAsync(string agentId, CancellationToken cancellationToken)
            => Task.FromResult<IConnector?>(string.Equals(agentId, _connector.Descriptor.AgentId, StringComparison.OrdinalIgnoreCase)
                ? _connector
                : null);
    }

    private sealed class FakeConnector : IConnector
    {
        private readonly ConnectorCommandResult _result;

        public FakeConnector(
            string agentId,
            string endpointId,
            ConnectorCommandResult result,
            IReadOnlyList<CapabilityDescriptor>? capabilities = null)
        {
            _result = result;
            Descriptor = new ConnectorDescriptor(
                agentId,
                endpointId,
                ConnectorType.HidBridge,
                capabilities ?? [new CapabilityDescriptor(CapabilityNames.HidKeyboardV1, "1.0")]);
        }

        public ConnectorDescriptor Descriptor { get; }
        public int ExecuteCount { get; private set; }

        public Task<AgentRegisterBody> RegisterAsync(CancellationToken cancellationToken)
            => Task.FromResult(new AgentRegisterBody(ConnectorType.HidBridge, "test", Descriptor.Capabilities));

        public Task<AgentHeartbeatBody> HeartbeatAsync(CancellationToken cancellationToken)
            => Task.FromResult(new AgentHeartbeatBody(AgentStatus.Online, 1, null));

        public Task<ConnectorCommandResult> ExecuteAsync(CommandRequestBody command, CancellationToken cancellationToken)
        {
            ExecuteCount++;
            return Task.FromResult(_result with { CommandId = command.CommandId });
        }
    }

    private sealed class FakeEndpointSnapshotStore : IEndpointSnapshotStore
    {
        private readonly Dictionary<string, EndpointSnapshot> _items;

        public FakeEndpointSnapshotStore(params EndpointSnapshot[] snapshots)
        {
            _items = snapshots.ToDictionary(x => x.EndpointId, StringComparer.OrdinalIgnoreCase);
        }

        public Task UpsertAsync(EndpointSnapshot snapshot, CancellationToken cancellationToken)
        {
            _items[snapshot.EndpointId] = snapshot;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<EndpointSnapshot>> ListAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<EndpointSnapshot>>(_items.Values.ToArray());
    }

    private sealed class FakeWebRtcSignalingStore : IWebRtcSignalingStore
    {
        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() },
        };

        private readonly Dictionary<string, List<WebRtcSignalMessageBody>> _signalsBySession = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _sync = new();
        private string? _autoAckPeerId;

        public List<WebRtcSignalMessageBody> Published { get; } = [];

        public void EnableAutoAckFromCommandPayload(string senderPeerId)
        {
            _autoAckPeerId = senderPeerId;
        }

        public Task<WebRtcSignalMessageBody> AppendAsync(
            string sessionId,
            WebRtcSignalKind kind,
            string senderPeerId,
            string? recipientPeerId,
            string payload,
            string? mid,
            int? mLineIndex,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            lock (_sync)
            {
                if (!_signalsBySession.TryGetValue(sessionId, out var list))
                {
                    list = [];
                    _signalsBySession[sessionId] = list;
                }

                var sequence = list.Count + 1;
                var message = new WebRtcSignalMessageBody(
                    SessionId: sessionId,
                    Sequence: sequence,
                    Kind: kind,
                    SenderPeerId: senderPeerId,
                    RecipientPeerId: recipientPeerId,
                    Payload: payload,
                    Mid: mid,
                    MLineIndex: mLineIndex,
                    CreatedAtUtc: DateTimeOffset.UtcNow);
                list.Add(message);
                Published.Add(message);

                if (kind == WebRtcSignalKind.Command
                    && !string.IsNullOrWhiteSpace(_autoAckPeerId))
                {
                    var command = JsonSerializer.Deserialize<TransportCommandMessageBody>(payload, JsonOptions);
                    if (command is not null)
                    {
                        var ackPayload = JsonSerializer.Serialize(
                            new TransportAckMessageBody(
                                Kind: TransportMessageKind.Ack,
                                CommandId: command.CommandId,
                                Status: CommandStatus.Applied,
                                AcknowledgedAtUtc: DateTimeOffset.UtcNow),
                            JsonOptions);
                        var ack = new WebRtcSignalMessageBody(
                            SessionId: sessionId,
                            Sequence: list.Count + 1,
                            Kind: WebRtcSignalKind.Ack,
                            SenderPeerId: _autoAckPeerId,
                            RecipientPeerId: senderPeerId,
                            Payload: ackPayload,
                            CreatedAtUtc: DateTimeOffset.UtcNow);
                        list.Add(ack);
                        Published.Add(ack);
                    }
                }

                return Task.FromResult(message);
            }
        }

        public Task<IReadOnlyList<WebRtcSignalMessageBody>> ListAsync(
            string sessionId,
            string? recipientPeerId,
            int? afterSequence,
            int limit,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            lock (_sync)
            {
                if (!_signalsBySession.TryGetValue(sessionId, out var list))
                {
                    return Task.FromResult<IReadOnlyList<WebRtcSignalMessageBody>>([]);
                }

                var effectiveLimit = Math.Clamp(limit, 1, 500);
                var filtered = list
                    .Where(x => !afterSequence.HasValue || x.Sequence > afterSequence.Value)
                    .Where(x =>
                        string.IsNullOrWhiteSpace(recipientPeerId)
                        || string.IsNullOrWhiteSpace(x.RecipientPeerId)
                        || string.Equals(x.RecipientPeerId, recipientPeerId, StringComparison.OrdinalIgnoreCase))
                    .OrderBy(x => x.Sequence)
                    .Take(effectiveLimit)
                    .ToArray();
                return Task.FromResult<IReadOnlyList<WebRtcSignalMessageBody>>(filtered);
            }
        }
    }

    private sealed class FakeDcdControlBridge : IWebRtcDcdControlBridge
    {
        private readonly CommandAckBody? _result;

        public FakeDcdControlBridge(CommandAckBody? result)
        {
            _result = result;
        }

        public int CallCount { get; private set; }

        public Task<CommandAckBody?> TrySendAsync(
            RealtimeTransportRouteContext route,
            CommandRequestBody command,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return Task.FromResult(_result);
        }
    }
}

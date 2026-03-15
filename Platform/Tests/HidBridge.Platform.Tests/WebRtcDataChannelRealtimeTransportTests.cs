using HidBridge.Abstractions;
using HidBridge.Application;
using HidBridge.Contracts;
using System.IO;
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

        public FakeConnector(string agentId, string endpointId, ConnectorCommandResult result)
        {
            _result = result;
            Descriptor = new ConnectorDescriptor(
                agentId,
                endpointId,
                ConnectorType.HidBridge,
                [new CapabilityDescriptor(CapabilityNames.HidKeyboardV1, "1.0")]);
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
}

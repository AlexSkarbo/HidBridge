using HidBridge.Abstractions;
using HidBridge.Application;
using HidBridge.Contracts;
using Xunit;

namespace HidBridge.Platform.Tests;

/// <summary>
/// Verifies transport-provider selection and telemetry projection in command dispatch use case.
/// </summary>
public sealed class DispatchCommandUseCaseTests
{
    [Fact]
    public async Task ExecuteAsync_UsesFactoryDefaultProviderWhenOverrideIsMissing()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var transport = new FakeRealtimeTransport(
            RealtimeTransportProvider.Uart,
            new CommandAckBody("cmd-default-provider", CommandStatus.Applied));
        var factory = new FakeRealtimeTransportFactory(RealtimeTransportProvider.Uart, transport);
        var events = new RecordingEventWriter();
        var useCase = new DispatchCommandUseCase(new NullConnectorRegistry(), events, factory);

        var ack = await useCase.ExecuteAsync(
            "agent-1",
            new CommandRequestBody(
                CommandId: "cmd-default-provider",
                SessionId: "session-1",
                Channel: CommandChannel.Hid,
                Action: "keyboard.text",
                Args: new Dictionary<string, object?> { ["text"] = "hello" },
                TimeoutMs: 250,
                IdempotencyKey: "cmd-default-provider"),
            cancellationToken);

        Assert.Equal(CommandStatus.Applied, ack.Status);
        Assert.Equal(RealtimeTransportProvider.Uart, transport.LastConnectedProvider);
        Assert.Single(events.Telemetry);
        Assert.Equal("Uart", events.Telemetry[0].Metrics["transportProvider"]?.ToString());
    }

    [Fact]
    public async Task ExecuteAsync_UsesProviderOverrideFromArgs()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var uart = new FakeRealtimeTransport(
            RealtimeTransportProvider.Uart,
            new CommandAckBody("cmd-override", CommandStatus.Applied));
        var webrtc = new FakeRealtimeTransport(
            RealtimeTransportProvider.WebRtcDataChannel,
            new CommandAckBody(
                "cmd-override",
                CommandStatus.Rejected,
                new ErrorInfo(ErrorDomain.Transport, "E_TRANSPORT_NOT_IMPLEMENTED", "stub", false)));
        var factory = new FakeRealtimeTransportFactory(RealtimeTransportProvider.Uart, uart, webrtc);
        var events = new RecordingEventWriter();
        var useCase = new DispatchCommandUseCase(new NullConnectorRegistry(), events, factory);

        var ack = await useCase.ExecuteAsync(
            "agent-1",
            new CommandRequestBody(
                CommandId: "cmd-override",
                SessionId: "session-2",
                Channel: CommandChannel.Hid,
                Action: "keyboard.shortcut",
                Args: new Dictionary<string, object?>
                {
                    ["shortcut"] = "CTRL+ALT+M",
                    ["transportProvider"] = "webrtc",
                },
                TimeoutMs: 250,
                IdempotencyKey: "cmd-override"),
            cancellationToken);

        Assert.Equal(CommandStatus.Rejected, ack.Status);
        Assert.Equal(RealtimeTransportProvider.WebRtcDataChannel, webrtc.LastConnectedProvider);
        Assert.Single(events.Telemetry);
        Assert.Equal("WebRtcDataChannel", events.Telemetry[0].Metrics["transportProvider"]?.ToString());
    }

    private sealed class NullConnectorRegistry : IConnectorRegistry
    {
        public Task RegisterAsync(IConnector connector, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyList<ConnectorDescriptor>> ListAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<ConnectorDescriptor>>([]);

        public Task<IConnector?> ResolveAsync(string agentId, CancellationToken cancellationToken)
            => Task.FromResult<IConnector?>(null);
    }

    private sealed class RecordingEventWriter : IEventWriter
    {
        public List<TelemetryEventBody> Telemetry { get; } = [];

        public Task WriteAuditAsync(AuditEventBody auditEvent, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task WriteTelemetryAsync(TelemetryEventBody telemetryEvent, CancellationToken cancellationToken)
        {
            Telemetry.Add(telemetryEvent);
            return Task.CompletedTask;
        }
    }

    private sealed class FakeRealtimeTransportFactory : IRealtimeTransportFactory
    {
        private readonly Dictionary<RealtimeTransportProvider, IRealtimeTransport> _transports;

        public FakeRealtimeTransportFactory(RealtimeTransportProvider defaultProvider, params IRealtimeTransport[] transports)
        {
            DefaultProvider = defaultProvider;
            _transports = transports.ToDictionary(x => x.Provider);
        }

        public RealtimeTransportProvider DefaultProvider { get; }

        public IRealtimeTransport Resolve(RealtimeTransportProvider? provider = null)
            => _transports[provider ?? DefaultProvider];
    }

    private sealed class FakeRealtimeTransport : IRealtimeTransport
    {
        private readonly CommandAckBody _ack;

        public FakeRealtimeTransport(RealtimeTransportProvider provider, CommandAckBody ack)
        {
            Provider = provider;
            _ack = ack;
        }

        public RealtimeTransportProvider Provider { get; }

        public RealtimeTransportProvider? LastConnectedProvider { get; private set; }

        public Task ConnectAsync(RealtimeTransportRouteContext route, CancellationToken cancellationToken)
        {
            LastConnectedProvider = Provider;
            return Task.CompletedTask;
        }

        public Task<CommandAckBody> SendCommandAsync(RealtimeTransportRouteContext route, CommandRequestBody command, CancellationToken cancellationToken)
            => Task.FromResult(_ack);

        public async IAsyncEnumerable<RealtimeTransportMessage> ReceiveAsync(RealtimeTransportRouteContext route, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task CloseAsync(RealtimeTransportRouteContext route, string? reason, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task<RealtimeTransportHealth> GetHealthAsync(RealtimeTransportRouteContext route, CancellationToken cancellationToken)
            => Task.FromResult(new RealtimeTransportHealth(Provider, true, "Online", new Dictionary<string, object?>()));
    }
}

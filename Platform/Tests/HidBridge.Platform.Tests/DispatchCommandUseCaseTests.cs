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
        var factory = new FakeRealtimeTransportFactory(RealtimeTransportProvider.Uart, endpointProviders: null, transport);
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
        Assert.Equal("default", events.Telemetry[0].Metrics["transportProviderSource"]?.ToString());
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
        var factory = new FakeRealtimeTransportFactory(RealtimeTransportProvider.Uart, endpointProviders: null, uart, webrtc);
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
        Assert.Equal("request", events.Telemetry[0].Metrics["transportProviderSource"]?.ToString());
    }

    [Fact]
    public async Task ExecuteAsync_UsesEndpointRouteProviderWhenConfigured()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var uart = new FakeRealtimeTransport(
            RealtimeTransportProvider.Uart,
            new CommandAckBody("cmd-endpoint-route", CommandStatus.Applied));
        var webrtc = new FakeRealtimeTransport(
            RealtimeTransportProvider.WebRtcDataChannel,
            new CommandAckBody("cmd-endpoint-route", CommandStatus.Applied));
        var factory = new FakeRealtimeTransportFactory(
            RealtimeTransportProvider.Uart,
            new Dictionary<string, RealtimeTransportProvider>(StringComparer.OrdinalIgnoreCase)
            {
                ["endpoint-1"] = RealtimeTransportProvider.WebRtcDataChannel,
            },
            uart,
            webrtc);
        var events = new RecordingEventWriter();
        var useCase = new DispatchCommandUseCase(new NullConnectorRegistry(), events, factory);

        var ack = await useCase.ExecuteAsync(
            "agent-1",
            new CommandRequestBody(
                CommandId: "cmd-endpoint-route",
                SessionId: "session-endpoint-route",
                Channel: CommandChannel.Hid,
                Action: "keyboard.text",
                Args: new Dictionary<string, object?> { ["text"] = "hello" },
                TimeoutMs: 250,
                IdempotencyKey: "cmd-endpoint-route"),
            endpointId: "endpoint-1",
            sessionTransportProvider: null,
            cancellationToken);

        Assert.Equal(CommandStatus.Applied, ack.Status);
        Assert.Equal(RealtimeTransportProvider.WebRtcDataChannel, webrtc.LastConnectedProvider);
        Assert.Single(events.Telemetry);
        Assert.Equal("WebRtcDataChannel", events.Telemetry[0].Metrics["transportProvider"]?.ToString());
        Assert.Equal("endpoint", events.Telemetry[0].Metrics["transportProviderSource"]?.ToString());
    }

    [Fact]
    public async Task ExecuteAsync_RejectsRequestProviderThatConflictsWithSessionRoute()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var uart = new FakeRealtimeTransport(
            RealtimeTransportProvider.Uart,
            new CommandAckBody("cmd-route-conflict", CommandStatus.Applied));
        var webrtc = new FakeRealtimeTransport(
            RealtimeTransportProvider.WebRtcDataChannel,
            new CommandAckBody("cmd-route-conflict", CommandStatus.Applied));
        var factory = new FakeRealtimeTransportFactory(RealtimeTransportProvider.Uart, endpointProviders: null, uart, webrtc);
        var events = new RecordingEventWriter();
        var useCase = new DispatchCommandUseCase(new NullConnectorRegistry(), events, factory);

        var ack = await useCase.ExecuteAsync(
            "agent-1",
            new CommandRequestBody(
                CommandId: "cmd-route-conflict",
                SessionId: "session-route-conflict",
                Channel: CommandChannel.Hid,
                Action: "keyboard.text",
                Args: new Dictionary<string, object?>
                {
                    ["text"] = "hello",
                    ["transportProvider"] = "webrtc",
                },
                TimeoutMs: 250,
                IdempotencyKey: "cmd-route-conflict"),
            endpointId: "endpoint-1",
            sessionTransportProvider: RealtimeTransportProvider.Uart,
            cancellationToken);

        Assert.Equal(CommandStatus.Rejected, ack.Status);
        Assert.Equal("E_TRANSPORT_ROUTE_CONFLICT", ack.Error?.Code);
        Assert.Null(webrtc.LastConnectedProvider);
        Assert.Single(events.Telemetry);
        Assert.Equal("Rejected", events.Telemetry[0].Metrics["status"]?.ToString());
        Assert.Equal("Rejected", events.Telemetry[0].Metrics["transportErrorKind"]?.ToString());
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

        public FakeRealtimeTransportFactory(
            RealtimeTransportProvider defaultProvider,
            IReadOnlyDictionary<string, RealtimeTransportProvider>? endpointProviders,
            params IRealtimeTransport[] transports)
        {
            DefaultProvider = defaultProvider;
            _endpointProviders = endpointProviders ?? new Dictionary<string, RealtimeTransportProvider>(StringComparer.OrdinalIgnoreCase);
            _transports = transports.ToDictionary(x => x.Provider);
        }

        public RealtimeTransportProvider DefaultProvider { get; }
        private readonly IReadOnlyDictionary<string, RealtimeTransportProvider> _endpointProviders;

        public IRealtimeTransport Resolve(RealtimeTransportProvider? provider = null)
            => _transports[provider ?? DefaultProvider];

        public RealtimeTransportRouteResolution ResolveRoute(RealtimeTransportRoutePolicyContext context)
        {
            var endpointProvider = ResolveEndpointProvider(context.EndpointId);
            var expectedProvider = context.SessionProvider ?? endpointProvider ?? DefaultProvider;
            var selectedProvider = context.RequestedProvider ?? expectedProvider;
            var hasBoundRoute = context.SessionProvider is not null || endpointProvider is not null;
            if (hasBoundRoute
                && context.RequestedProvider is not null
                && context.RequestedProvider != expectedProvider)
            {
                throw new InvalidOperationException("Transport route conflict");
            }

            return new RealtimeTransportRouteResolution(
                selectedProvider,
                context.RequestedProvider is not null
                    ? "request"
                    : context.SessionProvider is not null
                        ? "session"
                        : endpointProvider is not null
                            ? "endpoint"
                            : "default");
        }

        private RealtimeTransportProvider? ResolveEndpointProvider(string? endpointId)
        {
            if (string.IsNullOrWhiteSpace(endpointId))
            {
                return null;
            }

            return _endpointProviders.TryGetValue(endpointId, out var provider)
                ? provider
                : null;
        }
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

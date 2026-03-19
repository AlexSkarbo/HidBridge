using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HidBridge.Abstractions;
using HidBridge.Application;
using HidBridge.Contracts;
using HidBridge.Edge.Abstractions;
using HidBridge.EdgeProxy.Agent;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace HidBridge.Platform.Tests;

/// <summary>
/// Verifies end-to-end integration behavior across WebRTC relay transport and edge proxy worker lifecycle.
/// </summary>
public sealed class WebRtcEdgeAgentIntegrationTests
{
    private const string SessionId = "session-edge-integration";
    private const string AgentId = "agent-1";
    private const string EndpointId = "endpoint-1";
    private const string PeerId = "peer-1";

    /// <summary>
    /// Ensures one command can flow end-to-end: transport enqueue -> edge worker execute -> relay ACK -> Applied.
    /// </summary>
    [Fact]
    public async Task RelayHappyPath_DispatchesAppliedAckThroughEdgeWorker()
    {
        var relay = new WebRtcCommandRelayService();
        var handler = new RelayBackplaneHandler(relay)
        {
            SessionId = SessionId,
            EndpointId = EndpointId,
            PeerId = PeerId,
        };
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost:18093", UriKind.Absolute),
        };
        var commandExecutor = new StubEdgeCommandExecutor(_ => EdgeCommandExecutionResult.Applied(18.5));
        var worker = CreateWorker(httpClient, commandExecutor, transientFailureThresholdForOffline: 2);
        var transport = CreateTransport(relay);
        var route = new RealtimeTransportRouteContext(AgentId, EndpointId, SessionId, SessionId);

        await worker.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await WaitUntilAsync(
                () => relay.HasOnlinePeer(SessionId, EndpointId),
                TimeSpan.FromSeconds(5),
                "Edge peer did not register online state in relay service.");

            await transport.ConnectAsync(route, TestContext.Current.CancellationToken);
            var ack = await transport.SendCommandAsync(
                route,
                new CommandRequestBody(
                    CommandId: "cmd-edge-happy",
                    SessionId: SessionId,
                    Channel: CommandChannel.DataChannel,
                    Action: "keyboard.text",
                    Args: new Dictionary<string, object?>
                    {
                        ["text"] = "hello",
                        ["recipientPeerId"] = PeerId,
                    },
                    TimeoutMs: 2000,
                    IdempotencyKey: "idem-edge-happy"),
                TestContext.Current.CancellationToken);

            Assert.Equal(CommandStatus.Applied, ack.Status);
            Assert.NotNull(ack.Metrics);
            Assert.Equal(1, ack.Metrics!["transportRelayMode"]);
            var executed = Assert.Single(commandExecutor.Executed);
            Assert.Equal("cmd-edge-happy", executed.CommandId);
            Assert.Equal("keyboard.text", executed.Action);
        }
        finally
        {
            await worker.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    /// <summary>
    /// Ensures transient relay API failures publish degraded peer metadata for readiness diagnostics.
    /// </summary>
    [Fact]
    public async Task RelayTransientFailure_PublishesDegradedPeerMetadata()
    {
        var relay = new WebRtcCommandRelayService();
        var handler = new RelayBackplaneHandler(relay)
        {
            SessionId = SessionId,
            EndpointId = EndpointId,
            PeerId = PeerId,
            FailSignalPollCount = 1,
        };
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost:18093", UriKind.Absolute),
        };
        var worker = CreateWorker(
            httpClient,
            new StubEdgeCommandExecutor(_ => EdgeCommandExecutionResult.Applied(10.0)),
            transientFailureThresholdForOffline: 3,
            mediaProbe: new ThrowingEdgeMediaReadinessProbe("capture-unavailable"));

        await worker.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await WaitUntilAsync(
                () => handler.OnlineMetadataHistory.Any(static metadata =>
                    metadata.TryGetValue("state", out var state)
                    && string.Equals(state, "Degraded", StringComparison.OrdinalIgnoreCase)),
                TimeSpan.FromSeconds(5),
                "Edge worker did not publish degraded metadata after transient failure.");

            var degraded = handler.OnlineMetadataHistory.Last(metadata =>
                metadata.TryGetValue("state", out var state)
                && string.Equals(state, "Degraded", StringComparison.OrdinalIgnoreCase));

            Assert.True(degraded.ContainsKey("failureReason"));
            Assert.Equal("false", degraded.GetValueOrDefault("mediaReady"));
            Assert.Equal("ProbeFailed", degraded.GetValueOrDefault("mediaState"));
        }
        finally
        {
            await worker.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    /// <summary>
    /// Ensures worker can mark offline after failures, reconnect, and continue processing relay commands.
    /// </summary>
    [Fact]
    public async Task RelayReconnect_AfterOfflineTransition_CommandFlowRecovers()
    {
        var relay = new WebRtcCommandRelayService();
        var handler = new RelayBackplaneHandler(relay)
        {
            SessionId = SessionId,
            EndpointId = EndpointId,
            PeerId = PeerId,
            FailSignalPollCount = 1,
        };
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost:18093", UriKind.Absolute),
        };
        var commandExecutor = new StubEdgeCommandExecutor(_ => EdgeCommandExecutionResult.Applied(22.0));
        var worker = CreateWorker(httpClient, commandExecutor, transientFailureThresholdForOffline: 1);
        var transport = CreateTransport(relay);
        var route = new RealtimeTransportRouteContext(AgentId, EndpointId, SessionId, SessionId);

        await worker.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            await WaitUntilAsync(
                () => handler.OfflineCallCount >= 1,
                TimeSpan.FromSeconds(5),
                "Expected worker to mark peer offline after transient failures.");

            await WaitUntilAsync(
                () => handler.OnlineMetadataHistory.Count(metadata =>
                    metadata.TryGetValue("state", out var state)
                    && string.Equals(state, "Connected", StringComparison.OrdinalIgnoreCase)) >= 2,
                TimeSpan.FromSeconds(5),
                "Expected worker to reconnect and publish a second connected snapshot.");

            await WaitUntilAsync(
                () => relay.HasOnlinePeer(SessionId, EndpointId),
                TimeSpan.FromSeconds(5),
                "Relay peer did not recover to online state.");

            await transport.ConnectAsync(route, TestContext.Current.CancellationToken);
            var ack = await transport.SendCommandAsync(
                route,
                new CommandRequestBody(
                    CommandId: "cmd-edge-reconnect",
                    SessionId: SessionId,
                    Channel: CommandChannel.DataChannel,
                    Action: "mouse.move",
                    Args: new Dictionary<string, object?>
                    {
                        ["dx"] = 5,
                        ["dy"] = 2,
                        ["recipientPeerId"] = PeerId,
                    },
                    TimeoutMs: 2000,
                    IdempotencyKey: "idem-edge-reconnect"),
                TestContext.Current.CancellationToken);

            Assert.Equal(CommandStatus.Applied, ack.Status);
            Assert.NotNull(ack.Metrics);
            Assert.Equal(1, ack.Metrics!["transportRelayMode"]);
        }
        finally
        {
            await worker.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    /// <summary>
    /// Creates one edge worker bound to in-memory relay API handler.
    /// </summary>
    private static EdgeProxyWorker CreateWorker(
        HttpClient httpClient,
        IEdgeCommandExecutor commandExecutor,
        int transientFailureThresholdForOffline,
        IEdgeMediaReadinessProbe? mediaProbe = null)
    {
        var options = new EdgeProxyOptions
        {
            BaseUrl = "http://localhost:18093",
            SessionId = SessionId,
            PeerId = PeerId,
            EndpointId = EndpointId,
            AccessToken = "integration-token",
            PrincipalId = "smoke-runner",
            TenantId = "local-tenant",
            OrganizationId = "local-org",
            PollIntervalMs = 50,
            HeartbeatIntervalSec = 1,
            CommandTimeoutMs = 2000,
            ControlWsUrl = "ws://127.0.0.1:28092/ws/control",
            TransientFailureThresholdForOffline = transientFailureThresholdForOffline,
            RequireMediaReady = true,
            AssumeMediaReadyWithoutProbe = true,
        };
        options.Normalize();

        return new EdgeProxyWorker(
            new StaticHttpClientFactory(httpClient),
            commandExecutor,
            mediaProbe ?? new StubEdgeMediaReadinessProbe(),
            Options.Create(options),
            NullLogger<EdgeProxyWorker>.Instance);
    }

    /// <summary>
    /// Creates one WebRTC transport adapter backed by in-memory endpoint/connector stubs.
    /// </summary>
    private static WebRtcDataChannelRealtimeTransport CreateTransport(WebRtcCommandRelayService relay)
    {
        var connector = new FakeConnector(AgentId, EndpointId);
        var registry = new FakeConnectorRegistry(connector);
        var endpointStore = new FakeEndpointSnapshotStore(
            new EndpointSnapshot(
                EndpointId,
                [
                    new CapabilityDescriptor(CapabilityNames.TransportWebRtcDataChannelV1, "1.0"),
                    new CapabilityDescriptor(CapabilityNames.HidKeyboardV1, "1.0"),
                ],
                DateTimeOffset.UtcNow));

        return new WebRtcDataChannelRealtimeTransport(
            registry,
            endpointStore,
            relay,
            new WebRtcTransportRuntimeOptions
            {
                RequireDataChannelCapability = true,
                EnableConnectorBridge = true,
            });
    }

    /// <summary>
    /// Waits until predicate returns true or fails with timeout.
    /// </summary>
    private static async Task WaitUntilAsync(Func<bool> predicate, TimeSpan timeout, string failureMessage)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            if (predicate())
            {
                return;
            }

            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException(failureMessage);
    }

    /// <summary>
    /// Routes edge worker HTTP calls into in-memory relay service endpoints.
    /// </summary>
    private sealed class RelayBackplaneHandler : HttpMessageHandler
    {
        private readonly WebRtcCommandRelayService _relay;
        private int _remainingSignalPollFailures;

        public RelayBackplaneHandler(WebRtcCommandRelayService relay)
        {
            _relay = relay;
        }

        public string SessionId { get; set; } = string.Empty;

        public string EndpointId { get; set; } = string.Empty;

        public string PeerId { get; set; } = string.Empty;

        public int FailSignalPollCount
        {
            get => _remainingSignalPollFailures;
            set => _remainingSignalPollFailures = Math.Max(0, value);
        }

        public int OfflineCallCount { get; private set; }

        public List<IReadOnlyDictionary<string, string>> OnlineMetadataHistory { get; } = [];

        /// <summary>
        /// Handles one API request emitted by the edge worker.
        /// </summary>
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            if (request.Method == HttpMethod.Post
                && path.EndsWith($"/api/v1/sessions/{SessionId}/transport/webrtc/peers/{PeerId}/online", StringComparison.Ordinal))
            {
                var metadata = await ReadMetadataAsync(request, cancellationToken);
                OnlineMetadataHistory.Add(metadata);
                _ = await _relay.MarkPeerOnlineAsync(SessionId, PeerId, EndpointId, metadata, cancellationToken);
                return JsonOk(new { ok = true });
            }

            if (request.Method == HttpMethod.Post
                && path.EndsWith($"/api/v1/sessions/{SessionId}/transport/webrtc/peers/{PeerId}/offline", StringComparison.Ordinal))
            {
                OfflineCallCount++;
                _ = await _relay.MarkPeerOfflineAsync(SessionId, PeerId, cancellationToken);
                return JsonOk(new { ok = true });
            }

            if (request.Method == HttpMethod.Post
                && path.EndsWith($"/api/v1/sessions/{SessionId}/transport/webrtc/signals", StringComparison.Ordinal))
            {
                return JsonOk(new { accepted = true });
            }

            if (request.Method == HttpMethod.Get
                && path.EndsWith($"/api/v1/sessions/{SessionId}/transport/webrtc/signals", StringComparison.Ordinal))
            {
                if (_remainingSignalPollFailures > 0)
                {
                    _remainingSignalPollFailures--;
                    return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
                    {
                        Content = JsonContent.Create(new { error = "transient-signal-failure" }),
                    };
                }

                return JsonOk(new { items = Array.Empty<object>() });
            }

            if (request.Method == HttpMethod.Get
                && path.EndsWith($"/api/v1/sessions/{SessionId}/transport/webrtc/commands", StringComparison.Ordinal))
            {
                var peerId = GetQueryString(request.RequestUri, "peerId") ?? PeerId;
                var afterSequence = ParseNullableInt(GetQueryString(request.RequestUri, "afterSequence"));
                var limit = ParseNullableInt(GetQueryString(request.RequestUri, "limit")) ?? 50;
                var commands = await _relay.ListCommandsAsync(SessionId, peerId, afterSequence, limit, cancellationToken);
                return JsonOk(new { items = commands });
            }

            if (request.Method == HttpMethod.Post
                && path.Contains($"/api/v1/sessions/{SessionId}/transport/webrtc/commands/", StringComparison.Ordinal)
                && path.EndsWith("/ack", StringComparison.Ordinal))
            {
                var commandId = ExtractCommandIdFromAckPath(path);
                var ack = await ReadAckAsync(commandId, request, cancellationToken);
                var accepted = await _relay.PublishAckAsync(SessionId, ack, cancellationToken);
                return accepted
                    ? JsonOk(new { accepted = true })
                    : new HttpResponseMessage(HttpStatusCode.NotFound)
                    {
                        Content = JsonContent.Create(new { accepted = false }),
                    };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = JsonContent.Create(new { error = "unknown-route", method = request.Method.Method, path }),
            };
        }

        /// <summary>
        /// Reads peer metadata payload from one online request body.
        /// </summary>
        private static async Task<IReadOnlyDictionary<string, string>> ReadMetadataAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(body))
            {
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (!root.TryGetProperty("metadata", out var metadataElement)
                || metadataElement.ValueKind != JsonValueKind.Object)
            {
                return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }

            var metadata = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in metadataElement.EnumerateObject())
            {
                metadata[property.Name] = property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString() ?? string.Empty
                    : property.Value.ToString();
            }

            return metadata;
        }

        /// <summary>
        /// Reads one relay ACK request payload and converts it into contract model.
        /// </summary>
        private static async Task<CommandAckBody> ReadAckAsync(
            string commandId,
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(body))
            {
                return new CommandAckBody(commandId, CommandStatus.Rejected);
            }

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var statusToken = root.TryGetProperty("status", out var statusElement)
                ? statusElement.GetString()
                : null;
            var status = Enum.TryParse<CommandStatus>(statusToken, ignoreCase: true, out var parsedStatus)
                ? parsedStatus
                : CommandStatus.Rejected;

            ErrorInfo? error = null;
            if (root.TryGetProperty("error", out var errorElement)
                && errorElement.ValueKind == JsonValueKind.Object)
            {
                var domainToken = errorElement.TryGetProperty("domain", out var domainElement)
                    ? domainElement.GetString()
                    : null;
                var domain = Enum.TryParse<ErrorDomain>(domainToken, ignoreCase: true, out var parsedDomain)
                    ? parsedDomain
                    : ErrorDomain.Command;
                var code = errorElement.TryGetProperty("code", out var codeElement)
                    ? codeElement.GetString() ?? "E_EDGE_ACK_ERROR"
                    : "E_EDGE_ACK_ERROR";
                var message = errorElement.TryGetProperty("message", out var messageElement)
                    ? messageElement.GetString() ?? "Peer returned non-success response."
                    : "Peer returned non-success response.";
                var retryable = errorElement.TryGetProperty("retryable", out var retryableElement)
                    && retryableElement.ValueKind is JsonValueKind.True;
                error = new ErrorInfo(domain, code, message, retryable);
            }

            IReadOnlyDictionary<string, double>? metrics = null;
            if (root.TryGetProperty("metrics", out var metricsElement)
                && metricsElement.ValueKind == JsonValueKind.Object)
            {
                var map = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
                foreach (var property in metricsElement.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.Number
                        && property.Value.TryGetDouble(out var number))
                    {
                        map[property.Name] = number;
                    }
                }

                metrics = map;
            }

            return new CommandAckBody(commandId, status, error, metrics);
        }

        /// <summary>
        /// Extracts command id from relay ACK route.
        /// </summary>
        private static string ExtractCommandIdFromAckPath(string path)
        {
            var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length < 3)
            {
                return string.Empty;
            }

            return Uri.UnescapeDataString(segments[^2]);
        }

        /// <summary>
        /// Reads one query-string value.
        /// </summary>
        private static string? GetQueryString(Uri? uri, string key)
        {
            if (uri is null || string.IsNullOrWhiteSpace(uri.Query))
            {
                return null;
            }

            var query = uri.Query.TrimStart('?');
            foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = pair.Split('=', 2);
                if (!string.Equals(Uri.UnescapeDataString(parts[0]), key, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                return parts.Length == 2
                    ? Uri.UnescapeDataString(parts[1])
                    : string.Empty;
            }

            return null;
        }

        /// <summary>
        /// Parses nullable integer query value.
        /// </summary>
        private static int? ParseNullableInt(string? value)
            => int.TryParse(value, out var parsed) ? parsed : null;

        /// <summary>
        /// Builds HTTP 200 JSON response.
        /// </summary>
        private static HttpResponseMessage JsonOk(object payload)
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(payload),
            };
        }
    }

    /// <summary>
    /// Returns fixed command-execution results and captures executed commands.
    /// </summary>
    private sealed class StubEdgeCommandExecutor : IEdgeCommandExecutor
    {
        private readonly Func<EdgeCommandRequest, EdgeCommandExecutionResult> _resultFactory;

        public StubEdgeCommandExecutor(Func<EdgeCommandRequest, EdgeCommandExecutionResult> resultFactory)
        {
            _resultFactory = resultFactory;
        }

        public List<EdgeCommandRequest> Executed { get; } = [];

        public Task<EdgeCommandExecutionResult> ExecuteAsync(EdgeCommandRequest request, CancellationToken cancellationToken)
        {
            Executed.Add(request);
            return Task.FromResult(_resultFactory(request));
        }
    }

    /// <summary>
    /// Returns deterministic ready media snapshots for integration tests.
    /// </summary>
    private sealed class StubEdgeMediaReadinessProbe : IEdgeMediaReadinessProbe
    {
        public Task<EdgeMediaReadinessSnapshot> GetReadinessAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(new EdgeMediaReadinessSnapshot(
                IsReady: true,
                State: "Ready",
                ReportedAtUtc: DateTimeOffset.UtcNow,
                StreamId: "stream-main",
                Source: "edge-capture"));
        }
    }

    /// <summary>
    /// Throws deterministic exceptions to validate media-probe failure handling in worker metadata publication.
    /// </summary>
    private sealed class ThrowingEdgeMediaReadinessProbe : IEdgeMediaReadinessProbe
    {
        private readonly string _message;

        public ThrowingEdgeMediaReadinessProbe(string message)
        {
            _message = message;
        }

        public Task<EdgeMediaReadinessSnapshot> GetReadinessAsync(CancellationToken cancellationToken)
            => throw new InvalidOperationException(_message);
    }

    /// <summary>
    /// Returns a fixed <see cref="HttpClient"/> for all named client requests.
    /// </summary>
    private sealed class StaticHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;

        public StaticHttpClientFactory(HttpClient client)
        {
            _client = client;
        }

        public HttpClient CreateClient(string name) => _client;
    }

    /// <summary>
    /// Stores one endpoint snapshot list for transport capability lookup.
    /// </summary>
    private sealed class FakeEndpointSnapshotStore : IEndpointSnapshotStore
    {
        private readonly Dictionary<string, EndpointSnapshot> _items;

        public FakeEndpointSnapshotStore(params EndpointSnapshot[] snapshots)
        {
            _items = snapshots.ToDictionary(static snapshot => snapshot.EndpointId, StringComparer.OrdinalIgnoreCase);
        }

        public Task UpsertAsync(EndpointSnapshot snapshot, CancellationToken cancellationToken)
        {
            _items[snapshot.EndpointId] = snapshot;
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<EndpointSnapshot>> ListAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<EndpointSnapshot>>(_items.Values.ToArray());
    }

    /// <summary>
    /// Resolves one deterministic fake connector by agent id.
    /// </summary>
    private sealed class FakeConnectorRegistry : IConnectorRegistry
    {
        private readonly IConnector _connector;

        public FakeConnectorRegistry(IConnector connector)
        {
            _connector = connector;
        }

        public Task RegisterAsync(IConnector connector, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task<IReadOnlyList<ConnectorDescriptor>> ListAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<ConnectorDescriptor>>([_connector.Descriptor]);

        public Task<IConnector?> ResolveAsync(string agentId, CancellationToken cancellationToken)
        {
            var match = string.Equals(agentId, _connector.Descriptor.AgentId, StringComparison.OrdinalIgnoreCase)
                ? _connector
                : null;
            return Task.FromResult(match);
        }
    }

    /// <summary>
    /// Minimal connector used only so transport connect path can resolve registered agent.
    /// </summary>
    private sealed class FakeConnector : IConnector
    {
        public FakeConnector(string agentId, string endpointId)
        {
            Descriptor = new ConnectorDescriptor(
                agentId,
                endpointId,
                ConnectorType.HidBridge,
                [new CapabilityDescriptor(CapabilityNames.HidKeyboardV1, "1.0")]);
        }

        public ConnectorDescriptor Descriptor { get; }

        public Task<AgentRegisterBody> RegisterAsync(CancellationToken cancellationToken)
            => Task.FromResult(new AgentRegisterBody(Descriptor.ConnectorType, "test", Descriptor.Capabilities));

        public Task<AgentHeartbeatBody> HeartbeatAsync(CancellationToken cancellationToken)
            => Task.FromResult(new AgentHeartbeatBody(AgentStatus.Online));

        public Task<ConnectorCommandResult> ExecuteAsync(CommandRequestBody command, CancellationToken cancellationToken)
            => Task.FromResult(new ConnectorCommandResult(command.CommandId, CommandStatus.Applied));
    }
}

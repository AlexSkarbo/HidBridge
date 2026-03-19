using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HidBridge.Edge.Abstractions;
using HidBridge.Contracts;
using HidBridge.EdgeProxy.Agent;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace HidBridge.Platform.Tests;

/// <summary>
/// Verifies the end-to-end relay lifecycle handled by <see cref="EdgeProxyWorker"/>.
/// </summary>
public sealed class EdgeProxyWorkerLifecycleTests
{
    /// <summary>
    /// Ensures the worker publishes online/heartbeat, executes one command, posts Applied ACK, then marks peer offline.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_AppliedCommand_PublishesLifecycleAndAck()
    {
        var handler = new RelayApiHandler(
            commandEnvelope: new
            {
                sequence = 1,
                command = new
                {
                    commandId = "cmd-1",
                    action = "keyboard.text",
                    timeoutMs = 5000,
                    args = new Dictionary<string, object?> { ["text"] = "hello" },
                },
            });
        var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost:18093", UriKind.Absolute),
        };
        var factory = new StaticHttpClientFactory(client);
        var hidAdapter = new StubHidBridgeHidAdapter(_ => EdgeCommandExecutionResult.Applied(42.5));
        var worker = CreateWorker(factory, hidAdapter);

        try
        {
            await worker.StartAsync(TestContext.Current.CancellationToken);

            var ackPayload = await handler.AckPayloadTask.Task.WaitAsync(
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken);
            Assert.Contains("\"status\":\"Applied\"", ackPayload, StringComparison.Ordinal);
            Assert.Contains("\"relayAdapterWsRoundtripMs\":42.5", ackPayload, StringComparison.Ordinal);
            Assert.Contains("\"transportRelayMode\":1", ackPayload, StringComparison.Ordinal);

            var onlinePayload = await handler.OnlinePayloadTask.Task.WaitAsync(
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken);
            Assert.Contains("\"state\":\"Connected\"", onlinePayload, StringComparison.Ordinal);
            Assert.Contains("\"stateChangedAtUtc\"", onlinePayload, StringComparison.Ordinal);
            Assert.Contains("\"stateReason\":\"none\"", onlinePayload, StringComparison.Ordinal);
            Assert.Contains("\"mediaReady\":\"true\"", onlinePayload, StringComparison.Ordinal);
            Assert.Contains("\"mediaState\":\"Ready\"", onlinePayload, StringComparison.Ordinal);
            var mediaPayload = await handler.MediaPayloadTask.Task.WaitAsync(
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken);
            Assert.Contains("\"streamId\":\"stream-main\"", mediaPayload, StringComparison.Ordinal);
            Assert.Contains("\"source\":\"test-capture\"", mediaPayload, StringComparison.Ordinal);

            Assert.True(handler.OnlineSeen);
            Assert.True(handler.HeartbeatSeen);
            var executed = Assert.Single(hidAdapter.Executed);
            Assert.Equal("cmd-1", executed.CommandId);
        }
        finally
        {
            await worker.StopAsync(TestContext.Current.CancellationToken);
        }

        await handler.OfflineTask.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        Assert.True(handler.OfflineSeen);
    }

    /// <summary>
    /// Ensures command failures are mapped to relay Rejected ACK with normalized error payload.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_RejectedCommand_EmitsRejectedAck()
    {
        var handler = new RelayApiHandler(
            commandEnvelope: new
            {
                sequence = 1,
                command = new
                {
                    commandId = "cmd-2",
                    action = "keyboard.shortcut",
                    timeoutMs = 5000,
                    args = new Dictionary<string, object?> { ["keys"] = "CTRL+ALT+DEL" },
                },
            });
        var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost:18093", UriKind.Absolute),
        };
        var factory = new StaticHttpClientFactory(client);
        var hidAdapter = new StubHidBridgeHidAdapter(_ =>
            EdgeCommandExecutionResult.Rejected(
                "E_WEBRTC_PEER_EXECUTION_FAILED",
                "Peer returned non-success response.",
                19.0));
        var worker = CreateWorker(factory, hidAdapter);

        try
        {
            await worker.StartAsync(TestContext.Current.CancellationToken);

            var ackPayload = await handler.AckPayloadTask.Task.WaitAsync(
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken);
            using var ackDoc = JsonDocument.Parse(ackPayload);
            var root = ackDoc.RootElement;
            Assert.Equal("Rejected", root.GetProperty("status").GetString());
            Assert.Equal("Command", root.GetProperty("error").GetProperty("domain").GetString());
            Assert.Equal("E_WEBRTC_PEER_EXECUTION_FAILED", root.GetProperty("error").GetProperty("code").GetString());
            Assert.Equal("Peer returned non-success response.", root.GetProperty("error").GetProperty("message").GetString());
            Assert.Equal(19.0, root.GetProperty("metrics").GetProperty("relayAdapterWsRoundtripMs").GetDouble());
        }
        finally
        {
            await worker.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    /// <summary>
    /// Creates worker with deterministic options for lifecycle tests.
    /// </summary>
    private static EdgeProxyWorker CreateWorker(IHttpClientFactory httpClientFactory, IHidBridgeHidAdapter hidAdapter)
    {
        var options = new EdgeProxyOptions
        {
            BaseUrl = "http://localhost:18093",
            SessionId = "session-1",
            PeerId = "peer-1",
            EndpointId = "endpoint-1",
            CommandExecutor = "uart",
            UartPort = "COM6",
            ControlWsUrl = "ws://127.0.0.1:28092/ws/control",
            PollIntervalMs = 100,
            BatchLimit = 20,
            HeartbeatIntervalSec = 10,
            CommandTimeoutMs = 5000,
            AccessToken = "test-token",
            PrincipalId = "smoke-runner",
            TenantId = "local-tenant",
            OrganizationId = "local-org",
        };
        options.Normalize();

        return new EdgeProxyWorker(
            httpClientFactory,
            hidAdapter,
            new StubCaptureAdapter(new StubEdgeMediaReadinessProbe()),
            Options.Create(options),
            NullLogger<EdgeProxyWorker>.Instance);
    }

    /// <summary>
    /// Records API calls made by worker and serves deterministic relay responses.
    /// </summary>
    private sealed class RelayApiHandler : HttpMessageHandler
    {
        private readonly object? _commandEnvelope;
        private int _commandPollCount;

        public RelayApiHandler(object? commandEnvelope)
        {
            _commandEnvelope = commandEnvelope;
        }

        public bool OnlineSeen { get; private set; }

        public bool HeartbeatSeen { get; private set; }

        public bool OfflineSeen { get; private set; }

        public TaskCompletionSource<string> AckPayloadTask { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<string> OnlinePayloadTask { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<string> MediaPayloadTask { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> OfflineTask { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>
        /// Routes known relay endpoints and captures lifecycle payloads.
        /// </summary>
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);

            if (request.Method == HttpMethod.Post && path.EndsWith("/transport/webrtc/peers/peer-1/online", StringComparison.Ordinal))
            {
                OnlineSeen = true;
                OnlinePayloadTask.TrySetResult(body);
                return JsonOk(new { ok = true });
            }

            if (request.Method == HttpMethod.Post && path.EndsWith("/transport/webrtc/signals", StringComparison.Ordinal))
            {
                HeartbeatSeen = true;
                return JsonOk(new { ok = true });
            }

            if (request.Method == HttpMethod.Get && path.EndsWith("/transport/webrtc/signals", StringComparison.Ordinal))
            {
                return JsonOk(new { items = Array.Empty<object>() });
            }

            if (request.Method == HttpMethod.Get && path.EndsWith("/transport/webrtc/commands", StringComparison.Ordinal))
            {
                _commandPollCount++;
                if (_commandPollCount == 1 && _commandEnvelope is not null)
                {
                    return JsonOk(new { items = new[] { _commandEnvelope } });
                }

                return JsonOk(new { items = Array.Empty<object>() });
            }

            if (request.Method == HttpMethod.Post && path.EndsWith("/transport/media/streams", StringComparison.Ordinal))
            {
                MediaPayloadTask.TrySetResult(body);
                return JsonOk(new { accepted = true });
            }

            if (request.Method == HttpMethod.Post && path.Contains("/transport/webrtc/commands/", StringComparison.Ordinal) && path.EndsWith("/ack", StringComparison.Ordinal))
            {
                AckPayloadTask.TrySetResult(body);
                return JsonOk(new { accepted = true });
            }

            if (request.Method == HttpMethod.Post && path.EndsWith("/transport/webrtc/peers/peer-1/offline", StringComparison.Ordinal))
            {
                OfflineSeen = true;
                OfflineTask.TrySetResult(true);
                return JsonOk(new { ok = true });
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = JsonContent.Create(new { error = "unexpected route", method = request.Method.Method, path }),
            };
        }

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
    /// Returns the same configured <see cref="HttpClient"/> for all named clients.
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
    /// Captures command requests and returns deterministic execution results.
    /// </summary>
    private sealed class StubHidBridgeHidAdapter : IHidBridgeHidAdapter
    {
        private readonly Func<TransportCommandMessageBody, EdgeCommandExecutionResult> _resultFactory;

        public StubHidBridgeHidAdapter(Func<TransportCommandMessageBody, EdgeCommandExecutionResult> resultFactory)
        {
            _resultFactory = resultFactory;
        }

        public List<TransportCommandMessageBody> Executed { get; } = [];

        public Task<EdgeCommandExecutionResult> ExecuteAsync(TransportCommandMessageBody request, CancellationToken cancellationToken)
        {
            Executed.Add(request);
            return Task.FromResult(_resultFactory(request));
        }
    }

    /// <summary>
    /// Returns deterministic ready media snapshots for lifecycle tests.
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
                Source: "test-capture"));
        }
    }

    private sealed class StubCaptureAdapter : ICaptureAdapter
    {
        private readonly IEdgeMediaReadinessProbe _probe;

        public StubCaptureAdapter(IEdgeMediaReadinessProbe probe)
        {
            _probe = probe;
        }

        public Task<EdgeMediaReadinessSnapshot> GetReadinessAsync(CancellationToken cancellationToken)
            => _probe.GetReadinessAsync(cancellationToken);
    }
}

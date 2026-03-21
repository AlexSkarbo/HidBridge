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
    /// Ensures worker can recover startup when control-ensure endpoint is unavailable and session route must be created explicitly.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_ControlEnsureNotFound_CreatesSessionRouteBeforePublishingTelemetry()
    {
        var handler = new RelayApiHandler(commandEnvelope: null)
        {
            SessionExists = false,
            ReturnNotFoundForControlEnsure = true,
        };
        var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost:18093", UriKind.Absolute),
        };
        var factory = new StaticHttpClientFactory(client);
        var hidAdapter = new StubHidBridgeHidAdapter(_ => EdgeCommandExecutionResult.Applied(1.0));
        var worker = CreateWorker(factory, hidAdapter);

        try
        {
            await worker.StartAsync(TestContext.Current.CancellationToken);
            _ = await handler.OnlinePayloadTask.Task.WaitAsync(
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken);
        }
        finally
        {
            await worker.StopAsync(TestContext.Current.CancellationToken);
        }

        Assert.True(handler.SessionExists);
        Assert.Equal(1, handler.SessionOpenCallCount);
        Assert.True(handler.OnlineSeen);
    }

    /// <summary>
    /// Ensures worker sends least-privilege role header by default.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_DefaultRolesHeader_UsesOperatorEdge()
    {
        var handler = new RelayApiHandler(commandEnvelope: null);
        var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost:18093", UriKind.Absolute),
        };
        var factory = new StaticHttpClientFactory(client);
        var hidAdapter = new StubHidBridgeHidAdapter(_ => EdgeCommandExecutionResult.Applied(1.0));
        var worker = CreateWorker(factory, hidAdapter);

        try
        {
            await worker.StartAsync(TestContext.Current.CancellationToken);
            _ = await handler.OnlinePayloadTask.Task.WaitAsync(
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken);
        }
        finally
        {
            await worker.StopAsync(TestContext.Current.CancellationToken);
        }

        Assert.Equal("operator.edge", handler.LastRoleHeader);
    }

    /// <summary>
    /// Ensures worker rotates bearer token after one unauthorized relay call.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_UnauthorizedRelayCall_RotatesBearerTokenAndRetries()
    {
        var handler = new RelayApiHandler(commandEnvelope: null)
        {
            UnauthorizedFirstOnlineCall = true,
        };
        var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost:18093", UriKind.Absolute),
        };
        var factory = new StaticHttpClientFactory(client);
        var hidAdapter = new StubHidBridgeHidAdapter(_ => EdgeCommandExecutionResult.Applied(1.0));
        var worker = CreateWorker(factory, hidAdapter, options =>
        {
            options.AccessToken = "expired-token";
            options.KeycloakBaseUrl = "http://localhost:18093";
            options.TokenUsername = "operator.smoke.admin";
            options.TokenPassword = "ChangeMe123!";
            options.TokenClientId = "controlplane-smoke";
        });

        try
        {
            await worker.StartAsync(TestContext.Current.CancellationToken);
            _ = await handler.OnlinePayloadTask.Task.WaitAsync(
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken);
        }
        finally
        {
            await worker.StopAsync(TestContext.Current.CancellationToken);
        }

        Assert.True(handler.TokenEndpointCalled);
        Assert.True(handler.OnlineAuthorizationHeaders.Count >= 2);
        Assert.Equal("Bearer expired-token", handler.OnlineAuthorizationHeaders[0]);
        Assert.Equal("Bearer refreshed-access-token", handler.OnlineAuthorizationHeaders[1]);
    }

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
    /// Ensures command-deck action set keeps mapping to relay payloads and receives Applied acknowledgments.
    /// </summary>
    [Fact]
    public async Task ExecuteAsync_CommandDeckActionSet_MapsArgsAndAcksApplied()
    {
        var commandEnvelopes = new object[]
        {
            BuildEnvelope(1, "cmd-deck-text", "keyboard.text", new Dictionary<string, object?> { ["text"] = "Hello from MicroMeet" }),
            BuildEnvelope(2, "cmd-deck-shortcut", "keyboard.shortcut", new Dictionary<string, object?> { ["shortcut"] = "CTRL+ALT+M" }),
            BuildEnvelope(3, "cmd-deck-move", "mouse.move", new Dictionary<string, object?> { ["dx"] = 24, ["dy"] = 12 }),
            BuildEnvelope(4, "cmd-deck-click", "mouse.click", new Dictionary<string, object?> { ["button"] = "left", ["holdMs"] = 25 }),
            BuildEnvelope(5, "cmd-deck-wheel", "mouse.wheel", new Dictionary<string, object?> { ["delta"] = 1 }),
            BuildEnvelope(6, "cmd-deck-button-down", "mouse.button", new Dictionary<string, object?> { ["button"] = "left", ["down"] = true }),
            BuildEnvelope(7, "cmd-deck-button-up", "mouse.button", new Dictionary<string, object?> { ["button"] = "left", ["down"] = false }),
            BuildEnvelope(8, "cmd-deck-press", "keyboard.press", new Dictionary<string, object?> { ["usage"] = 4, ["modifiers"] = 0 }),
            BuildEnvelope(9, "cmd-deck-reset", "keyboard.reset", new Dictionary<string, object?>()),
        };
        var handler = new RelayApiHandler(commandEnvelopes);
        var client = new HttpClient(handler)
        {
            BaseAddress = new Uri("http://localhost:18093", UriKind.Absolute),
        };
        var factory = new StaticHttpClientFactory(client);
        var hidAdapter = new StubHidBridgeHidAdapter(_ => EdgeCommandExecutionResult.Applied(7.5));
        var worker = CreateWorker(factory, hidAdapter);

        try
        {
            await worker.StartAsync(TestContext.Current.CancellationToken);
            await WaitUntilAsync(
                () => hidAdapter.Executed.Count >= commandEnvelopes.Length,
                TimeSpan.FromSeconds(8),
                "Edge worker did not execute full command-deck action set.");
            await WaitUntilAsync(
                () => handler.AckPayloads.Count >= commandEnvelopes.Length,
                TimeSpan.FromSeconds(8),
                "Relay did not record ACKs for full command-deck action set.");
        }
        finally
        {
            await worker.StopAsync(TestContext.Current.CancellationToken);
        }

        Assert.Collection(
            hidAdapter.Executed,
            command =>
            {
                Assert.Equal("keyboard.text", command.Action);
                Assert.Equal("Hello from MicroMeet", command.Args.Text);
            },
            command =>
            {
                Assert.Equal("keyboard.shortcut", command.Action);
                Assert.Equal("CTRL+ALT+M", command.Args.Shortcut);
            },
            command =>
            {
                Assert.Equal("mouse.move", command.Action);
                Assert.Equal(24, command.Args.Dx);
                Assert.Equal(12, command.Args.Dy);
            },
            command =>
            {
                Assert.Equal("mouse.click", command.Action);
                Assert.Equal("left", command.Args.Button);
                Assert.Equal(25, command.Args.HoldMs);
            },
            command =>
            {
                Assert.Equal("mouse.wheel", command.Action);
                Assert.Equal(1, command.Args.Delta);
            },
            command =>
            {
                Assert.Equal("mouse.button", command.Action);
                Assert.Equal("left", command.Args.Button);
                Assert.True(command.Args.Down);
            },
            command =>
            {
                Assert.Equal("mouse.button", command.Action);
                Assert.Equal("left", command.Args.Button);
                Assert.False(command.Args.Down);
            },
            command =>
            {
                Assert.Equal("keyboard.press", command.Action);
                Assert.Equal(4, command.Args.Usage);
                Assert.Equal(0, command.Args.Modifiers);
            },
            command => Assert.Equal("keyboard.reset", command.Action));

        Assert.All(
            handler.AckPayloads,
            payload => Assert.Contains("\"status\":\"Applied\"", payload, StringComparison.Ordinal));
    }

    /// <summary>
    /// Builds one deterministic relay command envelope for lifecycle regression tests.
    /// </summary>
    private static object BuildEnvelope(
        int sequence,
        string commandId,
        string action,
        IDictionary<string, object?> args)
        => new
        {
            sequence,
            command = new
            {
                commandId,
                action,
                timeoutMs = 5000,
                args,
            },
        };

    /// <summary>
    /// Waits until predicate returns true or throws timeout failure.
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
    /// Creates worker with deterministic options for lifecycle tests.
    /// </summary>
    private static EdgeProxyWorker CreateWorker(
        IHttpClientFactory httpClientFactory,
        IHidBridgeHidAdapter hidAdapter,
        Action<EdgeProxyOptions>? configure = null)
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
        configure?.Invoke(options);
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
        private readonly Queue<object> _commandEnvelopes;
        private int _onlineCallCount;

        public RelayApiHandler(object? commandEnvelope)
        {
            _commandEnvelopes = new Queue<object>();
            if (commandEnvelope is IEnumerable<object> envelopes && commandEnvelope is not string)
            {
                foreach (var envelope in envelopes)
                {
                    _commandEnvelopes.Enqueue(envelope);
                }
            }
            else if (commandEnvelope is not null)
            {
                _commandEnvelopes.Enqueue(commandEnvelope);
            }
        }

        public bool SessionExists { get; set; } = true;

        public bool ReturnNotFoundForControlEnsure { get; set; }

        public int SessionOpenCallCount { get; private set; }

        public bool OnlineSeen { get; private set; }

        public bool HeartbeatSeen { get; private set; }

        public bool OfflineSeen { get; private set; }

        public bool TokenEndpointCalled { get; private set; }

        public bool UnauthorizedFirstOnlineCall { get; set; }

        public string LastRoleHeader { get; private set; } = string.Empty;

        public List<string> OnlineAuthorizationHeaders { get; } = [];

        public TaskCompletionSource<string> AckPayloadTask { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<string> AckPayloads { get; } = [];

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

            if (request.Headers.TryGetValues("X-HidBridge-Role", out var roleValues))
            {
                LastRoleHeader = roleValues.FirstOrDefault() ?? LastRoleHeader;
            }

            if (request.Method == HttpMethod.Post
                && path.EndsWith("/realms/hidbridge-dev/protocol/openid-connect/token", StringComparison.Ordinal))
            {
                TokenEndpointCalled = true;
                return JsonOk(new
                {
                    access_token = "refreshed-access-token",
                    refresh_token = "refresh-token-1",
                    expires_in = 300,
                });
            }

            if (request.Method == HttpMethod.Post
                && path.EndsWith("/api/v1/sessions/session-1/control/ensure", StringComparison.Ordinal))
            {
                if (ReturnNotFoundForControlEnsure)
                {
                    return new HttpResponseMessage(HttpStatusCode.NotFound)
                    {
                        Content = JsonContent.Create(new { sessionId = "session-1", error = "not-found" }),
                    };
                }

                var sessionCreated = !SessionExists;
                SessionExists = true;
                return JsonOk(new
                {
                    requestedSessionId = "session-1",
                    effectiveSessionId = "session-1",
                    lease = new
                    {
                        participantId = "owner:smoke-runner",
                        principalId = "smoke-runner",
                        grantedBy = "smoke-runner",
                        grantedAtUtc = DateTimeOffset.UtcNow,
                        expiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(30),
                    },
                    sessionCreated,
                    sessionReused = false,
                    resolvedAtUtc = DateTimeOffset.UtcNow,
                });
            }

            if (request.Method == HttpMethod.Get
                && path.EndsWith("/api/v1/sessions/session-1", StringComparison.Ordinal))
            {
                if (!SessionExists)
                {
                    return new HttpResponseMessage(HttpStatusCode.NotFound)
                    {
                        Content = JsonContent.Create(new { sessionId = "session-1", error = "missing-session" }),
                    };
                }

                return JsonOk(new { sessionId = "session-1" });
            }

            if (request.Method == HttpMethod.Post
                && path.EndsWith("/api/v1/sessions", StringComparison.Ordinal))
            {
                SessionOpenCallCount++;
                SessionExists = true;
                return JsonOk(new
                {
                    sessionId = "session-1",
                    profile = "UltraLowLatency",
                    requestedBy = "smoke-runner",
                    targetAgentId = "agent-1",
                    targetEndpointId = "endpoint-1",
                    shareMode = "Owner",
                    transportProvider = "WebRtcDataChannel",
                });
            }

            if (!SessionExists
                && path.StartsWith("/api/v1/sessions/session-1/transport/", StringComparison.Ordinal))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    Content = JsonContent.Create(new { sessionId = "session-1", error = "missing-session-route" }),
                };
            }

            if (request.Method == HttpMethod.Post && path.EndsWith("/transport/webrtc/peers/peer-1/online", StringComparison.Ordinal))
            {
                _onlineCallCount++;
                OnlineAuthorizationHeaders.Add(request.Headers.Authorization?.ToString() ?? string.Empty);
                if (UnauthorizedFirstOnlineCall && _onlineCallCount == 1)
                {
                    return new HttpResponseMessage(HttpStatusCode.Unauthorized);
                }

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
                if (_commandEnvelopes.Count > 0)
                {
                    return JsonOk(new { items = new[] { _commandEnvelopes.Dequeue() } });
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
                AckPayloads.Add(body);
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

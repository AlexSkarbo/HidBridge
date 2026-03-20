using System.Net;
using System.Text;
using HidBridge.EdgeProxy.Agent;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace HidBridge.Platform.Tests;

public sealed class EdgeProxyMediaReadinessProbeTests
{
    [Fact]
    public async Task GetReadinessAsync_UartExecutor_DoesNotDeriveControlWsHealthUrl()
    {
        var handler = new RecordingHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"ok":true}""", Encoding.UTF8, "application/json"),
            });
        var probe = CreateProbe(
            handler,
            new EdgeProxyOptions
            {
                CommandExecutor = "uart",
                ControlWsUrl = "ws://127.0.0.1:28092/ws/control",
                MediaHealthUrl = string.Empty,
                RequireMediaReady = true,
                AssumeMediaReadyWithoutProbe = false,
            });

        var snapshot = await probe.GetReadinessAsync(TestContext.Current.CancellationToken);

        Assert.False(snapshot.IsReady);
        Assert.Equal("NoProbeConfigured", snapshot.State);
        Assert.Equal("MediaHealthUrl is not configured.", snapshot.FailureReason);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task GetReadinessAsync_UartExecutor_AssumeReadyWithoutProbe_ReturnsReadyState()
    {
        var handler = new RecordingHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"ok":true}""", Encoding.UTF8, "application/json"),
            });
        var probe = CreateProbe(
            handler,
            new EdgeProxyOptions
            {
                CommandExecutor = "uart",
                ControlWsUrl = "ws://127.0.0.1:28092/ws/control",
                MediaHealthUrl = string.Empty,
                RequireMediaReady = true,
                AssumeMediaReadyWithoutProbe = true,
            });

        var snapshot = await probe.GetReadinessAsync(TestContext.Current.CancellationToken);

        Assert.True(snapshot.IsReady);
        Assert.Equal("Ready", snapshot.State);
        Assert.Null(snapshot.FailureReason);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task GetReadinessAsync_ControlWsExecutor_DerivesControlWsHealthUrl()
    {
        var handler = new RecordingHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"ok":true}""", Encoding.UTF8, "application/json"),
            });
        var probe = CreateProbe(
            handler,
            new EdgeProxyOptions
            {
                CommandExecutor = "controlws",
                ControlWsUrl = "ws://127.0.0.1:28092/ws/control",
                MediaHealthUrl = string.Empty,
                RequireMediaReady = true,
            });

        var snapshot = await probe.GetReadinessAsync(TestContext.Current.CancellationToken);

        Assert.True(snapshot.IsReady);
        Assert.Equal(1, handler.RequestCount);
        Assert.Equal("http://127.0.0.1:28092/health", handler.LastRequestUri);
    }

    private static EdgeProxyMediaReadinessProbe CreateProbe(HttpMessageHandler handler, EdgeProxyOptions options)
    {
        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(2),
        };
        return new EdgeProxyMediaReadinessProbe(
            new StaticHttpClientFactory(client),
            Options.Create(options),
            NullLogger<EdgeProxyMediaReadinessProbe>.Instance);
    }

    private sealed class StaticHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;

        public StaticHttpClientFactory(HttpClient client)
        {
            _client = client;
        }

        public HttpClient CreateClient(string name) => _client;
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responseFactory;

        public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        {
            _responseFactory = responseFactory;
        }

        public int RequestCount { get; private set; }

        public string LastRequestUri { get; private set; } = string.Empty;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            LastRequestUri = request.RequestUri?.ToString() ?? string.Empty;
            return Task.FromResult(_responseFactory(request));
        }
    }
}

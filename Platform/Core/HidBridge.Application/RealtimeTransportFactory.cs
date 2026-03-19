using HidBridge.Abstractions;
using HidBridge.Contracts;
using System.Runtime.CompilerServices;
using System.Collections.Concurrent;
using System.IO;

namespace HidBridge.Application;

/// <summary>
/// Carries runtime policy for realtime transport provider selection.
/// </summary>
public sealed class RealtimeTransportRuntimeOptions
{
    /// <summary>
    /// Gets or sets the default provider selected by runtime configuration.
    /// </summary>
    public RealtimeTransportProvider DefaultProvider { get; init; } = RealtimeTransportProvider.Uart;

    /// <summary>
    /// Gets or sets per-endpoint provider overrides.
    /// </summary>
    public IReadOnlyDictionary<string, RealtimeTransportProvider> EndpointProviders { get; init; }
        = new Dictionary<string, RealtimeTransportProvider>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Carries runtime options for the WebRTC DataChannel transport adapter.
/// </summary>
public sealed class WebRtcTransportRuntimeOptions
{
    /// <summary>
    /// Gets or sets a value indicating whether the adapter requires endpoint capability advertisement.
    /// </summary>
    public bool RequireDataChannelCapability { get; init; }

    /// <summary>
    /// Gets or sets a value indicating whether command dispatch is bridged through connector execute path.
    /// </summary>
    public bool EnableConnectorBridge { get; init; } = true;
}

/// <summary>
/// Resolves transport providers from the set registered in dependency injection.
/// </summary>
public sealed class DefaultRealtimeTransportFactory : IRealtimeTransportFactory
{
    private readonly IReadOnlyDictionary<RealtimeTransportProvider, IRealtimeTransport> _transports;
    private readonly IReadOnlyDictionary<string, RealtimeTransportProvider> _endpointProviders;

    /// <summary>
    /// Creates the transport factory.
    /// </summary>
    public DefaultRealtimeTransportFactory(
        IEnumerable<IRealtimeTransport> transports,
        RealtimeTransportRuntimeOptions options)
    {
        var map = transports
            .GroupBy(x => x.Provider)
            .ToDictionary(group => group.Key, group => group.First());
        _transports = map;
        _endpointProviders = options.EndpointProviders
            ?? new Dictionary<string, RealtimeTransportProvider>(StringComparer.OrdinalIgnoreCase);
        DefaultProvider = map.ContainsKey(options.DefaultProvider)
            ? options.DefaultProvider
            : map.Keys.FirstOrDefault(RealtimeTransportProvider.Uart);
    }

    /// <inheritdoc />
    public RealtimeTransportProvider DefaultProvider { get; }

    /// <inheritdoc />
    public IRealtimeTransport Resolve(RealtimeTransportProvider? provider = null)
    {
        var selected = provider ?? DefaultProvider;
        if (_transports.TryGetValue(selected, out var transport))
        {
            return transport;
        }

        var available = string.Join(", ", _transports.Keys.OrderBy(x => x));
        throw new InvalidOperationException(
            $"Transport provider '{selected}' is not registered. Available providers: {available}.");
    }

    /// <inheritdoc />
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
            throw new InvalidOperationException(
                $"Transport route conflict: requested provider '{context.RequestedProvider}' does not match expected provider '{expectedProvider}'.");
        }

        _ = Resolve(selectedProvider);

        var source = context.RequestedProvider is not null
            ? "request"
            : context.SessionProvider is not null
                ? "session"
                : endpointProvider is not null
                    ? "endpoint"
                    : "default";

        return new RealtimeTransportRouteResolution(selectedProvider, source);
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

/// <summary>
/// Provides a transport implementation backed by registered connector instances.
/// </summary>
public sealed class ConnectorBackedRealtimeTransport : IRealtimeTransport
{
    private readonly IConnectorRegistry _connectorRegistry;

    /// <summary>
    /// Creates one connector-backed transport provider.
    /// </summary>
    public ConnectorBackedRealtimeTransport(
        RealtimeTransportProvider provider,
        IConnectorRegistry connectorRegistry)
    {
        Provider = provider;
        _connectorRegistry = connectorRegistry;
    }

    /// <inheritdoc />
    public RealtimeTransportProvider Provider { get; }

    /// <inheritdoc />
    public async Task ConnectAsync(RealtimeTransportRouteContext route, CancellationToken cancellationToken)
    {
        _ = await _connectorRegistry.ResolveAsync(route.AgentId, cancellationToken)
            ?? throw new InvalidOperationException($"Agent {route.AgentId} is not registered.");
    }

    /// <inheritdoc />
    public async Task<CommandAckBody> SendCommandAsync(RealtimeTransportRouteContext route, CommandRequestBody command, CancellationToken cancellationToken)
    {
        var connector = await _connectorRegistry.ResolveAsync(route.AgentId, cancellationToken);
        if (connector is null)
        {
            return new CommandAckBody(
                command.CommandId,
                CommandStatus.Rejected,
                new ErrorInfo(ErrorDomain.Agent, "E_AGENT_NOT_REGISTERED", $"Agent {route.AgentId} is not registered", false));
        }

        var result = await connector.ExecuteAsync(command, cancellationToken);
        return new CommandAckBody(command.CommandId, result.Status, result.Error, result.Metrics);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<RealtimeTransportMessage> ReceiveAsync(
        RealtimeTransportRouteContext route,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        yield break;
    }

    /// <inheritdoc />
    public Task CloseAsync(RealtimeTransportRouteContext route, string? reason, CancellationToken cancellationToken)
        => Task.CompletedTask;

    /// <inheritdoc />
    public async Task<RealtimeTransportHealth> GetHealthAsync(RealtimeTransportRouteContext route, CancellationToken cancellationToken)
    {
        var connector = await _connectorRegistry.ResolveAsync(route.AgentId, cancellationToken);
        if (connector is null)
        {
            return new RealtimeTransportHealth(
                Provider,
                IsConnected: false,
                Status: AgentStatus.Offline.ToString(),
                Metrics: new Dictionary<string, object?>
                {
                    ["agentId"] = route.AgentId,
                    ["connected"] = false,
                });
        }

        var heartbeat = await connector.HeartbeatAsync(cancellationToken);
        var isConnected = heartbeat.Status is AgentStatus.Online or AgentStatus.Degraded;
        return new RealtimeTransportHealth(
            Provider,
            isConnected,
            heartbeat.Status.ToString(),
            new Dictionary<string, object?>
            {
                ["agentId"] = route.AgentId,
                ["connected"] = isConnected,
                ["uptimeMs"] = heartbeat.UptimeMs,
            });
    }
}

/// <summary>
/// Placeholder WebRTC DataChannel provider used until the MVP adapter is wired.
/// </summary>
public sealed class WebRtcDataChannelRealtimeTransport : IRealtimeTransport
{
    private readonly IConnectorRegistry _connectorRegistry;
    private readonly IEndpointSnapshotStore _endpointSnapshotStore;
    private readonly WebRtcCommandRelayService _commandRelay;
    private readonly WebRtcTransportRuntimeOptions _options;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _connectedRoutes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Creates the WebRTC DataChannel transport adapter.
    /// </summary>
    public WebRtcDataChannelRealtimeTransport(
        IConnectorRegistry connectorRegistry,
        IEndpointSnapshotStore endpointSnapshotStore,
        WebRtcCommandRelayService commandRelay,
        WebRtcTransportRuntimeOptions options)
    {
        _connectorRegistry = connectorRegistry;
        _endpointSnapshotStore = endpointSnapshotStore;
        _commandRelay = commandRelay;
        _options = options;
    }

    /// <inheritdoc />
    public RealtimeTransportProvider Provider => RealtimeTransportProvider.WebRtcDataChannel;

    /// <inheritdoc />
    public async Task ConnectAsync(RealtimeTransportRouteContext route, CancellationToken cancellationToken)
    {
        var endpointSnapshot = await RequireEndpointSnapshotAsync(route.EndpointId, cancellationToken);
        EnsureEndpointSupportsWebRtc(route, endpointSnapshot);

        if (_commandRelay.HasOnlinePeer(route.SessionId, route.EndpointId))
        {
            _connectedRoutes[BuildRouteKey(route)] = DateTimeOffset.UtcNow;
            return;
        }

        var connector = await _connectorRegistry.ResolveAsync(route.AgentId, cancellationToken);
        if (connector is null)
        {
            throw new IOException($"Agent {route.AgentId} is not registered for WebRTC transport.");
        }

        _connectedRoutes[BuildRouteKey(route)] = DateTimeOffset.UtcNow;
    }

    /// <inheritdoc />
    public async Task<CommandAckBody> SendCommandAsync(RealtimeTransportRouteContext route, CommandRequestBody command, CancellationToken cancellationToken)
    {
        if (!_connectedRoutes.ContainsKey(BuildRouteKey(route)))
        {
            return new CommandAckBody(
                command.CommandId,
                CommandStatus.Rejected,
                new ErrorInfo(
                    ErrorDomain.Transport,
                    "E_TRANSPORT_DISCONNECTED",
                    "WebRTC transport route is not connected.",
                    true));
        }

        if (!string.IsNullOrWhiteSpace(route.SessionId)
            && _commandRelay.HasOnlinePeer(route.SessionId, route.EndpointId))
        {
            _ = await _commandRelay.EnqueueCommandAsync(route, command, cancellationToken);
            var relayAck = await _commandRelay.WaitForAckAsync(
                route.SessionId,
                command.CommandId,
                TimeSpan.FromMilliseconds(Math.Max(command.TimeoutMs, 100)),
                cancellationToken);
            if (relayAck is null)
            {
                return new CommandAckBody(
                    command.CommandId,
                    CommandStatus.Timeout,
                    new ErrorInfo(
                        ErrorDomain.Transport,
                        "E_TRANSPORT_TIMEOUT",
                        "WebRTC relay ACK timeout.",
                        true));
            }

            var relayMetrics = relayAck.Metrics is null
                ? new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, double>(relayAck.Metrics, StringComparer.OrdinalIgnoreCase);
            relayMetrics["transportRelayMode"] = 1;
            return relayAck with { Metrics = relayMetrics };
        }

        if (!_options.EnableConnectorBridge)
        {
            return new CommandAckBody(
                command.CommandId,
                CommandStatus.Rejected,
                new ErrorInfo(
                    ErrorDomain.Transport,
                    "E_TRANSPORT_NOT_IMPLEMENTED",
                    "WebRTC connector bridge is disabled.",
                    false));
        }

        var connector = await _connectorRegistry.ResolveAsync(route.AgentId, cancellationToken);
        if (connector is null)
        {
            return new CommandAckBody(
                command.CommandId,
                CommandStatus.Rejected,
                new ErrorInfo(
                    ErrorDomain.Transport,
                    "E_TRANSPORT_DISCONNECTED",
                    $"Agent {route.AgentId} is not registered.",
                    true));
        }

        var result = await connector.ExecuteAsync(command, cancellationToken);
        var metrics = result.Metrics is null
            ? new Dictionary<string, double>()
            : new Dictionary<string, double>(result.Metrics);
        metrics["transportBridgeMode"] = 1;

        return new CommandAckBody(
            command.CommandId,
            result.Status,
            result.Error,
            metrics);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<RealtimeTransportMessage> ReceiveAsync(
        RealtimeTransportRouteContext route,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        yield break;
    }

    /// <inheritdoc />
    public Task CloseAsync(RealtimeTransportRouteContext route, string? reason, CancellationToken cancellationToken)
    {
        _connectedRoutes.TryRemove(BuildRouteKey(route), out _);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<RealtimeTransportHealth> GetHealthAsync(RealtimeTransportRouteContext route, CancellationToken cancellationToken)
    {
        var routeKey = BuildRouteKey(route);
        var endpointSupportsWebRtc = false;
        var endpointId = route.EndpointId;
        if (!string.IsNullOrWhiteSpace(endpointId))
        {
            var endpoint = await TryResolveEndpointSnapshotAsync(endpointId, cancellationToken);
            endpointSupportsWebRtc = endpoint?.Capabilities.Any(IsWebRtcCapability) == true;
        }

        var relayMetrics = await _commandRelay.GetSessionMetricsAsync(route.SessionId, endpointId, cancellationToken);
        var relayOnlinePeers = relayMetrics.TryGetValue("onlinePeerCount", out var onlinePeersValue)
            && int.TryParse(onlinePeersValue?.ToString(), out var parsedOnlinePeers)
            ? parsedOnlinePeers
            : 0;
        var connected = _connectedRoutes.ContainsKey(routeKey) || relayOnlinePeers > 0;
        var bridgeMode = relayOnlinePeers > 0
            ? "webrtc-relay"
            : _options.EnableConnectorBridge
                ? "connector-bridge"
                : "disabled";
        var metrics = new Dictionary<string, object?>
        {
            ["agentId"] = route.AgentId,
            ["endpointId"] = endpointId,
            ["connected"] = connected,
            ["capabilityRequired"] = _options.RequireDataChannelCapability,
            ["endpointSupportsWebRtc"] = endpointSupportsWebRtc,
            ["bridgeMode"] = bridgeMode,
        };
        foreach (var pair in relayMetrics)
        {
            metrics[pair.Key] = pair.Value;
        }

        return new RealtimeTransportHealth(
            Provider,
            connected,
            connected ? "Connected" : "Disconnected",
            metrics);
    }

    private async Task<EndpointSnapshot> RequireEndpointSnapshotAsync(string? endpointId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(endpointId))
        {
            throw new InvalidDataException("WebRTC transport route requires endpointId.");
        }

        var endpoint = await TryResolveEndpointSnapshotAsync(endpointId, cancellationToken);
        return endpoint
            ?? throw new IOException($"Endpoint {endpointId} is not registered for WebRTC transport.");
    }

    private async Task<EndpointSnapshot?> TryResolveEndpointSnapshotAsync(string endpointId, CancellationToken cancellationToken)
    {
        var endpoints = await _endpointSnapshotStore.ListAsync(cancellationToken);
        return endpoints.FirstOrDefault(x => string.Equals(x.EndpointId, endpointId, StringComparison.OrdinalIgnoreCase));
    }

    private void EnsureEndpointSupportsWebRtc(RealtimeTransportRouteContext route, EndpointSnapshot endpoint)
    {
        if (!_options.RequireDataChannelCapability)
        {
            return;
        }

        if (endpoint.Capabilities.Any(IsWebRtcCapability))
        {
            return;
        }

        throw new InvalidDataException(
            $"Endpoint {route.EndpointId} does not advertise {CapabilityNames.TransportWebRtcDataChannelV1} capability.");
    }

    private static bool IsWebRtcCapability(CapabilityDescriptor capability)
        => string.Equals(capability.Name, CapabilityNames.TransportWebRtcDataChannelV1, StringComparison.OrdinalIgnoreCase);

    private static string BuildRouteKey(RealtimeTransportRouteContext route)
        => string.Join(
            "|",
            route.AgentId,
            route.EndpointId ?? string.Empty,
            route.SessionId ?? string.Empty);
}

/// <summary>
/// Parses and normalizes transport provider values from runtime configuration and command args.
/// </summary>
public static class RealtimeTransportProviderParser
{
    /// <summary>
    /// Parses a provider token.
    /// </summary>
    public static bool TryParse(string? value, out RealtimeTransportProvider provider)
    {
        provider = RealtimeTransportProvider.Uart;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Trim().Replace("_", "-", StringComparison.OrdinalIgnoreCase).ToLowerInvariant();
        switch (normalized)
        {
            case "uart":
                provider = RealtimeTransportProvider.Uart;
                return true;
            case "webrtc":
            case "webrtc-datachannel":
            case "webrtcdatachannel":
            case "datachannel":
                provider = RealtimeTransportProvider.WebRtcDataChannel;
                return true;
            default:
                return false;
        }
    }
}

using HidBridge.Abstractions;
using HidBridge.Contracts;
using System.Runtime.CompilerServices;

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
    /// <inheritdoc />
    public RealtimeTransportProvider Provider => RealtimeTransportProvider.WebRtcDataChannel;

    /// <inheritdoc />
    public Task ConnectAsync(RealtimeTransportRouteContext route, CancellationToken cancellationToken)
        => Task.CompletedTask;

    /// <inheritdoc />
    public Task<CommandAckBody> SendCommandAsync(RealtimeTransportRouteContext route, CommandRequestBody command, CancellationToken cancellationToken)
        => Task.FromResult(
            new CommandAckBody(
                command.CommandId,
                CommandStatus.Rejected,
                new ErrorInfo(
                    ErrorDomain.Transport,
                    "E_TRANSPORT_NOT_IMPLEMENTED",
                    "WebRTC DataChannel transport provider is not implemented yet.",
                    false)));

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
    public Task<RealtimeTransportHealth> GetHealthAsync(RealtimeTransportRouteContext route, CancellationToken cancellationToken)
        => Task.FromResult(
            new RealtimeTransportHealth(
                Provider,
                IsConnected: false,
                Status: "NotImplemented",
                Metrics: new Dictionary<string, object?>
                {
                    ["agentId"] = route.AgentId,
                    ["connected"] = false,
                }));
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
            case "datachannel":
                provider = RealtimeTransportProvider.WebRtcDataChannel;
                return true;
            default:
                return false;
        }
    }
}

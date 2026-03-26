using HidBridge.Abstractions;
using HidBridge.Contracts;
using System.Runtime.CompilerServices;
using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

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

    /// <summary>
    /// Gets or sets a value indicating whether API transport should try direct DCD control-channel dispatch before signal/relay fallback.
    /// </summary>
    public bool EnableDcdControlBridge { get; init; }
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
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly IConnectorRegistry _connectorRegistry;
    private readonly IEndpointSnapshotStore _endpointSnapshotStore;
    private readonly WebRtcCommandRelayService _commandRelay;
    private readonly IWebRtcSignalingStore _signalingStore;
    private readonly IWebRtcDcdControlBridge? _dcdControlBridge;
    private readonly WebRtcTransportRuntimeOptions _options;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _connectedRoutes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, int> _signalCursors = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _signalGates = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Creates the WebRTC DataChannel transport adapter.
    /// </summary>
    public WebRtcDataChannelRealtimeTransport(
        IConnectorRegistry connectorRegistry,
        IEndpointSnapshotStore endpointSnapshotStore,
        WebRtcCommandRelayService commandRelay,
        IWebRtcSignalingStore signalingStore,
        WebRtcTransportRuntimeOptions options,
        IWebRtcDcdControlBridge? dcdControlBridge = null)
    {
        _connectorRegistry = connectorRegistry;
        _endpointSnapshotStore = endpointSnapshotStore;
        _commandRelay = commandRelay;
        _signalingStore = signalingStore;
        _options = options;
        _dcdControlBridge = dcdControlBridge;
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

        if (!string.IsNullOrWhiteSpace(route.SessionId))
        {
            if (_options.EnableDcdControlBridge && _dcdControlBridge is not null)
            {
                var dcdAck = await _dcdControlBridge.TrySendAsync(route, command, cancellationToken);
                if (dcdAck is not null)
                {
                    return dcdAck;
                }
            }

            var directAck = await TrySendDirectSignalCommandAsync(route, command, cancellationToken);
            if (directAck is not null)
            {
                return directAck;
            }
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
            : _options.EnableDcdControlBridge
                ? "dcd-control-bridge"
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
            ["dcdControlBridgeEnabled"] = _options.EnableDcdControlBridge,
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

    /// <summary>
    /// Attempts direct DCD command delivery through signaling inbox/ack path.
    /// Returns null when no direct-capable peer is available for the route.
    /// </summary>
    private async Task<CommandAckBody?> TrySendDirectSignalCommandAsync(
        RealtimeTransportRouteContext route,
        CommandRequestBody command,
        CancellationToken cancellationToken)
    {
        var sessionId = route.SessionId;
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return null;
        }

        var directPeer = await ResolveDirectSignalPeerAsync(route, cancellationToken);
        if (directPeer is null)
        {
            return null;
        }

        var routeKey = BuildRouteKey(route);
        var gate = _signalGates.GetOrAdd(routeKey, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var controlPeerId = BuildControlPlanePeerId(sessionId);
            await _signalingStore.AppendAsync(
                sessionId,
                WebRtcSignalKind.Command,
                controlPeerId,
                directPeer.PeerId,
                JsonSerializer.Serialize(BuildSignalCommand(command), JsonOptions),
                mid: null,
                mLineIndex: null,
                cancellationToken);

            _signalCursors.TryGetValue(routeKey, out var afterSequence);
            var timeout = TimeSpan.FromMilliseconds(Math.Max(command.TimeoutMs, 100));
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(timeout);

            while (true)
            {
                timeoutCts.Token.ThrowIfCancellationRequested();

                var signals = await _signalingStore.ListAsync(
                    sessionId,
                    recipientPeerId: controlPeerId,
                    afterSequence: afterSequence > 0 ? afterSequence : null,
                    limit: 100,
                    timeoutCts.Token);
                foreach (var signal in signals.OrderBy(static x => x.Sequence))
                {
                    afterSequence = Math.Max(afterSequence, signal.Sequence);
                    if (signal.Kind != WebRtcSignalKind.Ack)
                    {
                        continue;
                    }

                    if (!string.Equals(signal.SenderPeerId, directPeer.PeerId, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (!TryParseSignalAck(signal.Payload, command.CommandId, out var ack))
                    {
                        continue;
                    }

                    _signalCursors[routeKey] = afterSequence;
                    return MarkDcdDirectMetric(ack);
                }

                _signalCursors[routeKey] = afterSequence;
                await Task.Delay(50, timeoutCts.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new CommandAckBody(
                command.CommandId,
                CommandStatus.Timeout,
                new ErrorInfo(
                    ErrorDomain.Transport,
                    "E_TRANSPORT_TIMEOUT",
                    "WebRTC direct signal ACK timeout.",
                    true),
                new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
                {
                    ["transportEngineDcdDirect"] = 1d,
                });
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Resolves one online peer that advertises DCD transport-engine metadata for the current route.
    /// </summary>
    private async Task<WebRtcPeerStateBody?> ResolveDirectSignalPeerAsync(
        RealtimeTransportRouteContext route,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(route.SessionId))
        {
            return null;
        }

        var peers = await _commandRelay.ListPeersAsync(route.SessionId, cancellationToken);
        return peers
            .Where(static peer => peer.IsOnline)
            .Where(peer => IsRoutePeerMatch(route, peer))
            .Where(static peer => IsDcdDirectPeer(peer.Metadata))
            .OrderByDescending(static peer => peer.LastSeenAtUtc)
            .FirstOrDefault();
    }

    /// <summary>
    /// Checks whether peer endpoint metadata is compatible with route endpoint selector.
    /// </summary>
    private static bool IsRoutePeerMatch(RealtimeTransportRouteContext route, WebRtcPeerStateBody peer)
    {
        return string.IsNullOrWhiteSpace(route.EndpointId)
            || string.IsNullOrWhiteSpace(peer.EndpointId)
            || string.Equals(route.EndpointId, peer.EndpointId, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Checks whether peer metadata marks this peer as DCD direct-command capable.
    /// </summary>
    private static bool IsDcdDirectPeer(IReadOnlyDictionary<string, string>? metadata)
    {
        if (metadata is null || !metadata.TryGetValue("transportEngine", out var value) || string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value
            .Trim()
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();
        return normalized is "dcd" or "datachanneldotnet";
    }

    /// <summary>
    /// Builds one direct-signal command payload from command request.
    /// </summary>
    private static TransportCommandMessageBody BuildSignalCommand(CommandRequestBody command)
    {
        return new TransportCommandMessageBody(
            Kind: TransportMessageKind.Command,
            CommandId: command.CommandId,
            SessionId: command.SessionId,
            Action: command.Action,
            Args: BuildTransportHidArgs(command.Args),
            TimeoutMs: Math.Max(1000, command.TimeoutMs),
            CreatedAtUtc: DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Converts dynamic command args into typed transport HID args payload.
    /// </summary>
    private static TransportHidCommandArgsBody BuildTransportHidArgs(IReadOnlyDictionary<string, object?> args)
    {
        return new TransportHidCommandArgsBody(
            Text: ReadString(args, "text"),
            Shortcut: ReadString(args, "shortcut"),
            Usage: ReadInt(args, "usage"),
            Modifiers: ReadInt(args, "modifiers"),
            Dx: ReadInt(args, "dx"),
            Dy: ReadInt(args, "dy"),
            Wheel: ReadInt(args, "wheel"),
            Delta: ReadInt(args, "delta"),
            Button: ReadString(args, "button"),
            Down: ReadBool(args, "down"),
            HoldMs: ReadInt(args, "holdMs"),
            InterfaceSelector: ReadInt(args, "itfSel"));
    }

    /// <summary>
    /// Parses one direct ACK signal payload and validates command identity.
    /// </summary>
    private static bool TryParseSignalAck(string payload, string commandId, out CommandAckBody ack)
    {
        ack = default!;
        if (string.IsNullOrWhiteSpace(payload))
        {
            return false;
        }

        try
        {
            var transportAck = JsonSerializer.Deserialize<TransportAckMessageBody>(payload, JsonOptions);
            if (transportAck is not null
                && transportAck.Kind == TransportMessageKind.Ack
                && string.Equals(transportAck.CommandId, commandId, StringComparison.OrdinalIgnoreCase))
            {
                ack = new CommandAckBody(
                    transportAck.CommandId,
                    transportAck.Status,
                    transportAck.Error,
                    transportAck.Metrics);
                return true;
            }
        }
        catch (JsonException)
        {
        }

        try
        {
            var commandAck = JsonSerializer.Deserialize<CommandAckBody>(payload, JsonOptions);
            if (commandAck is not null
                && string.Equals(commandAck.CommandId, commandId, StringComparison.OrdinalIgnoreCase))
            {
                ack = commandAck;
                return true;
            }
        }
        catch (JsonException)
        {
        }

        return false;
    }

    /// <summary>
    /// Adds deterministic marker for direct DCD command path.
    /// </summary>
    private static CommandAckBody MarkDcdDirectMetric(CommandAckBody ack)
    {
        var metrics = ack.Metrics is null
            ? new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, double>(ack.Metrics, StringComparer.OrdinalIgnoreCase);
        metrics["transportEngineDcdDirect"] = 1d;
        return ack with { Metrics = metrics };
    }

    /// <summary>
    /// Builds deterministic control-plane recipient peer id for direct DCD signal replies.
    /// </summary>
    private static string BuildControlPlanePeerId(string sessionId)
        => $"controlplane:{sessionId}";

    /// <summary>
    /// Reads string argument by key.
    /// </summary>
    private static string? ReadString(IReadOnlyDictionary<string, object?> args, string key)
    {
        return args.TryGetValue(key, out var value) ? value?.ToString() : null;
    }

    /// <summary>
    /// Reads integer argument by key.
    /// </summary>
    private static int? ReadInt(IReadOnlyDictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        if (value is JsonElement jsonElement)
        {
            if (jsonElement.ValueKind == JsonValueKind.Number && jsonElement.TryGetInt32(out var jsonNumber))
            {
                return jsonNumber;
            }

            if (jsonElement.ValueKind == JsonValueKind.String
                && int.TryParse(jsonElement.GetString(), out var jsonStringNumber))
            {
                return jsonStringNumber;
            }
        }

        return int.TryParse(value.ToString(), out var parsed) ? parsed : null;
    }

    /// <summary>
    /// Reads boolean argument by key.
    /// </summary>
    private static bool? ReadBool(IReadOnlyDictionary<string, object?> args, string key)
    {
        if (!args.TryGetValue(key, out var value) || value is null)
        {
            return null;
        }

        if (value is JsonElement jsonElement)
        {
            if (jsonElement.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                return jsonElement.GetBoolean();
            }

            if (jsonElement.ValueKind == JsonValueKind.String
                && bool.TryParse(jsonElement.GetString(), out var jsonStringBool))
            {
                return jsonStringBool;
            }
        }

        return bool.TryParse(value.ToString(), out var parsed) ? parsed : null;
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
        var persisted = endpoints.FirstOrDefault(x => string.Equals(x.EndpointId, endpointId, StringComparison.OrdinalIgnoreCase));
        var connector = (await _connectorRegistry.ListAsync(cancellationToken))
            .FirstOrDefault(x => string.Equals(x.EndpointId, endpointId, StringComparison.OrdinalIgnoreCase));

        if (persisted is null)
        {
            return connector is null
                ? null
                : new EndpointSnapshot(
                    connector.EndpointId,
                    connector.Capabilities,
                    DateTimeOffset.UtcNow);
        }

        if (connector is null)
        {
            return persisted;
        }

        // Keep persisted snapshot timestamps/state but merge capabilities from active connector descriptor.
        // This protects WebRTC capability checks when SQL snapshot was reset by test/setup workflows.
        var mergedCapabilities = persisted.Capabilities
            .Concat(connector.Capabilities)
            .GroupBy(static capability => capability.Name, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .ToArray();

        return persisted with { Capabilities = mergedCapabilities };
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

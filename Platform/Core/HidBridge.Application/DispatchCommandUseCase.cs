using HidBridge.Abstractions;
using HidBridge.Contracts;
using System.IO;
using System.Net.Sockets;

namespace HidBridge.Application;

/// <summary>
/// Carries runtime command-dispatch policy for transport fallback behavior.
/// </summary>
public sealed class DispatchCommandRuntimeOptions
{
    /// <summary>
    /// Gets or sets a value indicating whether WebRTC transport failures may fallback to the default provider.
    /// </summary>
    public bool EnableDefaultProviderFallbackOnWebRtcError { get; init; } = true;
}

/// <summary>
/// Dispatches one command through the configured realtime transport fabric.
/// </summary>
public sealed class DispatchCommandUseCase
{
    private readonly IConnectorRegistry _connectorRegistry;
    private readonly IEventWriter _eventWriter;
    private readonly IRealtimeTransportFactory? _realtimeTransportFactory;
    private readonly DispatchCommandRuntimeOptions _options;

    /// <summary>
    /// Creates the command dispatch use case.
    /// </summary>
    public DispatchCommandUseCase(
        IConnectorRegistry connectorRegistry,
        IEventWriter eventWriter,
        IRealtimeTransportFactory? realtimeTransportFactory = null,
        DispatchCommandRuntimeOptions? options = null)
    {
        _connectorRegistry = connectorRegistry;
        _eventWriter = eventWriter;
        _realtimeTransportFactory = realtimeTransportFactory;
        _options = options ?? new DispatchCommandRuntimeOptions();
    }

    /// <summary>
    /// Dispatches a command to a resolved connector and emits telemetry about the result.
    /// </summary>
    /// <param name="agentId">The target agent identifier.</param>
    /// <param name="request">The command request payload.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The command acknowledgment returned to the caller.</returns>
    public Task<CommandAckBody> ExecuteAsync(string agentId, CommandRequestBody request, CancellationToken cancellationToken)
        => ExecuteAsync(agentId, request, endpointId: null, sessionTransportProvider: null, cancellationToken);

    /// <summary>
    /// Dispatches a command to a resolved connector and emits telemetry about the result.
    /// </summary>
    /// <param name="agentId">The target agent identifier.</param>
    /// <param name="request">The command request payload.</param>
    /// <param name="endpointId">The session-bound endpoint identifier when one exists.</param>
    /// <param name="sessionTransportProvider">The session-bound transport provider when one exists.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The command acknowledgment returned to the caller.</returns>
    public async Task<CommandAckBody> ExecuteAsync(
        string agentId,
        CommandRequestBody request,
        string? endpointId,
        RealtimeTransportProvider? sessionTransportProvider,
        CancellationToken cancellationToken)
    {
        CommandAckBody ack;
        var transportProviderName = "connector";
        var transportProviderSource = "legacy-connector";
        var transportErrorKind = string.Empty;
        var fallbackAttempted = false;
        var fallbackSucceeded = false;
        var fallbackFromProvider = string.Empty;
        var fallbackToProvider = string.Empty;

        if (_realtimeTransportFactory is null)
        {
            var connector = await _connectorRegistry.ResolveAsync(agentId, cancellationToken);
            if (connector is null)
            {
                ack = new CommandAckBody(
                    request.CommandId,
                    CommandStatus.Rejected,
                    new ErrorInfo(ErrorDomain.Agent, "E_AGENT_NOT_REGISTERED", $"Agent {agentId} is not registered", false));
            }
            else
            {
                var result = await connector.ExecuteAsync(request, cancellationToken);
                ack = new CommandAckBody(request.CommandId, result.Status, result.Error, result.Metrics);
            }
        }
        else
        {
            var providerOverride = ResolveTransportProviderOverride(request.Args);
            RealtimeTransportRouteResolution routeResolution;
            try
            {
                routeResolution = _realtimeTransportFactory.ResolveRoute(
                    new RealtimeTransportRoutePolicyContext(
                        EndpointId: endpointId,
                        SessionProvider: sessionTransportProvider,
                        RequestedProvider: providerOverride));
                var selectedProvider = routeResolution.Provider;
                transportProviderName = selectedProvider.ToString();
                transportProviderSource = routeResolution.Source;
                var primaryAttempt = await DispatchViaProviderAsync(
                    selectedProvider,
                    agentId,
                    endpointId,
                    request,
                    cancellationToken);
                ack = primaryAttempt.Ack;
                transportErrorKind = primaryAttempt.TransportErrorKind;

                var shouldAttemptFallback = ShouldAttemptDefaultProviderFallback(
                    selectedProvider,
                    providerOverride,
                    primaryAttempt.Ack,
                    primaryAttempt.TransportErrorKind);
                if (shouldAttemptFallback
                    && _realtimeTransportFactory.DefaultProvider != selectedProvider)
                {
                    fallbackAttempted = true;
                    fallbackFromProvider = selectedProvider.ToString();
                    fallbackToProvider = _realtimeTransportFactory.DefaultProvider.ToString();

                    var fallbackAttempt = await DispatchViaProviderAsync(
                        _realtimeTransportFactory.DefaultProvider,
                        agentId,
                        endpointId,
                        request,
                        cancellationToken);
                    if (IsAcceptedCommandAck(fallbackAttempt.Ack.Status))
                    {
                        fallbackSucceeded = true;
                        ack = MergeFallbackMetadata(
                            fallbackAttempt.Ack,
                            fallbackApplied: true,
                            fromProvider: fallbackFromProvider,
                            toProvider: fallbackToProvider);
                        transportProviderName = _realtimeTransportFactory.DefaultProvider.ToString();
                        transportProviderSource = "fallback-default";
                        transportErrorKind = string.Empty;
                    }
                    else
                    {
                        ack = MergeFallbackMetadata(
                            ack,
                            fallbackApplied: false,
                            fromProvider: fallbackFromProvider,
                            toProvider: fallbackToProvider);
                    }
                }
            }
            catch (Exception ex)
            {
                var (errorCode, errorKind, retriable) = MapTransportException(ex);
                transportErrorKind = errorKind.ToString();
                ack = new CommandAckBody(
                    request.CommandId,
                    CommandStatus.Rejected,
                    new ErrorInfo(
                        ErrorDomain.Transport,
                        errorCode,
                        ex.Message,
                        retriable));
            }
        }

        await _eventWriter.WriteTelemetryAsync(
            new TelemetryEventBody(
                "command",
                new Dictionary<string, object?>
                {
                    ["agentId"] = agentId,
                    ["commandId"] = request.CommandId,
                    ["action"] = request.Action,
                    ["status"] = ack.Status.ToString(),
                    ["transportProvider"] = transportProviderName,
                    ["transportProviderSource"] = transportProviderSource,
                    ["transportErrorKind"] = transportErrorKind,
                    ["transportFallbackAttempted"] = fallbackAttempted,
                    ["transportFallbackSucceeded"] = fallbackSucceeded,
                    ["transportFallbackFrom"] = fallbackFromProvider,
                    ["transportFallbackTo"] = fallbackToProvider,
                },
                request.SessionId),
            cancellationToken);

        return ack;
    }

    private static RealtimeTransportProvider? ResolveTransportProviderOverride(IReadOnlyDictionary<string, object?> args)
    {
        if (!args.TryGetValue("transportProvider", out var value) || value is null)
        {
            return null;
        }

        var providerToken = value.ToString();
        return RealtimeTransportProviderParser.TryParse(providerToken, out var provider)
            ? provider
            : null;
    }

    private static (string ErrorCode, RealtimeTransportErrorKind ErrorKind, bool Retriable) MapTransportException(Exception exception)
    {
        if (exception is OperationCanceledException or TimeoutException)
        {
            return ("E_TRANSPORT_TIMEOUT", RealtimeTransportErrorKind.Timeout, true);
        }

        if (exception is IOException or SocketException)
        {
            return ("E_TRANSPORT_DISCONNECTED", RealtimeTransportErrorKind.Disconnected, true);
        }

        var message = exception.Message;
        if (message.Contains("route conflict", StringComparison.OrdinalIgnoreCase))
        {
            return ("E_TRANSPORT_ROUTE_CONFLICT", RealtimeTransportErrorKind.Rejected, false);
        }

        if (message.Contains("not registered", StringComparison.OrdinalIgnoreCase))
        {
            return ("E_TRANSPORT_PROVIDER_NOT_REGISTERED", RealtimeTransportErrorKind.Rejected, false);
        }

        if (exception is FormatException or InvalidDataException or NotSupportedException)
        {
            return ("E_TRANSPORT_PROTOCOL", RealtimeTransportErrorKind.ProtocolError, false);
        }

        return ("E_TRANSPORT_REJECTED", RealtimeTransportErrorKind.Rejected, false);
    }

    private async Task<TransportDispatchAttempt> DispatchViaProviderAsync(
        RealtimeTransportProvider provider,
        string agentId,
        string? endpointId,
        CommandRequestBody request,
        CancellationToken cancellationToken)
    {
        try
        {
            var transport = _realtimeTransportFactory!.Resolve(provider);
            var route = new RealtimeTransportRouteContext(
                AgentId: agentId,
                EndpointId: endpointId,
                SessionId: request.SessionId,
                RoomId: request.SessionId);
            await transport.ConnectAsync(route, cancellationToken);

            try
            {
                var ack = await transport.SendCommandAsync(route, request, cancellationToken);
                return new TransportDispatchAttempt(ack, TransportErrorKind: string.Empty);
            }
            finally
            {
                try
                {
                    await transport.CloseAsync(route, "command-dispatch-completed", cancellationToken);
                }
                catch
                {
                    // Close errors should not overwrite command acknowledgment semantics.
                }
            }
        }
        catch (Exception ex)
        {
            var (errorCode, errorKind, retriable) = MapTransportException(ex);
            var ack = new CommandAckBody(
                request.CommandId,
                CommandStatus.Rejected,
                new ErrorInfo(
                    ErrorDomain.Transport,
                    errorCode,
                    ex.Message,
                    retriable));
            return new TransportDispatchAttempt(ack, TransportErrorKind: errorKind.ToString());
        }
    }

    private bool ShouldAttemptDefaultProviderFallback(
        RealtimeTransportProvider selectedProvider,
        RealtimeTransportProvider? providerOverride,
        CommandAckBody ack,
        string transportErrorKind)
    {
        if (!_options.EnableDefaultProviderFallbackOnWebRtcError)
        {
            return false;
        }

        if (providerOverride is not null)
        {
            return false;
        }

        if (selectedProvider != RealtimeTransportProvider.WebRtcDataChannel)
        {
            return false;
        }

        if (string.Equals(transportErrorKind, RealtimeTransportErrorKind.Timeout.ToString(), StringComparison.OrdinalIgnoreCase)
            || string.Equals(transportErrorKind, RealtimeTransportErrorKind.Disconnected.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (ack.Status == CommandStatus.Timeout)
        {
            return true;
        }

        if (ack.Error is null || ack.Error.Domain != ErrorDomain.Transport)
        {
            return false;
        }

        return ack.Error.Code is "E_TRANSPORT_DISCONNECTED"
            or "E_TRANSPORT_TIMEOUT"
            or "E_TRANSPORT_NOT_IMPLEMENTED"
            or "E_TRANSPORT_PROTOCOL";
    }

    private static CommandAckBody MergeFallbackMetadata(
        CommandAckBody ack,
        bool fallbackApplied,
        string fromProvider,
        string toProvider)
    {
        var metrics = ack.Metrics is null
            ? new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, double>(ack.Metrics, StringComparer.OrdinalIgnoreCase);
        metrics["transportFallbackAttempted"] = 1;
        metrics["transportFallbackApplied"] = fallbackApplied ? 1 : 0;
        metrics["transportFallbackFromWebRtc"] = string.Equals(fromProvider, RealtimeTransportProvider.WebRtcDataChannel.ToString(), StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        metrics["transportFallbackToUart"] = string.Equals(toProvider, RealtimeTransportProvider.Uart.ToString(), StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        return ack with { Metrics = metrics };
    }

    private static bool IsAcceptedCommandAck(CommandStatus status)
        => status is CommandStatus.Accepted or CommandStatus.Applied;

    private sealed record TransportDispatchAttempt(
        CommandAckBody Ack,
        string TransportErrorKind);
}

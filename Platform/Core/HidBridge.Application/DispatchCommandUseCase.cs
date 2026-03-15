using HidBridge.Abstractions;
using HidBridge.Contracts;
using System.IO;
using System.Net.Sockets;

namespace HidBridge.Application;

/// <summary>
/// Dispatches one command through the configured realtime transport fabric.
/// </summary>
public sealed class DispatchCommandUseCase
{
    private readonly IConnectorRegistry _connectorRegistry;
    private readonly IEventWriter _eventWriter;
    private readonly IRealtimeTransportFactory? _realtimeTransportFactory;

    /// <summary>
    /// Creates the command dispatch use case.
    /// </summary>
    public DispatchCommandUseCase(
        IConnectorRegistry connectorRegistry,
        IEventWriter eventWriter,
        IRealtimeTransportFactory? realtimeTransportFactory = null)
    {
        _connectorRegistry = connectorRegistry;
        _eventWriter = eventWriter;
        _realtimeTransportFactory = realtimeTransportFactory;
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
                var transport = _realtimeTransportFactory.Resolve(selectedProvider);
                var route = new RealtimeTransportRouteContext(
                    AgentId: agentId,
                    EndpointId: endpointId,
                    SessionId: request.SessionId,
                    RoomId: request.SessionId);
                await transport.ConnectAsync(route, cancellationToken);
                ack = await transport.SendCommandAsync(route, request, cancellationToken);
                await transport.CloseAsync(route, "command-dispatch-completed", cancellationToken);
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
}

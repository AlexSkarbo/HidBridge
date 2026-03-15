using HidBridge.Abstractions;
using HidBridge.Contracts;

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
    public async Task<CommandAckBody> ExecuteAsync(string agentId, CommandRequestBody request, CancellationToken cancellationToken)
    {
        CommandAckBody ack;
        var transportProviderName = "connector";

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
            var selectedProvider = providerOverride ?? _realtimeTransportFactory.DefaultProvider;
            transportProviderName = selectedProvider.ToString();
            try
            {
                var transport = _realtimeTransportFactory.Resolve(selectedProvider);
                var route = new RealtimeTransportRouteContext(
                    AgentId: agentId,
                    SessionId: request.SessionId,
                    RoomId: request.SessionId);
                await transport.ConnectAsync(route, cancellationToken);
                ack = await transport.SendCommandAsync(route, request, cancellationToken);
                await transport.CloseAsync(route, "command-dispatch-completed", cancellationToken);
            }
            catch (Exception ex)
            {
                ack = new CommandAckBody(
                    request.CommandId,
                    CommandStatus.Rejected,
                    new ErrorInfo(
                        ErrorDomain.Transport,
                        "E_TRANSPORT_DISPATCH_FAILED",
                        ex.Message,
                        true));
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
}

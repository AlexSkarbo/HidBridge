using HidBridge.Abstractions;
using HidBridge.Contracts;

namespace HidBridge.Application;

/// <summary>
/// Resolves a connector and dispatches one command to it.
/// </summary>
public sealed class DispatchCommandUseCase
{
    private readonly IConnectorRegistry _connectorRegistry;
    private readonly IEventWriter _eventWriter;

    /// <summary>
    /// Creates the command dispatch use case.
    /// </summary>
    public DispatchCommandUseCase(IConnectorRegistry connectorRegistry, IEventWriter eventWriter)
    {
        _connectorRegistry = connectorRegistry;
        _eventWriter = eventWriter;
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
        var connector = await _connectorRegistry.ResolveAsync(agentId, cancellationToken);
        if (connector is null)
        {
            return new CommandAckBody(
                request.CommandId,
                CommandStatus.Rejected,
                new ErrorInfo(ErrorDomain.Agent, "E_AGENT_NOT_REGISTERED", $"Agent {agentId} is not registered", false));
        }

        var result = await connector.ExecuteAsync(request, cancellationToken);
        var ack = new CommandAckBody(request.CommandId, result.Status, result.Error, result.Metrics);

        await _eventWriter.WriteTelemetryAsync(
            new TelemetryEventBody(
                "command",
                new Dictionary<string, object?>
                {
                    ["agentId"] = agentId,
                    ["commandId"] = request.CommandId,
                    ["action"] = request.Action,
                    ["status"] = ack.Status.ToString(),
                },
                request.SessionId),
            cancellationToken);

        return ack;
    }
}

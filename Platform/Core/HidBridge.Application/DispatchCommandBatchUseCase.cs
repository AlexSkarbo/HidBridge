using HidBridge.Abstractions;
using HidBridge.Contracts;

namespace HidBridge.Application;

/// <summary>
/// Dispatches a command batch through one session and returns aggregated acknowledgements.
/// </summary>
public sealed class DispatchCommandBatchUseCase
{
    private readonly ISessionOrchestrator _orchestrator;

    /// <summary>
    /// Creates one batch use case instance.
    /// </summary>
    public DispatchCommandBatchUseCase(ISessionOrchestrator orchestrator)
    {
        _orchestrator = orchestrator;
    }

    /// <summary>
    /// Dispatches all commands in input order and aggregates per-item and summary statuses.
    /// </summary>
    public async Task<SessionCommandBatchAckBody> ExecuteAsync(
        string sessionId,
        SessionCommandBatchDispatchBody request,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(request);

        var commands = request.Commands ?? [];
        var results = new List<SessionCommandBatchResultBody>(commands.Count);
        var applied = 0;
        var rejected = 0;
        var timeout = 0;

        foreach (var command in commands)
        {
            var normalized = new CommandRequestBody(
                CommandId: command.CommandId,
                SessionId: sessionId,
                Channel: command.Channel,
                Action: command.Action,
                Args: command.Args,
                TimeoutMs: command.TimeoutMs,
                IdempotencyKey: command.IdempotencyKey,
                TenantId: request.TenantId,
                OrganizationId: request.OrganizationId,
                OperatorRoles: request.OperatorRoles);

            var ack = await _orchestrator.DispatchAsync(normalized, cancellationToken);
            results.Add(new SessionCommandBatchResultBody(
                CommandId: ack.CommandId,
                Status: ack.Status,
                Error: ack.Error,
                Metrics: ack.Metrics));

            if (ack.Status == CommandStatus.Applied)
            {
                applied++;
            }
            else if (ack.Status == CommandStatus.Timeout)
            {
                timeout++;
            }
            else
            {
                rejected++;
            }
        }

        return new SessionCommandBatchAckBody(results, applied, rejected, timeout);
    }
}

using System.Net;
using HidBridge.Contracts;
using Microsoft.Extensions.Logging;

namespace HidBridge.EdgeProxy.Agent.Transport;

/// <summary>
/// Keeps existing relay-queue command polling semantics as default control transport engine.
/// </summary>
internal sealed class RelayCompatControlEngine : IEdgeControlTransportEngine
{
    private readonly ILogger _logger;

    /// <summary>
    /// Creates relay-compatible transport engine.
    /// </summary>
    public RelayCompatControlEngine(ILogger logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<int?> PollAndProcessCommandsAsync(
        EdgeControlTransportContext context,
        int? lastCommandSequence,
        CancellationToken cancellationToken)
    {
        var query = $"/api/v1/sessions/{Uri.EscapeDataString(context.EffectiveSessionId)}/transport/webrtc/commands?peerId={Uri.EscapeDataString(context.PeerId)}&limit={context.BatchLimit}";
        if (lastCommandSequence.HasValue)
        {
            query += $"&afterSequence={lastCommandSequence.Value}";
        }

        var envelopes = await context.QueryCommandsAsync(query, cancellationToken);
        var nextSequence = lastCommandSequence;
        foreach (var envelope in envelopes.OrderBy(static x => x.Sequence))
        {
            nextSequence = envelope.Sequence;
            if (envelope.Command is null || string.IsNullOrWhiteSpace(envelope.Command.CommandId))
            {
                continue;
            }

            var ack = await context.ExecuteCommandAsync(envelope.Command, cancellationToken);
            var ackStatus = await context.PublishAckAsync(envelope.Command.CommandId, ack, cancellationToken);
            if (ackStatus == HttpStatusCode.NotFound)
            {
                _logger.LogDebug(
                    "Relay ACK target was not found for command {CommandId}; it was likely expired or already acknowledged.",
                    envelope.Command.CommandId);
            }
        }

        return nextSequence;
    }
}

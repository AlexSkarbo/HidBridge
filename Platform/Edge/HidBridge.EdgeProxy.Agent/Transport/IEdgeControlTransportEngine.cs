using System.Net;
using HidBridge.Contracts;

namespace HidBridge.EdgeProxy.Agent.Transport;

/// <summary>
/// Defines pluggable edge control transport engine contract used by edge worker polling loop.
/// </summary>
internal interface IEdgeControlTransportEngine
{
    /// <summary>
    /// Polls pending control commands, executes them, and publishes ACK payloads.
    /// </summary>
    /// <param name="context">Runtime context delegated by edge worker.</param>
    /// <param name="lastCommandSequence">Last observed relay command sequence cursor.</param>
    /// <param name="cancellationToken">Cancels polling and execution.</param>
    /// <returns>Updated command sequence cursor after processing.</returns>
    Task<int?> PollAndProcessCommandsAsync(
        EdgeControlTransportContext context,
        int? lastCommandSequence,
        CancellationToken cancellationToken);
}

/// <summary>
/// Carries worker-provided callbacks and routing metadata for control transport engines.
/// </summary>
internal sealed record EdgeControlTransportContext(
    string EffectiveSessionId,
    string PeerId,
    int BatchLimit,
    Func<string, CancellationToken, Task<List<WebRtcCommandEnvelopeBody>>> QueryCommandsAsync,
    Func<string, CancellationToken, Task<List<WebRtcSignalMessageBody>>> QuerySignalsAsync,
    Func<CommandRequestBody, CancellationToken, Task<TransportAckMessageBody>> ExecuteCommandAsync,
    Func<string, TransportAckMessageBody, CancellationToken, Task<HttpStatusCode>> PublishAckAsync,
    Func<WebRtcSignalPublishBody, CancellationToken, Task<HttpStatusCode>> PublishSignalAsync);

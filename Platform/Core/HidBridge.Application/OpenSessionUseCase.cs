using HidBridge.Abstractions;
using HidBridge.Contracts;

namespace HidBridge.Application;

/// <summary>
/// Validates and records the intent to open a session.
/// </summary>
public sealed class OpenSessionUseCase
{
    private readonly IEventWriter _eventWriter;

    /// <summary>
    /// Creates the session opening use case.
    /// </summary>
    public OpenSessionUseCase(IEventWriter eventWriter)
    {
        _eventWriter = eventWriter;
    }

    /// <summary>
    /// Emits the audit trail for a session open request.
    /// </summary>
    /// <param name="request">The requested session payload.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The accepted session request.</returns>
    public async Task<SessionOpenBody> ExecuteAsync(SessionOpenBody request, CancellationToken cancellationToken)
    {
        await _eventWriter.WriteAuditAsync(
            new AuditEventBody("session", $"Session {request.SessionId} requested", request.SessionId, new Dictionary<string, object?>
            {
                ["profile"] = request.Profile.ToString(),
                ["requestedBy"] = request.RequestedBy,
                ["targetAgentId"] = request.TargetAgentId,
                ["targetEndpointId"] = request.TargetEndpointId,
                ["role"] = request.ShareMode.ToString(),
            }),
            cancellationToken);
        return request;
    }
}

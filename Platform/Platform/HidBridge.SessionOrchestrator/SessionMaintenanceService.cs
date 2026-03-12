using HidBridge.Abstractions;
using HidBridge.Contracts;

namespace HidBridge.SessionOrchestrator;

/// <summary>
/// Performs startup reconciliation and retention maintenance over persisted session and event stores.
/// </summary>
public sealed class SessionMaintenanceService
{
    private readonly ISessionStore _sessionStore;
    private readonly IEventStore _eventStore;
    private readonly IEventWriter _eventWriter;
    private readonly SessionMaintenanceOptions _options;

    /// <summary>
    /// Initializes the session maintenance service.
    /// </summary>
    /// <param name="sessionStore">The persisted session store.</param>
    /// <param name="eventStore">The persisted event store.</param>
    /// <param name="eventWriter">The event writer used for audit logging.</param>
    /// <param name="options">The maintenance configuration.</param>
    public SessionMaintenanceService(
        ISessionStore sessionStore,
        IEventStore eventStore,
        IEventWriter eventWriter,
        SessionMaintenanceOptions options)
    {
        _sessionStore = sessionStore;
        _eventStore = eventStore;
        _eventWriter = eventWriter;
        _options = options;
    }

    /// <summary>
    /// Reconciles persisted sessions and returns the number of state transitions that were applied.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The number of updated session snapshots.</returns>
    public async Task<int> ReconcileAsync(CancellationToken cancellationToken)
    {
        var nowUtc = DateTimeOffset.UtcNow;
        var sessions = await _sessionStore.ListAsync(cancellationToken);
        var updatedCount = 0;

        foreach (var snapshot in sessions)
        {
            var nextState = SessionLifecyclePolicy.EvaluateNextState(snapshot, nowUtc, _options);
            var controlLeaseExpired = snapshot.ControlLease is not null && snapshot.ControlLease.ExpiresAtUtc <= nowUtc;
            if (nextState == snapshot.State && !controlLeaseExpired)
            {
                continue;
            }

            var updated = snapshot with
            {
                State = nextState,
                UpdatedAtUtc = nowUtc,
                ControlLease = controlLeaseExpired ? null : snapshot.ControlLease,
            };
            await _sessionStore.UpsertAsync(updated, cancellationToken);
            if (nextState != snapshot.State)
            {
                await _eventWriter.WriteAuditAsync(
                    new AuditEventBody(
                        "session.reconcile",
                        $"Session {snapshot.SessionId} moved from {snapshot.State} to {nextState}",
                        snapshot.SessionId,
                        new Dictionary<string, object?>
                        {
                            ["previousState"] = snapshot.State.ToString(),
                            ["nextState"] = nextState.ToString(),
                            ["leaseExpiresAtUtc"] = snapshot.LeaseExpiresAtUtc,
                        }),
                    cancellationToken);
            }

            if (controlLeaseExpired && snapshot.ControlLease is not null)
            {
                await _eventWriter.WriteAuditAsync(
                    new AuditEventBody(
                        "session.control",
                        $"Control lease expired for participant {snapshot.ControlLease.ParticipantId}",
                        snapshot.SessionId,
                        new Dictionary<string, object?>
                        {
                            ["participantId"] = snapshot.ControlLease.ParticipantId,
                            ["principalId"] = snapshot.ControlLease.PrincipalId,
                            ["grantedBy"] = snapshot.ControlLease.GrantedBy,
                            ["expiresAtUtc"] = snapshot.ControlLease.ExpiresAtUtc,
                        }),
                    cancellationToken);
            }

            updatedCount++;
        }

        return updatedCount;
    }

    /// <summary>
    /// Applies audit and telemetry retention policies and returns the number of deleted events.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The number of deleted audit and telemetry events.</returns>
    public async Task<(int AuditDeleted, int TelemetryDeleted)> TrimRetentionAsync(CancellationToken cancellationToken)
    {
        var nowUtc = DateTimeOffset.UtcNow;
        var auditDeleted = await _eventStore.TrimAuditAsync(nowUtc - _options.AuditRetention, cancellationToken);
        var telemetryDeleted = await _eventStore.TrimTelemetryAsync(nowUtc - _options.TelemetryRetention, cancellationToken);
        return (auditDeleted, telemetryDeleted);
    }
}

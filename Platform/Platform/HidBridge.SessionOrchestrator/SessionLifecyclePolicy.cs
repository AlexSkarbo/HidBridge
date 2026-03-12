using HidBridge.Abstractions;
using HidBridge.Contracts;

namespace HidBridge.SessionOrchestrator;

/// <summary>
/// Encapsulates the deterministic lifecycle transitions used for lease reconciliation.
/// </summary>
public static class SessionLifecyclePolicy
{
    /// <summary>
    /// Computes the lease expiration timestamp from a heartbeat timestamp and lease duration.
    /// </summary>
    /// <param name="heartbeatAtUtc">The heartbeat time in UTC.</param>
    /// <param name="leaseDuration">The active lease duration.</param>
    /// <returns>The computed lease expiration timestamp.</returns>
    public static DateTimeOffset ComputeLeaseExpiration(DateTimeOffset heartbeatAtUtc, TimeSpan leaseDuration)
        => heartbeatAtUtc + leaseDuration;

    /// <summary>
    /// Evaluates the next lifecycle state for a persisted session snapshot.
    /// </summary>
    /// <param name="snapshot">The persisted session snapshot.</param>
    /// <param name="nowUtc">The current time in UTC.</param>
    /// <param name="options">The maintenance options controlling reconciliation.</param>
    /// <returns>The lifecycle state that should be applied after reconciliation.</returns>
    public static SessionState EvaluateNextState(SessionSnapshot snapshot, DateTimeOffset nowUtc, SessionMaintenanceOptions options)
    {
        if (snapshot.State is SessionState.Ended or SessionState.Failed or SessionState.Terminating)
        {
            return snapshot.State;
        }

        var leaseExpired = snapshot.LeaseExpiresAtUtc.HasValue && snapshot.LeaseExpiresAtUtc.Value <= nowUtc;
        if (!leaseExpired)
        {
            return snapshot.State;
        }

        if (snapshot.State is SessionState.Active or SessionState.Preparing or SessionState.Arming)
        {
            return SessionState.Recovering;
        }

        if (snapshot.State == SessionState.Recovering)
        {
            var recoveryDeadline = (snapshot.LeaseExpiresAtUtc ?? snapshot.UpdatedAtUtc) + options.RecoveryGracePeriod;
            return recoveryDeadline <= nowUtc ? SessionState.Failed : SessionState.Recovering;
        }

        return snapshot.State;
    }
}

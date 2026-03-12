using HidBridge.Abstractions;
using HidBridge.Contracts;
using HidBridge.SessionOrchestrator;
using Xunit;

namespace HidBridge.Platform.Tests;

/// <summary>
/// Verifies session lease reconciliation rules and state transitions.
/// </summary>
public sealed class SessionLifecyclePolicyTests
{
    /// <summary>
    /// Verifies that an active session with an expired lease transitions to recovering state.
    /// </summary>
    [Fact]
    public void EvaluateNextState_ExpiredActiveLease_MovesToRecovering()
    {
        var nowUtc = DateTimeOffset.UtcNow;
        var snapshot = new SessionSnapshot(
            "session-lease",
            "agent-1",
            "endpoint-1",
            SessionProfile.UltraLowLatency,
            "owner",
            SessionRole.Owner,
            SessionState.Active,
            nowUtc.AddSeconds(-45),
            LeaseExpiresAtUtc: nowUtc.AddSeconds(-1));

        var next = SessionLifecyclePolicy.EvaluateNextState(snapshot, nowUtc, new SessionMaintenanceOptions());

        Assert.Equal(SessionState.Recovering, next);
    }

    /// <summary>
    /// Verifies that a recovering session becomes failed after the recovery grace period elapses.
    /// </summary>
    [Fact]
    public void EvaluateNextState_RecoveringPastGrace_MovesToFailed()
    {
        var nowUtc = DateTimeOffset.UtcNow;
        var options = new SessionMaintenanceOptions
        {
            RecoveryGracePeriod = TimeSpan.FromSeconds(30),
        };
        var snapshot = new SessionSnapshot(
            "session-recovery",
            "agent-1",
            "endpoint-1",
            SessionProfile.Balanced,
            "owner",
            SessionRole.Owner,
            SessionState.Recovering,
            nowUtc.AddSeconds(-60),
            LeaseExpiresAtUtc: nowUtc.AddSeconds(-31));

        var next = SessionLifecyclePolicy.EvaluateNextState(snapshot, nowUtc, options);

        Assert.Equal(SessionState.Failed, next);
    }
}

using HidBridge.Contracts;
using HidBridge.Domain;
using HidBridge.Abstractions;

namespace HidBridge.SessionOrchestrator;

/// <summary>
/// Encapsulates moderator and controller authorization rules for collaborative sessions.
/// </summary>
internal static class SessionAuthorizationPolicy
{
    private static readonly string[] ViewerRoles = ["operator.viewer", "operator.moderator", "operator.admin"];
    private static readonly string[] ModeratorRoles = ["operator.moderator", "operator.admin"];

    /// <summary>
    /// Verifies that the actor has at least viewer-level operator access when explicit operator roles are present.
    /// </summary>
    internal static void EnsureViewerAccess(IReadOnlyList<string>? operatorRoles)
    {
        if (OperatorPolicyRoles.HasViewerAccess(operatorRoles))
        {
            return;
        }

        throw new UnauthorizedAccessException(
            $"Caller does not have an operator role that grants viewer access. Required roles: {string.Join(", ", ViewerRoles)}.");
    }

    /// <summary>
    /// Verifies that the actor belongs to the same tenant and organization scope as the session.
    /// Legacy unscoped sessions remain accessible until all callers are scope-aware.
    /// </summary>
    internal static void EnsureInSessionScope(
        SessionAggregate session,
        string? tenantId,
        string? organizationId,
        IReadOnlyList<string>? operatorRoles = null)
    {
        if (OperatorPolicyRoles.HasAdminAccess(operatorRoles))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(session.TenantId) && string.IsNullOrWhiteSpace(session.OrganizationId))
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(session.TenantId)
            && !string.Equals(session.TenantId, tenantId, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException($"Tenant {tenantId ?? "<missing>"} is not allowed to access session {session.SessionId}.");
        }

        if (!string.IsNullOrWhiteSpace(session.OrganizationId)
            && !string.Equals(session.OrganizationId, organizationId, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException($"Organization {organizationId ?? "<missing>"} is not allowed to access session {session.SessionId}.");
        }
    }

    /// <summary>
    /// Verifies that the actor is allowed to moderate the session.
    /// </summary>
    internal static void EnsureCanModerate(
        SessionAggregate session,
        string actedBy,
        IReadOnlyList<string>? operatorRoles,
        DateTimeOffset nowUtc)
    {
        if (OperatorPolicyRoles.HasModerationAccess(operatorRoles) || IsOwner(session, actedBy) || IsActiveController(session, actedBy, nowUtc))
        {
            return;
        }

        throw new UnauthorizedAccessException(
            $"Principal {actedBy} is not allowed to moderate session {session.SessionId}. Required roles: {string.Join(", ", ModeratorRoles)}.");
    }

    /// <summary>
    /// Verifies that the actor is allowed to remove the specified participant.
    /// </summary>
    internal static void EnsureCanRemoveParticipant(
        SessionAggregate session,
        string actedBy,
        SessionParticipantBody participant,
        IReadOnlyList<string>? operatorRoles,
        DateTimeOffset nowUtc)
    {
        if (OperatorPolicyRoles.HasModerationAccess(operatorRoles)
            || IsOwner(session, actedBy)
            || IsActiveController(session, actedBy, nowUtc)
            || string.Equals(participant.PrincipalId, actedBy, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        throw new UnauthorizedAccessException($"Principal {actedBy} is not allowed to remove participant {participant.ParticipantId}.");
    }

    /// <summary>
    /// Verifies that the actor is allowed to request invitations for the specified principal.
    /// </summary>
    internal static void EnsureCanRequestInvitation(
        SessionAggregate session,
        string actedBy,
        string principalId,
        IReadOnlyList<string>? operatorRoles,
        DateTimeOffset nowUtc)
    {
        if (OperatorPolicyRoles.HasModerationAccess(operatorRoles)
            || string.Equals(actedBy, principalId, StringComparison.OrdinalIgnoreCase)
            || IsOwner(session, actedBy)
            || IsActiveController(session, actedBy, nowUtc))
        {
            return;
        }

        throw new UnauthorizedAccessException($"Principal {actedBy} is not allowed to request invitations for {principalId}.");
    }

    /// <summary>
    /// Verifies that the actor is allowed to accept or reject the specified share.
    /// </summary>
    internal static void EnsureCanResolveShare(
        SessionAggregate session,
        string actedBy,
        SessionShareBody share,
        IReadOnlyList<string>? operatorRoles,
        DateTimeOffset nowUtc)
    {
        if (OperatorPolicyRoles.HasModerationAccess(operatorRoles)
            || string.Equals(share.PrincipalId, actedBy, StringComparison.OrdinalIgnoreCase)
            || IsOwner(session, actedBy)
            || IsActiveController(session, actedBy, nowUtc))
        {
            return;
        }

        throw new UnauthorizedAccessException($"Principal {actedBy} is not allowed to resolve share {share.ShareId}.");
    }

    /// <summary>
    /// Verifies that the actor is allowed to dispatch a command through the session.
    /// </summary>
    internal static void EnsureCanDispatch(
        SessionAggregate session,
        string principalId,
        string? participantId,
        IReadOnlyList<string>? operatorRoles,
        DateTimeOffset nowUtc)
    {
        EnsureViewerAccess(operatorRoles);

        if (session.ControlLease is null || session.ControlLease.ExpiresAtUtc <= nowUtc)
        {
            if (IsOwner(session, principalId))
            {
                return;
            }

            throw new UnauthorizedAccessException($"Principal {principalId} cannot dispatch commands without an active control lease.");
        }

        if (!string.Equals(session.ControlLease.PrincipalId, principalId, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException($"Principal {principalId} does not hold the active control lease for session {session.SessionId}.");
        }

        if (!string.IsNullOrWhiteSpace(participantId)
            && !string.Equals(session.ControlLease.ParticipantId, participantId, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException($"Participant {participantId} does not match the active control lease holder.");
        }
    }

    /// <summary>
    /// Verifies that the actor is allowed to request control for the target participant.
    /// </summary>
    internal static void EnsureCanRequestControl(
        SessionAggregate session,
        string requestedBy,
        SessionParticipantBody participant,
        IReadOnlyList<string>? operatorRoles)
    {
        EnsureViewerAccess(operatorRoles);

        if (OperatorPolicyRoles.HasModerationAccess(operatorRoles)
            || string.Equals(participant.PrincipalId, requestedBy, StringComparison.OrdinalIgnoreCase)
            || IsOwner(session, requestedBy))
        {
            return;
        }

        throw new UnauthorizedAccessException(
            $"Principal {requestedBy} cannot request control for participant {participant.ParticipantId}.");
    }

    /// <summary>
    /// Verifies that the actor may release the active control lease.
    /// </summary>
    internal static void EnsureCanReleaseControl(
        SessionAggregate session,
        string actedBy,
        SessionControlLeaseBody existingLease,
        IReadOnlyList<string>? operatorRoles)
    {
        if (OperatorPolicyRoles.HasModerationAccess(operatorRoles)
            || IsOwner(session, actedBy)
            || string.Equals(existingLease.PrincipalId, actedBy, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        throw new UnauthorizedAccessException($"Principal {actedBy} cannot release the current control lease.");
    }

    /// <summary>
    /// Verifies that the target participant is eligible to become the active controller.
    /// </summary>
    internal static void EnsureControlEligible(SessionParticipantBody participant, string sessionId)
    {
        if (participant.Role is SessionRole.Owner or SessionRole.Controller or SessionRole.Presenter)
        {
            return;
        }

        throw ControlArbitrationException.ParticipantNotEligible(sessionId, participant);
    }

    /// <summary>
    /// Returns <see langword="true"/> when the principal is the session owner.
    /// </summary>
    internal static bool IsOwner(SessionAggregate session, string principalId)
        => string.Equals(session.RequestedBy, principalId, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Returns <see langword="true"/> when the principal currently holds an unexpired control lease.
    /// </summary>
    internal static bool IsActiveController(SessionAggregate session, string principalId, DateTimeOffset nowUtc)
        => session.ControlLease is not null
            && session.ControlLease.ExpiresAtUtc > nowUtc
            && string.Equals(session.ControlLease.PrincipalId, principalId, StringComparison.OrdinalIgnoreCase);

}

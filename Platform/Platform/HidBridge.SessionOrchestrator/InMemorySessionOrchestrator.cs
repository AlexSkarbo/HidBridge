using System.Collections.Concurrent;
using HidBridge.Abstractions;
using HidBridge.Application;
using HidBridge.Contracts;
using HidBridge.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace HidBridge.SessionOrchestrator;

/// <summary>
/// Implements the current session orchestration baseline with in-memory routing state and persisted snapshots.
/// </summary>
public sealed class InMemorySessionOrchestrator : ISessionOrchestrator
{
    private readonly ConcurrentDictionary<string, SessionAggregate> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly OpenSessionUseCase _openSessionUseCase;
    private readonly DispatchCommandUseCase _dispatchCommandUseCase;
    private readonly IEventWriter _eventWriter;
    private readonly ICommandJournalStore _commandJournalStore;
    private readonly ISessionStore _sessionStore;
    private readonly SessionMaintenanceOptions _options;
    private readonly IRealtimeTransportFactory? _realtimeTransportFactory;
    private readonly ConcurrentDictionary<string, RealtimeTransportProvider> _sessionTransportProviders = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Creates the in-memory session orchestrator with a transient in-memory session store.
    /// </summary>
    public InMemorySessionOrchestrator(OpenSessionUseCase openSessionUseCase, DispatchCommandUseCase dispatchCommandUseCase, IEventWriter eventWriter)
        : this(openSessionUseCase, dispatchCommandUseCase, eventWriter, new TransientCommandJournalStore(), new TransientSessionStore(), new SessionMaintenanceOptions())
    {
    }

    /// <summary>
    /// Creates the session orchestrator.
    /// </summary>
    public InMemorySessionOrchestrator(
        OpenSessionUseCase openSessionUseCase,
        DispatchCommandUseCase dispatchCommandUseCase,
        IEventWriter eventWriter,
        ICommandJournalStore commandJournalStore,
        ISessionStore sessionStore,
        SessionMaintenanceOptions? options = null,
        IRealtimeTransportFactory? realtimeTransportFactory = null)
    {
        _openSessionUseCase = openSessionUseCase;
        _dispatchCommandUseCase = dispatchCommandUseCase;
        _eventWriter = eventWriter;
        _commandJournalStore = commandJournalStore;
        _sessionStore = sessionStore;
        _options = options ?? new SessionMaintenanceOptions();
        _realtimeTransportFactory = realtimeTransportFactory;
    }

    /// <summary>
    /// Opens and activates a session bound to a target agent.
    /// </summary>
    public async Task<SessionOpenBody> OpenAsync(SessionOpenBody request, CancellationToken cancellationToken)
    {
        SessionAuthorizationPolicy.EnsureViewerAccess(request.OperatorRoles);
        var accepted = await _openSessionUseCase.ExecuteAsync(request, cancellationToken);
        var nowUtc = DateTimeOffset.UtcNow;
        var aggregate = new SessionAggregate(
            accepted.SessionId,
            accepted.TargetAgentId,
            accepted.TargetEndpointId ?? string.Empty,
            accepted.Profile,
            accepted.RequestedBy,
            accepted.ShareMode,
            participants:
            [
                new SessionParticipantBody(
                    ParticipantId: $"owner:{accepted.RequestedBy}",
                    PrincipalId: accepted.RequestedBy,
                    Role: accepted.ShareMode,
                    JoinedAtUtc: nowUtc,
                    UpdatedAtUtc: nowUtc)
            ],
            lastHeartbeatAtUtc: nowUtc,
            leaseExpiresAtUtc: SessionLifecyclePolicy.ComputeLeaseExpiration(nowUtc, _options.LeaseDuration),
            tenantId: accepted.TenantId,
            organizationId: accepted.OrganizationId);
        aggregate.MoveTo(SessionState.Active);
        _sessions[accepted.SessionId] = aggregate;
        TryAssignSessionTransportProvider(accepted.SessionId, accepted.TargetEndpointId);
        await _sessionStore.UpsertAsync(ToSnapshot(aggregate, nowUtc), cancellationToken);
        return accepted;
    }

    /// <summary>
    /// Closes an active session and emits an audit event.
    /// </summary>
    public async Task<SessionCloseBody> CloseAsync(SessionCloseBody request, CancellationToken cancellationToken)
    {
        _sessions.TryRemove(request.SessionId, out _);
        _sessionTransportProviders.TryRemove(request.SessionId, out _);
        await _sessionStore.RemoveAsync(request.SessionId, cancellationToken);
        await _eventWriter.WriteAuditAsync(new AuditEventBody("session", $"Session {request.SessionId} closed", request.SessionId, new Dictionary<string, object?>
        {
            ["reason"] = request.Reason,
        }), cancellationToken);
        return request;
    }

    /// <summary>
    /// Resolves the target agent for the session and dispatches the command.
    /// </summary>
    public async Task<CommandAckBody> DispatchAsync(CommandRequestBody request, CancellationToken cancellationToken)
    {
        var session = await ResolveSessionAsync(request.SessionId, cancellationToken);
        RealtimeTransportProvider? sessionTransportProvider = null;
        if (session is not null)
        {
            SessionAuthorizationPolicy.EnsureInSessionScope(session, request.TenantId, request.OrganizationId, request.OperatorRoles);
            if (_sessionTransportProviders.TryGetValue(session.SessionId, out var resolvedSessionProvider))
            {
                sessionTransportProvider = resolvedSessionProvider;
            }
            else
            {
                sessionTransportProvider = TryAssignSessionTransportProvider(session.SessionId, session.EndpointId);
            }
        }

        var principalId = request.Args.TryGetValue("principalId", out var principalValue)
            ? principalValue?.ToString()
            : session?.RequestedBy;
        var participantId = request.Args.TryGetValue("participantId", out var participantValue)
            ? participantValue?.ToString()
            : (string.Equals(principalId, session?.RequestedBy, StringComparison.OrdinalIgnoreCase) ? $"owner:{session?.RequestedBy}" : null);

        var existingEntry = await _commandJournalStore.FindByCommandIdAsync(request.CommandId, cancellationToken);
        if (existingEntry is null && !string.IsNullOrWhiteSpace(request.IdempotencyKey))
        {
            existingEntry = await _commandJournalStore.FindByIdempotencyKeyAsync(request.SessionId, request.IdempotencyKey, cancellationToken);
        }

        if (existingEntry is not null)
        {
            return new CommandAckBody(existingEntry.CommandId, existingEntry.Status, existingEntry.Error, existingEntry.Metrics);
        }

        // Session-target binding is the default routing path. args.agentId is still accepted
        // as an override for ad-hoc testing until the command contract is fully locked down.
        var agentId = request.Args.TryGetValue("agentId", out var value)
            ? value?.ToString()
            : session?.AgentId;

        CommandAckBody ack;
        if (string.IsNullOrWhiteSpace(agentId))
        {
            ack = new CommandAckBody(
                request.CommandId,
                CommandStatus.Rejected,
                new ErrorInfo(ErrorDomain.Command, "E_COMMAND_INVALID_PAYLOAD", "Missing args.agentId", false));
        }
        else
        {
            try
            {
                if (session is not null && !string.IsNullOrWhiteSpace(principalId))
                {
                    SessionAuthorizationPolicy.EnsureCanDispatch(session, principalId, participantId, request.OperatorRoles, DateTimeOffset.UtcNow);
                }

                ack = await _dispatchCommandUseCase.ExecuteAsync(
                    agentId,
                    request,
                    session?.EndpointId,
                    sessionTransportProvider,
                    cancellationToken);
            }
            catch (UnauthorizedAccessException ex)
            {
                ack = new CommandAckBody(
                    request.CommandId,
                    CommandStatus.Rejected,
                    new ErrorInfo(ErrorDomain.Session, "E_SESSION_CONTROL_REQUIRED", ex.Message, false));
            }
        }

        var nowUtc = DateTimeOffset.UtcNow;
        await _commandJournalStore.AppendAsync(
            new CommandJournalEntryBody(
                request.CommandId,
                request.SessionId,
                agentId ?? string.Empty,
                request.Channel,
                request.Action,
                request.Args,
                request.TimeoutMs,
                request.IdempotencyKey,
                ack.Status,
                nowUtc,
                nowUtc,
                ack.Error,
                ack.Metrics,
                participantId,
                principalId,
                request.Args.TryGetValue("shareId", out var shareId) ? shareId?.ToString() : null),
            cancellationToken);

        if (session is not null)
        {
            var shouldRefreshLease = ShouldRefreshSessionLeaseFromDispatch(ack);
            var shouldMoveToRecovering = ShouldMoveSessionToRecoveringFromDispatch(ack)
                && session.State is SessionState.Active or SessionState.Preparing or SessionState.Arming;
            var shouldMoveToActive = ack.Status == CommandStatus.Applied
                && session.State is SessionState.Recovering or SessionState.Failed;

            if (shouldRefreshLease || shouldMoveToRecovering || shouldMoveToActive)
            {
                var previousState = session.State;
                if (shouldRefreshLease)
                {
                    session.SetLease(nowUtc, SessionLifecyclePolicy.ComputeLeaseExpiration(nowUtc, _options.LeaseDuration));
                }

                if (shouldMoveToRecovering)
                {
                    session.MoveTo(SessionState.Recovering);
                }

                if (shouldMoveToActive)
                {
                    session.MoveTo(SessionState.Active);
                }

                await PersistSessionAsync(session, nowUtc, cancellationToken);
                if (session.State != previousState)
                {
                    var reason = shouldMoveToActive
                        ? "command_path_recovered"
                        : "command_path_unstable";
                    await _eventWriter.WriteAuditAsync(
                        new AuditEventBody(
                            "session.state",
                            $"Session {session.SessionId} moved from {previousState} to {session.State}",
                            session.SessionId,
                            new Dictionary<string, object?>
                            {
                                ["previousState"] = previousState.ToString(),
                                ["nextState"] = session.State.ToString(),
                                ["reason"] = reason,
                                ["commandId"] = request.CommandId,
                                ["commandAction"] = request.Action,
                                ["commandStatus"] = ack.Status.ToString(),
                                ["errorCode"] = ack.Error?.Code,
                            }),
                        cancellationToken);
                }
            }
        }

        return ack;
    }

    /// <summary>
    /// Lists the current participant set for the specified session.
    /// </summary>
    /// <param name="sessionId">The session identifier.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The persisted participant set.</returns>
    public async Task<IReadOnlyList<SessionParticipantBody>> ListParticipantsAsync(string sessionId, CancellationToken cancellationToken)
    {
        var aggregate = await RequireSessionAsync(sessionId, cancellationToken);
        return aggregate.Participants
            .OrderBy(x => x.JoinedAtUtc)
            .ThenBy(x => x.ParticipantId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// Creates or updates one participant in the target session.
    /// </summary>
    /// <param name="sessionId">The session identifier.</param>
    /// <param name="request">The participant mutation payload.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The stored participant snapshot.</returns>
    public async Task<SessionParticipantBody> UpsertParticipantAsync(string sessionId, SessionParticipantUpsertBody request, CancellationToken cancellationToken)
    {
        var aggregate = await RequireSessionAsync(sessionId, cancellationToken);
        var nowUtc = DateTimeOffset.UtcNow;
        SessionAuthorizationPolicy.EnsureInSessionScope(aggregate, request.TenantId, request.OrganizationId, request.OperatorRoles);
        SessionAuthorizationPolicy.EnsureCanModerate(aggregate, request.AddedBy, request.OperatorRoles, nowUtc);
        var existing = aggregate.Participants.FirstOrDefault(x => string.Equals(x.ParticipantId, request.ParticipantId, StringComparison.OrdinalIgnoreCase));
        var participant = new SessionParticipantBody(
            request.ParticipantId,
            request.PrincipalId,
            request.Role,
            existing?.JoinedAtUtc ?? nowUtc,
            nowUtc);

        aggregate.UpsertParticipant(participant);
        aggregate.SetLease(nowUtc, SessionLifecyclePolicy.ComputeLeaseExpiration(nowUtc, _options.LeaseDuration));
        await PersistSessionAsync(aggregate, nowUtc, cancellationToken);
        await _eventWriter.WriteAuditAsync(
            new AuditEventBody(
                "session.participant",
                $"Participant {request.ParticipantId} upserted",
                sessionId,
                new Dictionary<string, object?>
                {
                    ["participantId"] = request.ParticipantId,
                    ["principalId"] = request.PrincipalId,
                    ["role"] = request.Role.ToString(),
                    ["addedBy"] = request.AddedBy,
                }),
            cancellationToken);

        return participant;
    }

    /// <summary>
    /// Removes one participant from the target session.
    /// </summary>
    /// <param name="sessionId">The session identifier.</param>
    /// <param name="request">The participant removal payload.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The normalized removal payload.</returns>
    public async Task<SessionParticipantRemoveBody> RemoveParticipantAsync(string sessionId, SessionParticipantRemoveBody request, CancellationToken cancellationToken)
    {
        var aggregate = await RequireSessionAsync(sessionId, cancellationToken);
        var nowUtc = DateTimeOffset.UtcNow;
        SessionAuthorizationPolicy.EnsureInSessionScope(aggregate, request.TenantId, request.OrganizationId, request.OperatorRoles);
        var participant = aggregate.Participants.FirstOrDefault(x => string.Equals(x.ParticipantId, request.ParticipantId, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException($"Participant {request.ParticipantId} was not found in session {sessionId}.");
        SessionAuthorizationPolicy.EnsureCanRemoveParticipant(aggregate, request.RemovedBy, participant, request.OperatorRoles, nowUtc);

        if (string.Equals(participant.PrincipalId, aggregate.RequestedBy, StringComparison.OrdinalIgnoreCase)
            && participant.Role == aggregate.Role)
        {
            throw new InvalidOperationException("The owner participant cannot be removed directly. Close the session instead.");
        }

        aggregate.RemoveParticipant(request.ParticipantId);
        if (string.Equals(aggregate.ControlLease?.ParticipantId, request.ParticipantId, StringComparison.OrdinalIgnoreCase))
        {
            aggregate.ReplaceControlLease(null);
        }

        aggregate.SetLease(nowUtc, SessionLifecyclePolicy.ComputeLeaseExpiration(nowUtc, _options.LeaseDuration));
        await PersistSessionAsync(aggregate, nowUtc, cancellationToken);
        await _eventWriter.WriteAuditAsync(
            new AuditEventBody(
                "session.participant",
                $"Participant {request.ParticipantId} removed",
                sessionId,
                new Dictionary<string, object?>
                {
                    ["participantId"] = request.ParticipantId,
                    ["removedBy"] = request.RemovedBy,
                    ["reason"] = request.Reason,
                }),
            cancellationToken);

        return request;
    }

    /// <summary>
    /// Lists the share grants currently tracked for the target session.
    /// </summary>
    /// <param name="sessionId">The session identifier.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The share grant set in storage order.</returns>
    public async Task<IReadOnlyList<SessionShareBody>> ListSharesAsync(string sessionId, CancellationToken cancellationToken)
    {
        var aggregate = await RequireSessionAsync(sessionId, cancellationToken);
        return aggregate.Shares
            .OrderByDescending(x => x.CreatedAtUtc)
            .ThenBy(x => x.ShareId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// Grants a new collaborative share for the target session.
    /// </summary>
    /// <param name="sessionId">The session identifier.</param>
    /// <param name="request">The share grant payload.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The stored share grant snapshot.</returns>
    public Task<SessionShareBody> GrantShareAsync(string sessionId, SessionShareGrantBody request, CancellationToken cancellationToken)
        => UpsertShareAsync(
            sessionId,
            request.ShareId,
            request.PrincipalId,
            request.GrantedBy,
            request.Role,
            SessionShareStatus.Pending,
            "session.share",
            $"Share {request.ShareId} granted",
            new Dictionary<string, object?>
            {
                ["shareId"] = request.ShareId,
                ["principalId"] = request.PrincipalId,
                ["grantedBy"] = request.GrantedBy,
                ["role"] = request.Role.ToString(),
                ["status"] = SessionShareStatus.Pending.ToString(),
            },
            request.GrantedBy,
            request.TenantId,
            request.OrganizationId,
            request.OperatorRoles,
            cancellationToken);

    /// <summary>
    /// Records a new invitation request for later approval.
    /// </summary>
    /// <param name="sessionId">The session identifier.</param>
    /// <param name="request">The invitation request payload.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The stored invitation snapshot in Requested state.</returns>
    public Task<SessionShareBody> RequestInvitationAsync(string sessionId, SessionInvitationRequestBody request, CancellationToken cancellationToken)
        => UpsertShareAsync(
            sessionId,
            request.ShareId,
            request.PrincipalId,
            request.RequestedBy,
            request.RequestedRole,
            SessionShareStatus.Requested,
            "session.invitation",
            $"Invitation request {request.ShareId} created",
            new Dictionary<string, object?>
            {
                ["shareId"] = request.ShareId,
                ["principalId"] = request.PrincipalId,
                ["requestedBy"] = request.RequestedBy,
                ["role"] = request.RequestedRole.ToString(),
                ["message"] = request.Message,
                ["status"] = SessionShareStatus.Requested.ToString(),
            },
            request.RequestedBy,
            request.TenantId,
            request.OrganizationId,
            request.OperatorRoles,
            cancellationToken);

    /// <summary>
    /// Approves a requested invitation and converts it into a pending share invite.
    /// </summary>
    /// <param name="sessionId">The session identifier.</param>
    /// <param name="request">The invitation decision payload.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The updated invitation snapshot in Pending state.</returns>
    public async Task<SessionShareBody> ApproveInvitationAsync(string sessionId, SessionInvitationDecisionBody request, CancellationToken cancellationToken)
    {
        var aggregate = await RequireSessionAsync(sessionId, cancellationToken);
        var existing = aggregate.FindShare(request.ShareId)
            ?? throw new KeyNotFoundException($"Share {request.ShareId} was not found in session {sessionId}.");
        var nowUtc = DateTimeOffset.UtcNow;
        SessionAuthorizationPolicy.EnsureInSessionScope(aggregate, request.TenantId, request.OrganizationId, request.OperatorRoles);
        SessionAuthorizationPolicy.EnsureCanModerate(aggregate, request.ActedBy, request.OperatorRoles, nowUtc);

        if (existing.Status != SessionShareStatus.Requested)
        {
            throw new InvalidOperationException($"Only requested invitations can be approved. Current status: {existing.Status}.");
        }

        var approved = existing with
        {
            GrantedBy = request.ActedBy,
            Role = request.GrantedRole ?? existing.Role,
            Status = SessionShareStatus.Pending,
            UpdatedAtUtc = nowUtc,
        };

        aggregate.UpsertShare(approved);
        aggregate.SetLease(nowUtc, SessionLifecyclePolicy.ComputeLeaseExpiration(nowUtc, _options.LeaseDuration));
        await PersistSessionAsync(aggregate, nowUtc, cancellationToken);
        await _eventWriter.WriteAuditAsync(
            new AuditEventBody(
                "session.invitation",
                $"Invitation request {request.ShareId} approved",
                sessionId,
                new Dictionary<string, object?>
                {
                    ["shareId"] = request.ShareId,
                    ["principalId"] = approved.PrincipalId,
                    ["actedBy"] = request.ActedBy,
                    ["grantedRole"] = approved.Role.ToString(),
                    ["reason"] = request.Reason,
                    ["status"] = SessionShareStatus.Pending.ToString(),
                }),
            cancellationToken);

        return approved;
    }

    /// <summary>
    /// Declines a requested invitation without creating a pending invite.
    /// </summary>
    /// <param name="sessionId">The session identifier.</param>
    /// <param name="request">The invitation decision payload.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The updated invitation snapshot in Rejected state.</returns>
    public async Task<SessionShareBody> DeclineInvitationAsync(string sessionId, SessionInvitationDecisionBody request, CancellationToken cancellationToken)
    {
        var aggregate = await RequireSessionAsync(sessionId, cancellationToken);
        var existing = aggregate.FindShare(request.ShareId)
            ?? throw new KeyNotFoundException($"Share {request.ShareId} was not found in session {sessionId}.");
        var nowUtc = DateTimeOffset.UtcNow;
        SessionAuthorizationPolicy.EnsureInSessionScope(aggregate, request.TenantId, request.OrganizationId, request.OperatorRoles);
        SessionAuthorizationPolicy.EnsureCanModerate(aggregate, request.ActedBy, request.OperatorRoles, nowUtc);

        if (existing.Status != SessionShareStatus.Requested)
        {
            throw new InvalidOperationException($"Only requested invitations can be declined. Current status: {existing.Status}.");
        }

        var declined = existing with
        {
            Status = SessionShareStatus.Rejected,
            UpdatedAtUtc = nowUtc,
        };

        aggregate.UpsertShare(declined);
        aggregate.SetLease(nowUtc, SessionLifecyclePolicy.ComputeLeaseExpiration(nowUtc, _options.LeaseDuration));
        await PersistSessionAsync(aggregate, nowUtc, cancellationToken);
        await _eventWriter.WriteAuditAsync(
            new AuditEventBody(
                "session.invitation",
                $"Invitation request {request.ShareId} declined",
                sessionId,
                new Dictionary<string, object?>
                {
                    ["shareId"] = request.ShareId,
                    ["principalId"] = declined.PrincipalId,
                    ["actedBy"] = request.ActedBy,
                    ["reason"] = request.Reason,
                    ["status"] = SessionShareStatus.Rejected.ToString(),
                }),
            cancellationToken);

        return declined;
    }

    /// <summary>
    /// Accepts a pending share and materializes the grantee as a participant.
    /// </summary>
    /// <param name="sessionId">The session identifier.</param>
    /// <param name="request">The share transition payload.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The updated share snapshot.</returns>
    public Task<SessionShareBody> AcceptShareAsync(string sessionId, SessionShareTransitionBody request, CancellationToken cancellationToken)
        => TransitionShareAsync(sessionId, request, SessionShareStatus.Accepted, materializeParticipant: true, removeParticipant: false, cancellationToken);

    /// <summary>
    /// Rejects a pending share grant.
    /// </summary>
    /// <param name="sessionId">The session identifier.</param>
    /// <param name="request">The share transition payload.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The updated share snapshot.</returns>
    public Task<SessionShareBody> RejectShareAsync(string sessionId, SessionShareTransitionBody request, CancellationToken cancellationToken)
        => TransitionShareAsync(sessionId, request, SessionShareStatus.Rejected, materializeParticipant: false, removeParticipant: false, cancellationToken);

    /// <summary>
    /// Revokes an existing share grant and removes the derived participant when present.
    /// </summary>
    /// <param name="sessionId">The session identifier.</param>
    /// <param name="request">The share transition payload.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The updated share snapshot.</returns>
    public Task<SessionShareBody> RevokeShareAsync(string sessionId, SessionShareTransitionBody request, CancellationToken cancellationToken)
        => TransitionShareAsync(sessionId, request, SessionShareStatus.Revoked, materializeParticipant: false, removeParticipant: true, cancellationToken);

    /// <summary>
    /// Returns the currently active control lease for the specified session.
    /// </summary>
    /// <param name="sessionId">The session identifier.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The current control lease snapshot when active; otherwise <see langword="null"/>.</returns>
    public async Task<SessionControlLeaseBody?> GetControlLeaseAsync(string sessionId, CancellationToken cancellationToken)
    {
        var aggregate = await RequireSessionAsync(sessionId, cancellationToken);
        return await NormalizeControlLeaseAsync(aggregate, DateTimeOffset.UtcNow, cancellationToken);
    }

    /// <summary>
    /// Requests control for one participant and grants it when no competing controller is active.
    /// </summary>
    /// <param name="sessionId">The session identifier.</param>
    /// <param name="request">The control request payload.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The active control lease after the request is processed.</returns>
    public async Task<SessionControlLeaseBody> RequestControlAsync(string sessionId, SessionControlRequestBody request, CancellationToken cancellationToken)
    {
        var aggregate = await RequireSessionAsync(sessionId, cancellationToken);
        var nowUtc = DateTimeOffset.UtcNow;
        SessionAuthorizationPolicy.EnsureInSessionScope(aggregate, request.TenantId, request.OrganizationId, request.OperatorRoles);
        var participant = RequireParticipant(aggregate, request.ParticipantId, sessionId);
        SessionAuthorizationPolicy.EnsureControlEligible(participant, sessionId);
        SessionAuthorizationPolicy.EnsureCanRequestControl(aggregate, request.RequestedBy, participant, request.OperatorRoles);

        var existingLease = await NormalizeControlLeaseAsync(aggregate, nowUtc, cancellationToken);
        if (existingLease is not null
            && !string.Equals(existingLease.PrincipalId, participant.PrincipalId, StringComparison.OrdinalIgnoreCase))
        {
            throw ControlArbitrationException.LeaseHeldByOther(
                sessionId,
                existingLease,
                participant.ParticipantId,
                request.RequestedBy);
        }

        var lease = BuildControlLease(participant, request.RequestedBy, nowUtc, request.LeaseSeconds);
        aggregate.ReplaceControlLease(lease);
        aggregate.SetLease(nowUtc, SessionLifecyclePolicy.ComputeLeaseExpiration(nowUtc, _options.LeaseDuration));
        await PersistSessionAsync(aggregate, nowUtc, cancellationToken);
        await _eventWriter.WriteAuditAsync(
            new AuditEventBody(
                "session.control",
                $"Control requested by {request.RequestedBy} for participant {participant.ParticipantId}",
                sessionId,
                new Dictionary<string, object?>
                {
                    ["participantId"] = participant.ParticipantId,
                    ["principalId"] = participant.PrincipalId,
                    ["requestedBy"] = request.RequestedBy,
                    ["reason"] = request.Reason,
                    ["expiresAtUtc"] = lease.ExpiresAtUtc,
                }),
            cancellationToken);

        return lease;
    }

    /// <summary>
    /// Grants control to one participant under moderator policy rules.
    /// </summary>
    /// <param name="sessionId">The session identifier.</param>
    /// <param name="request">The grant payload.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The active control lease after the grant.</returns>
    public Task<SessionControlLeaseBody> GrantControlAsync(string sessionId, SessionControlGrantBody request, CancellationToken cancellationToken)
        => ApplyControlLeaseAsync(sessionId, request, forceTakeover: false, cancellationToken);

    /// <summary>
    /// Releases the current control lease.
    /// </summary>
    /// <param name="sessionId">The session identifier.</param>
    /// <param name="request">The release payload.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The lease snapshot that was active before release, or <see langword="null"/>.</returns>
    public async Task<SessionControlLeaseBody?> ReleaseControlAsync(string sessionId, SessionControlReleaseBody request, CancellationToken cancellationToken)
    {
        var aggregate = await RequireSessionAsync(sessionId, cancellationToken);
        var nowUtc = DateTimeOffset.UtcNow;
        var existingLease = await NormalizeControlLeaseAsync(aggregate, nowUtc, cancellationToken);
        if (existingLease is null)
        {
            return null;
        }

        SessionAuthorizationPolicy.EnsureInSessionScope(aggregate, request.TenantId, request.OrganizationId, request.OperatorRoles);
        SessionAuthorizationPolicy.EnsureCanReleaseControl(aggregate, request.ActedBy, existingLease, request.OperatorRoles);

        if (!string.IsNullOrWhiteSpace(request.ParticipantId)
            && !string.Equals(existingLease.ParticipantId, request.ParticipantId, StringComparison.OrdinalIgnoreCase))
        {
            throw ControlArbitrationException.ParticipantMismatch(
                sessionId,
                existingLease,
                request.ParticipantId,
                request.ActedBy);
        }

        aggregate.ReplaceControlLease(null);
        aggregate.SetLease(nowUtc, SessionLifecyclePolicy.ComputeLeaseExpiration(nowUtc, _options.LeaseDuration));
        await PersistSessionAsync(aggregate, nowUtc, cancellationToken);
        await _eventWriter.WriteAuditAsync(
            new AuditEventBody(
                "session.control",
                $"Control released by {request.ActedBy}",
                sessionId,
                new Dictionary<string, object?>
                {
                    ["participantId"] = existingLease.ParticipantId,
                    ["principalId"] = existingLease.PrincipalId,
                    ["actedBy"] = request.ActedBy,
                    ["reason"] = request.Reason,
                }),
            cancellationToken);

        return existingLease;
    }

    /// <summary>
    /// Force-transfers control to one participant regardless of the currently active controller.
    /// </summary>
    /// <param name="sessionId">The session identifier.</param>
    /// <param name="request">The force-takeover payload.</param>
    /// <param name="cancellationToken">Cancels the operation.</param>
    /// <returns>The active control lease after takeover.</returns>
    public Task<SessionControlLeaseBody> ForceTakeoverControlAsync(string sessionId, SessionControlGrantBody request, CancellationToken cancellationToken)
        => ApplyControlLeaseAsync(sessionId, request, forceTakeover: true, cancellationToken);

    /// <summary>
    /// Returns the persisted session snapshots for diagnostics and API inspection.
    /// </summary>
    public Task<IReadOnlyList<SessionSnapshot>> SnapshotAsync(CancellationToken cancellationToken)
        => _sessionStore.ListAsync(cancellationToken);

    private async Task<SessionShareBody> UpsertShareAsync(
        string sessionId,
        string shareId,
        string principalId,
        string grantedBy,
        SessionRole role,
        SessionShareStatus status,
        string auditCategory,
        string auditMessage,
        IReadOnlyDictionary<string, object?> auditData,
        string actedBy,
        string? tenantId,
        string? organizationId,
        IReadOnlyList<string>? operatorRoles,
        CancellationToken cancellationToken)
    {
        var aggregate = await RequireSessionAsync(sessionId, cancellationToken);
        var nowUtc = DateTimeOffset.UtcNow;
        SessionAuthorizationPolicy.EnsureInSessionScope(aggregate, tenantId, organizationId, operatorRoles);
        if (status == SessionShareStatus.Requested)
        {
            SessionAuthorizationPolicy.EnsureCanRequestInvitation(aggregate, actedBy, principalId, operatorRoles, nowUtc);
        }
        else
        {
            SessionAuthorizationPolicy.EnsureCanModerate(aggregate, actedBy, operatorRoles, nowUtc);
        }

        var existing = aggregate.FindShare(shareId);
        var share = new SessionShareBody(
            shareId,
            principalId,
            grantedBy,
            role,
            status,
            existing?.CreatedAtUtc ?? nowUtc,
            nowUtc);

        aggregate.UpsertShare(share);
        aggregate.SetLease(nowUtc, SessionLifecyclePolicy.ComputeLeaseExpiration(nowUtc, _options.LeaseDuration));
        await PersistSessionAsync(aggregate, nowUtc, cancellationToken);
        await _eventWriter.WriteAuditAsync(
            new AuditEventBody(
                auditCategory,
                auditMessage,
                sessionId,
                new Dictionary<string, object?>(auditData)),
            cancellationToken);

        return share;
    }

    private async Task<SessionAggregate> RequireSessionAsync(string sessionId, CancellationToken cancellationToken)
        => await ResolveSessionAsync(sessionId, cancellationToken)
            ?? throw new KeyNotFoundException($"Session {sessionId} was not found.");

    private async Task PersistSessionAsync(SessionAggregate aggregate, DateTimeOffset nowUtc, CancellationToken cancellationToken)
        => await _sessionStore.UpsertAsync(ToSnapshot(aggregate, nowUtc), cancellationToken);

    private static bool ShouldRefreshSessionLeaseFromDispatch(CommandAckBody ack)
    {
        if (ack.Status is CommandStatus.Applied or CommandStatus.Accepted or CommandStatus.Timeout)
        {
            return true;
        }

        if (ack.Status != CommandStatus.Rejected)
        {
            return false;
        }

        if (ack.Error is null)
        {
            return true;
        }

        return ack.Error.Domain switch
        {
            ErrorDomain.Auth => false,
            ErrorDomain.Session => false,
            _ => !string.Equals(ack.Error.Code, "E_COMMAND_INVALID_PAYLOAD", StringComparison.OrdinalIgnoreCase),
        };
    }

    private static bool ShouldMoveSessionToRecoveringFromDispatch(CommandAckBody ack)
    {
        if (ack.Status is CommandStatus.Applied or CommandStatus.Accepted)
        {
            return false;
        }

        return ShouldRefreshSessionLeaseFromDispatch(ack);
    }

    private async Task<SessionShareBody> TransitionShareAsync(
        string sessionId,
        SessionShareTransitionBody request,
        SessionShareStatus nextStatus,
        bool materializeParticipant,
        bool removeParticipant,
        CancellationToken cancellationToken)
    {
        var aggregate = await RequireSessionAsync(sessionId, cancellationToken);
        var existing = aggregate.FindShare(request.ShareId)
            ?? throw new KeyNotFoundException($"Share {request.ShareId} was not found in session {sessionId}.");
        var nowUtc = DateTimeOffset.UtcNow;
        SessionAuthorizationPolicy.EnsureInSessionScope(aggregate, request.TenantId, request.OrganizationId, request.OperatorRoles);
        if (nextStatus == SessionShareStatus.Revoked)
        {
            SessionAuthorizationPolicy.EnsureCanModerate(aggregate, request.ActedBy, request.OperatorRoles, nowUtc);
        }
        else
        {
            SessionAuthorizationPolicy.EnsureCanResolveShare(aggregate, request.ActedBy, existing, request.OperatorRoles, nowUtc);
        }

        ValidateShareTransition(existing, nextStatus);

        var updated = existing with
        {
            Status = nextStatus,
            UpdatedAtUtc = nowUtc,
        };

        aggregate.UpsertShare(updated);
        if (materializeParticipant)
        {
            aggregate.UpsertParticipant(new SessionParticipantBody(
                ParticipantId: $"share:{updated.ShareId}",
                PrincipalId: updated.PrincipalId,
                Role: updated.Role,
                JoinedAtUtc: nowUtc,
                UpdatedAtUtc: nowUtc));
        }

        if (removeParticipant)
        {
            aggregate.RemoveParticipant($"share:{updated.ShareId}");
            if (string.Equals(aggregate.ControlLease?.ParticipantId, $"share:{updated.ShareId}", StringComparison.OrdinalIgnoreCase))
            {
                aggregate.ReplaceControlLease(null);
            }
        }

        aggregate.SetLease(nowUtc, SessionLifecyclePolicy.ComputeLeaseExpiration(nowUtc, _options.LeaseDuration));
        await PersistSessionAsync(aggregate, nowUtc, cancellationToken);
        await _eventWriter.WriteAuditAsync(
            new AuditEventBody(
                "session.share",
                $"Share {request.ShareId} transitioned to {nextStatus}",
                sessionId,
                new Dictionary<string, object?>
                {
                    ["shareId"] = request.ShareId,
                    ["principalId"] = updated.PrincipalId,
                    ["actedBy"] = request.ActedBy,
                    ["reason"] = request.Reason,
                    ["status"] = nextStatus.ToString(),
                }),
            cancellationToken);

        return updated;
    }

    private static void ValidateShareTransition(SessionShareBody existing, SessionShareStatus nextStatus)
    {
        if (existing.Status is SessionShareStatus.Revoked or SessionShareStatus.Expired)
        {
            throw new InvalidOperationException($"Share {existing.ShareId} is already terminal with status {existing.Status}.");
        }

        if (nextStatus == SessionShareStatus.Accepted && existing.Status != SessionShareStatus.Pending)
        {
            throw new InvalidOperationException($"Only pending shares can be accepted. Current status: {existing.Status}.");
        }

        if (nextStatus == SessionShareStatus.Rejected && existing.Status is not (SessionShareStatus.Pending or SessionShareStatus.Requested))
        {
            throw new InvalidOperationException($"Only pending or requested shares can be rejected. Current status: {existing.Status}.");
        }
    }

    private async Task<SessionAggregate?> ResolveSessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (_sessions.TryGetValue(sessionId, out var existing))
        {
            return existing;
        }

        var snapshot = (await _sessionStore.ListAsync(cancellationToken))
            .FirstOrDefault(x => string.Equals(x.SessionId, sessionId, StringComparison.OrdinalIgnoreCase));
        if (snapshot is null)
        {
            return null;
        }

        var aggregate = new SessionAggregate(
            snapshot.SessionId,
            snapshot.AgentId,
            snapshot.EndpointId,
            snapshot.Profile,
            snapshot.RequestedBy,
            snapshot.Role,
            snapshot.Participants?.Select(x => new SessionParticipantBody(x.ParticipantId, x.PrincipalId, x.Role, x.JoinedAtUtc, x.UpdatedAtUtc)).ToArray(),
            snapshot.Shares?.Select(x => new SessionShareBody(x.ShareId, x.PrincipalId, x.GrantedBy, x.Role, x.Status, x.CreatedAtUtc, x.UpdatedAtUtc)).ToArray(),
            snapshot.ControlLease,
            snapshot.LastHeartbeatAtUtc,
            snapshot.LeaseExpiresAtUtc,
            snapshot.TenantId,
            snapshot.OrganizationId);
        aggregate.MoveTo(snapshot.State);
        _sessions[snapshot.SessionId] = aggregate;
        TryAssignSessionTransportProvider(snapshot.SessionId, snapshot.EndpointId);
        return aggregate;
    }

    private RealtimeTransportProvider? TryAssignSessionTransportProvider(string sessionId, string? endpointId)
    {
        if (_realtimeTransportFactory is null)
        {
            return null;
        }

        try
        {
            var resolution = _realtimeTransportFactory.ResolveRoute(
                new RealtimeTransportRoutePolicyContext(
                    EndpointId: endpointId,
                    SessionProvider: null,
                    RequestedProvider: null));
            _sessionTransportProviders[sessionId] = resolution.Provider;
            return resolution.Provider;
        }
        catch
        {
            _sessionTransportProviders.TryRemove(sessionId, out _);
            return null;
        }
    }

    private static SessionSnapshot ToSnapshot(SessionAggregate aggregate, DateTimeOffset nowUtc)
    {
        return new SessionSnapshot(
            aggregate.SessionId,
            aggregate.AgentId,
            aggregate.EndpointId,
            aggregate.Profile,
            aggregate.RequestedBy,
            aggregate.Role,
            aggregate.State,
            nowUtc,
            aggregate.Participants.Select(x => new SessionParticipantSnapshot(x.ParticipantId, x.PrincipalId, x.Role, x.JoinedAtUtc, x.UpdatedAtUtc)).ToArray(),
            aggregate.Shares.Select(x => new SessionShareSnapshot(x.ShareId, x.PrincipalId, x.GrantedBy, x.Role, x.Status, x.CreatedAtUtc, x.UpdatedAtUtc)).ToArray(),
            aggregate.ControlLease,
            aggregate.LastHeartbeatAtUtc,
            aggregate.LeaseExpiresAtUtc,
            aggregate.TenantId,
            aggregate.OrganizationId);
    }

    private async Task<SessionControlLeaseBody> ApplyControlLeaseAsync(string sessionId, SessionControlGrantBody request, bool forceTakeover, CancellationToken cancellationToken)
    {
        var aggregate = await RequireSessionAsync(sessionId, cancellationToken);
        var nowUtc = DateTimeOffset.UtcNow;
        SessionAuthorizationPolicy.EnsureInSessionScope(aggregate, request.TenantId, request.OrganizationId, request.OperatorRoles);
        SessionAuthorizationPolicy.EnsureCanModerate(aggregate, request.GrantedBy, request.OperatorRoles, nowUtc);

        var participant = RequireParticipant(aggregate, request.ParticipantId, sessionId);
        SessionAuthorizationPolicy.EnsureControlEligible(participant, sessionId);

        var existingLease = await NormalizeControlLeaseAsync(aggregate, nowUtc, cancellationToken);
        if (!forceTakeover
            && existingLease is not null
            && !string.Equals(existingLease.PrincipalId, participant.PrincipalId, StringComparison.OrdinalIgnoreCase))
        {
            throw ControlArbitrationException.LeaseHeldByOther(
                sessionId,
                existingLease,
                participant.ParticipantId,
                request.GrantedBy);
        }

        var lease = BuildControlLease(participant, request.GrantedBy, nowUtc, request.LeaseSeconds);
        aggregate.ReplaceControlLease(lease);
        aggregate.SetLease(nowUtc, SessionLifecyclePolicy.ComputeLeaseExpiration(nowUtc, _options.LeaseDuration));
        await PersistSessionAsync(aggregate, nowUtc, cancellationToken);
        await _eventWriter.WriteAuditAsync(
            new AuditEventBody(
                "session.control",
                forceTakeover
                    ? $"Control force-transferred to {participant.ParticipantId}"
                    : $"Control granted to {participant.ParticipantId}",
                sessionId,
                new Dictionary<string, object?>
                {
                    ["participantId"] = participant.ParticipantId,
                    ["principalId"] = participant.PrincipalId,
                    ["grantedBy"] = request.GrantedBy,
                    ["reason"] = request.Reason,
                    ["forceTakeover"] = forceTakeover,
                    ["expiresAtUtc"] = lease.ExpiresAtUtc,
                }),
            cancellationToken);

        return lease;
    }

    private SessionParticipantBody RequireParticipant(SessionAggregate aggregate, string participantId, string sessionId)
        => aggregate.Participants.FirstOrDefault(x => string.Equals(x.ParticipantId, participantId, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException($"Participant {participantId} was not found in session {sessionId}.");

    private SessionControlLeaseBody BuildControlLease(SessionParticipantBody participant, string grantedBy, DateTimeOffset nowUtc, int? leaseSeconds)
    {
        var defaultDuration = _options.ControlLeaseDuration > TimeSpan.Zero
            ? _options.ControlLeaseDuration
            : TimeSpan.FromSeconds(30);
        var minDuration = _options.MinControlLeaseDuration > TimeSpan.Zero
            ? _options.MinControlLeaseDuration
            : TimeSpan.FromSeconds(1);
        var maxDurationCandidate = _options.MaxControlLeaseDuration > TimeSpan.Zero
            ? _options.MaxControlLeaseDuration
            : defaultDuration;
        var maxDuration = maxDurationCandidate < minDuration
            ? minDuration
            : maxDurationCandidate;

        var requestedDuration = leaseSeconds.HasValue && leaseSeconds.Value > 0
            ? TimeSpan.FromSeconds(leaseSeconds.Value)
            : defaultDuration;
        var effectiveDuration = requestedDuration < minDuration
            ? minDuration
            : requestedDuration > maxDuration
                ? maxDuration
                : requestedDuration;

        return new SessionControlLeaseBody(
            participant.ParticipantId,
            participant.PrincipalId,
            grantedBy,
            nowUtc,
            nowUtc + effectiveDuration);
    }

    private async Task<SessionControlLeaseBody?> NormalizeControlLeaseAsync(
        SessionAggregate aggregate,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        var lease = aggregate.ControlLease;
        if (lease is null)
        {
            return null;
        }

        if (lease.ExpiresAtUtc > nowUtc)
        {
            return lease;
        }

        aggregate.ReplaceControlLease(null);
        await PersistSessionAsync(aggregate, nowUtc, cancellationToken);
        return null;
    }


    private sealed class TransientCommandJournalStore : ICommandJournalStore
    {
        private readonly ConcurrentQueue<CommandJournalEntryBody> _items = new();

        public Task AppendAsync(CommandJournalEntryBody entry, CancellationToken cancellationToken)
        {
            if (_items.Any(x => string.Equals(x.CommandId, entry.CommandId, StringComparison.OrdinalIgnoreCase)))
            {
                return Task.CompletedTask;
            }

            if (!string.IsNullOrWhiteSpace(entry.IdempotencyKey) &&
                _items.Any(x =>
                    string.Equals(x.SessionId, entry.SessionId, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(x.IdempotencyKey, entry.IdempotencyKey, StringComparison.OrdinalIgnoreCase)))
            {
                return Task.CompletedTask;
            }

            _items.Enqueue(entry);
            return Task.CompletedTask;
        }

        public Task<CommandJournalEntryBody?> FindByCommandIdAsync(string commandId, CancellationToken cancellationToken)
            => Task.FromResult(_items.FirstOrDefault(x => string.Equals(x.CommandId, commandId, StringComparison.OrdinalIgnoreCase)));

        public Task<CommandJournalEntryBody?> FindByIdempotencyKeyAsync(string sessionId, string idempotencyKey, CancellationToken cancellationToken)
            => Task.FromResult(_items.FirstOrDefault(x =>
                string.Equals(x.SessionId, sessionId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(x.IdempotencyKey, idempotencyKey, StringComparison.OrdinalIgnoreCase)));

        public Task<IReadOnlyList<CommandJournalEntryBody>> ListAsync(CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<CommandJournalEntryBody>>(_items.ToArray());

        public Task<IReadOnlyList<CommandJournalEntryBody>> ListBySessionAsync(string sessionId, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<CommandJournalEntryBody>>(_items
                .Where(x => string.Equals(x.SessionId, sessionId, StringComparison.OrdinalIgnoreCase))
                .ToArray());
    }

    private sealed class TransientSessionStore : ISessionStore
    {
        private readonly ConcurrentDictionary<string, SessionSnapshot> _items = new(StringComparer.OrdinalIgnoreCase);

        public Task UpsertAsync(SessionSnapshot snapshot, CancellationToken cancellationToken)
        {
            _items[snapshot.SessionId] = snapshot;
            return Task.CompletedTask;
        }

        public Task RemoveAsync(string sessionId, CancellationToken cancellationToken)
        {
            _items.TryRemove(sessionId, out _);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<SessionSnapshot>> ListAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult<IReadOnlyList<SessionSnapshot>>(_items.Values
                .OrderBy(x => x.SessionId, StringComparer.OrdinalIgnoreCase)
                .ToArray());
        }
    }
}

/// <summary>
/// Registers the in-memory session orchestration services.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds the session orchestration use cases and runtime services.
    /// </summary>
    /// <param name="services">The service collection to extend.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddSessionOrchestrator(this IServiceCollection services)
    {
        services.AddSingleton<SessionMaintenanceOptions>();
        services.AddSingleton<IOperatorPolicyService, OperatorPolicyService>();
        services.AddSingleton<OpenSessionUseCase>();
        services.AddSingleton<DispatchCommandUseCase>();
        services.AddSingleton<SessionMaintenanceService>();
        services.AddSingleton<InMemorySessionOrchestrator>();
        services.AddSingleton<ISessionOrchestrator>(sp => sp.GetRequiredService<InMemorySessionOrchestrator>());
        return services;
    }
}

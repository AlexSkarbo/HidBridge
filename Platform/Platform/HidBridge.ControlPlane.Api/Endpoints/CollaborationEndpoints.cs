using HidBridge.Abstractions;
using HidBridge.Contracts;

namespace HidBridge.ControlPlane.Api.Endpoints;

/// <summary>
/// Registers collaboration endpoints for session participants, shares, and collaboration summaries.
/// </summary>
public static class CollaborationEndpoints
{
    /// <summary>
    /// Maps collaboration endpoint groups onto the API route table.
    /// </summary>
    public static IEndpointRouteBuilder MapCollaborationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var sessionGroup = endpoints.MapGroup("/api/v1/sessions")
            .WithTags(ApiEndpointTags.Collaboration);

        sessionGroup.MapGet("/{sessionId}/participants", async (string sessionId, HttpContext httpContext, ISessionStore sessionStore, ISessionOrchestrator orchestrator, CancellationToken ct) =>
        {
            var caller = ApiCallerContext.FromHttpContext(httpContext);
            try
            {
                caller.EnsureViewerAccess();
                if (caller.IsPresent)
                {
                    await caller.RequireScopedSessionAsync(sessionStore, sessionId, ct);
                }

                return Results.Ok(await orchestrator.ListParticipantsAsync(sessionId, ct));
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new { sessionId, error = ex.Message });
            }
            catch (UnauthorizedAccessException ex)
            {
                return ApiAuthorizationResults.Forbidden(caller, ex, sessionId: sessionId);
            }
        })
        .WithSummary("List active participants in one collaboration session.")
        .WithDescription("Returns the materialized participant set for the specified session, including owner, controllers, observers, and presenters.");

        sessionGroup.MapPost("/{sessionId}/participants", async (
            string sessionId,
            HttpContext httpContext,
            ISessionOrchestrator orchestrator,
            SessionParticipantUpsertBody request,
            CancellationToken ct) =>
        {
            var caller = ApiCallerContext.FromHttpContext(httpContext);
            try
            {
                caller.EnsureModeratorAccess();
                var normalized = caller.Apply(request);
                return Results.Ok(await orchestrator.UpsertParticipantAsync(sessionId, normalized, ct));
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new { sessionId, error = ex.Message });
            }
            catch (UnauthorizedAccessException ex)
            {
                return ApiAuthorizationResults.Forbidden(caller, ex, sessionId: sessionId);
            }
        })
        .Accepts<SessionParticipantUpsertBody>("application/json")
        .Produces<SessionParticipantBody>(StatusCodes.Status200OK)
        .WithSummary("Create or update one session participant.")
        .WithDescription("Creates or updates one collaboration participant with an explicit role grant inside the specified session.");

        sessionGroup.MapDelete("/{sessionId}/participants/{participantId}", async (
            string sessionId,
            string participantId,
            HttpContext httpContext,
            ISessionOrchestrator orchestrator,
            string removedBy,
            string reason,
            CancellationToken ct) =>
        {
            var caller = ApiCallerContext.FromHttpContext(httpContext);
            try
            {
                caller.EnsureModeratorAccess();
                var request = caller
                    .Apply(new SessionParticipantRemoveBody(participantId, removedBy, reason));
                return Results.Ok(await orchestrator.RemoveParticipantAsync(sessionId, request, ct));
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new { sessionId, participantId, error = ex.Message });
            }
            catch (UnauthorizedAccessException ex)
            {
                return ApiAuthorizationResults.Forbidden(caller, ex, sessionId: sessionId, participantId: participantId);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { sessionId, participantId, error = ex.Message });
            }
        })
        .Produces<SessionParticipantRemoveBody>(StatusCodes.Status200OK)
        .WithSummary("Remove one session participant.")
        .WithDescription("Removes a participant from the session. The owner participant cannot be removed directly and must terminate the session instead.");

        sessionGroup.MapGet("/{sessionId}/shares", async (string sessionId, HttpContext httpContext, ISessionStore sessionStore, ISessionOrchestrator orchestrator, CancellationToken ct) =>
        {
            var caller = ApiCallerContext.FromHttpContext(httpContext);
            try
            {
                caller.EnsureViewerAccess();
                if (caller.IsPresent)
                {
                    await caller.RequireScopedSessionAsync(sessionStore, sessionId, ct);
                }

                return Results.Ok(await orchestrator.ListSharesAsync(sessionId, ct));
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new { sessionId, error = ex.Message });
            }
            catch (UnauthorizedAccessException ex)
            {
                return ApiAuthorizationResults.Forbidden(caller, ex, sessionId: sessionId);
            }
        })
        .WithSummary("List share grants for one collaboration session.")
        .WithDescription("Returns the persisted share grants and their current statuses for the specified session.");

        sessionGroup.MapPost("/{sessionId}/shares", async (
            string sessionId,
            HttpContext httpContext,
            ISessionOrchestrator orchestrator,
            SessionShareGrantBody request,
            CancellationToken ct) =>
        {
            var caller = ApiCallerContext.FromHttpContext(httpContext);
            try
            {
                caller.EnsureModeratorAccess();
                var normalized = caller.Apply(request);
                return Results.Ok(await orchestrator.GrantShareAsync(sessionId, normalized, ct));
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new { sessionId, error = ex.Message });
            }
            catch (UnauthorizedAccessException ex)
            {
                return ApiAuthorizationResults.Forbidden(caller, ex, sessionId: sessionId);
            }
        })
        .Accepts<SessionShareGrantBody>("application/json")
        .Produces<SessionShareBody>(StatusCodes.Status200OK)
        .WithSummary("Grant a new share invitation for the session.")
        .WithDescription("Creates or updates a pending collaboration grant that may later be accepted, rejected, or revoked.");

        sessionGroup.MapPost("/{sessionId}/shares/{shareId}/accept", async (
            string sessionId,
            string shareId,
            HttpContext httpContext,
            ISessionOrchestrator orchestrator,
            SessionShareTransitionBody request,
            CancellationToken ct) =>
        {
            var caller = ApiCallerContext.FromHttpContext(httpContext);
            try
            {
                caller.EnsureViewerAccess();
                var normalized = caller.Apply(request with { ShareId = shareId });
                return Results.Ok(await orchestrator.AcceptShareAsync(sessionId, normalized, ct));
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new { sessionId, shareId, error = ex.Message });
            }
            catch (UnauthorizedAccessException ex)
            {
                return ApiAuthorizationResults.Forbidden(caller, ex, sessionId: sessionId, shareId: shareId);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { sessionId, shareId, error = ex.Message });
            }
        })
        .Accepts<SessionShareTransitionBody>("application/json")
        .Produces<SessionShareBody>(StatusCodes.Status200OK)
        .WithSummary("Accept a pending share invitation.")
        .WithDescription("Transitions a pending share to Accepted and materializes the grantee as an active participant in the session.");

        sessionGroup.MapPost("/{sessionId}/shares/{shareId}/reject", async (
            string sessionId,
            string shareId,
            HttpContext httpContext,
            ISessionOrchestrator orchestrator,
            SessionShareTransitionBody request,
            CancellationToken ct) =>
        {
            var caller = ApiCallerContext.FromHttpContext(httpContext);
            try
            {
                caller.EnsureViewerAccess();
                var normalized = caller.Apply(request with { ShareId = shareId });
                return Results.Ok(await orchestrator.RejectShareAsync(sessionId, normalized, ct));
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new { sessionId, shareId, error = ex.Message });
            }
            catch (UnauthorizedAccessException ex)
            {
                return ApiAuthorizationResults.Forbidden(caller, ex, sessionId: sessionId, shareId: shareId);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { sessionId, shareId, error = ex.Message });
            }
        })
        .Accepts<SessionShareTransitionBody>("application/json")
        .Produces<SessionShareBody>(StatusCodes.Status200OK)
        .WithSummary("Reject a pending share invitation.")
        .WithDescription("Transitions a pending share to Rejected without materializing a participant.");

        sessionGroup.MapPost("/{sessionId}/shares/{shareId}/revoke", async (
            string sessionId,
            string shareId,
            HttpContext httpContext,
            ISessionOrchestrator orchestrator,
            SessionShareTransitionBody request,
            CancellationToken ct) =>
        {
            var caller = ApiCallerContext.FromHttpContext(httpContext);
            try
            {
                caller.EnsureModeratorAccess();
                var normalized = caller.Apply(request with { ShareId = shareId });
                return Results.Ok(await orchestrator.RevokeShareAsync(sessionId, normalized, ct));
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new { sessionId, shareId, error = ex.Message });
            }
            catch (UnauthorizedAccessException ex)
            {
                return ApiAuthorizationResults.Forbidden(caller, ex, sessionId: sessionId, shareId: shareId);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { sessionId, shareId, error = ex.Message });
            }
        })
        .Accepts<SessionShareTransitionBody>("application/json")
        .Produces<SessionShareBody>(StatusCodes.Status200OK)
        .WithSummary("Revoke an existing share invitation or grant.")
        .WithDescription("Transitions a share to Revoked. When the share was previously accepted, the derived participant is removed from the session.");

        var collaborationGroup = endpoints.MapGroup("/api/v1/collaboration")
            .WithTags(ApiEndpointTags.Collaboration);

        collaborationGroup.MapGet("/sessions/{sessionId}/summary", async (string sessionId, HttpContext httpContext, ISessionStore sessionStore, CancellationToken ct) =>
        {
            var caller = ApiCallerContext.FromHttpContext(httpContext);
            try
            {
                caller.EnsureViewerAccess();
                var snapshot = caller.IsPresent
                    ? await caller.RequireScopedSessionAsync(sessionStore, sessionId, ct)
                    : (await sessionStore.ListAsync(ct))
                        .FirstOrDefault(x => string.Equals(x.SessionId, sessionId, StringComparison.OrdinalIgnoreCase));
                if (snapshot is null)
                {
                    return Results.NotFound(new { sessionId, error = $"Session {sessionId} was not found." });
                }

                return Results.Ok(new
                {
                    snapshot.SessionId,
                    snapshot.AgentId,
                    snapshot.EndpointId,
                    snapshot.State,
                    snapshot.Profile,
                    snapshot.Role,
                    snapshot.RequestedBy,
                    snapshot.TenantId,
                    snapshot.OrganizationId,
                    participantCount = snapshot.Participants?.Count ?? 0,
                    activeControllers = snapshot.Participants?.Count(x => x.Role == SessionRole.Controller) ?? 0,
                    observers = snapshot.Participants?.Count(x => x.Role == SessionRole.Observer) ?? 0,
                    presenters = snapshot.Participants?.Count(x => x.Role == SessionRole.Presenter) ?? 0,
                    requestedInvitations = snapshot.Shares?.Count(x => x.Status == SessionShareStatus.Requested) ?? 0,
                    pendingShares = snapshot.Shares?.Count(x => x.Status == SessionShareStatus.Pending) ?? 0,
                    acceptedShares = snapshot.Shares?.Count(x => x.Status == SessionShareStatus.Accepted) ?? 0,
                    rejectedShares = snapshot.Shares?.Count(x => x.Status == SessionShareStatus.Rejected) ?? 0,
                    revokedShares = snapshot.Shares?.Count(x => x.Status == SessionShareStatus.Revoked) ?? 0,
                    snapshot.LastHeartbeatAtUtc,
                    snapshot.LeaseExpiresAtUtc,
                });
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new { sessionId, error = ex.Message });
            }
            catch (UnauthorizedAccessException ex)
            {
                return ApiAuthorizationResults.Forbidden(caller, ex, sessionId: sessionId);
            }
        })
        .WithSummary("Read a compact collaboration summary for one session.")
        .WithDescription("Returns a collaboration-oriented read model with participant counts, role breakdown, share status counts, and current lease timestamps.");

        return endpoints;
    }
}

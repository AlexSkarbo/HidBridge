using HidBridge.Abstractions;
using HidBridge.Contracts;

namespace HidBridge.ControlPlane.Api.Endpoints;

/// <summary>
/// Registers invitation request and approval endpoints for collaboration sessions.
/// </summary>
public static class InvitationEndpoints
{
    /// <summary>
    /// Maps invitation endpoints onto the API route table.
    /// </summary>
    /// <param name="endpoints">The route builder to extend.</param>
    /// <returns>The same route builder for chaining.</returns>
    public static IEndpointRouteBuilder MapInvitationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var invitationGroup = endpoints.MapGroup("/api/v1/sessions")
            .WithTags(ApiEndpointTags.Collaboration);

        invitationGroup.MapGet("/{sessionId}/invitations", async (string sessionId, HttpContext httpContext, ISessionStore sessionStore, ISessionOrchestrator orchestrator, CancellationToken ct) =>
        {
            var caller = ApiCallerContext.FromHttpContext(httpContext);
            try
            {
                caller.EnsureViewerAccess();
                if (caller.IsPresent)
                {
                    await caller.RequireScopedSessionAsync(sessionStore, sessionId, ct);
                }

                var shares = await orchestrator.ListSharesAsync(sessionId, ct);
                return Results.Ok(shares.Where(x => x.Status is SessionShareStatus.Requested or SessionShareStatus.Pending or SessionShareStatus.Rejected or SessionShareStatus.Accepted or SessionShareStatus.Revoked));
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
        .WithSummary("List invitation records for one session.")
        .WithDescription("Returns share records that participate in the invitation and approval lifecycle, including requested, pending, accepted, rejected, and revoked invitations.");

        invitationGroup.MapPost("/{sessionId}/invitations/requests", async (
            string sessionId,
            HttpContext httpContext,
            ISessionOrchestrator orchestrator,
            SessionInvitationRequestBody request,
            CancellationToken ct) =>
        {
            var caller = ApiCallerContext.FromHttpContext(httpContext);
            try
            {
                caller.EnsureViewerAccess();
                var normalized = caller.Apply(request);
                return Results.Ok(await orchestrator.RequestInvitationAsync(sessionId, normalized, ct));
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
        .Accepts<SessionInvitationRequestBody>("application/json")
        .Produces<SessionShareBody>(StatusCodes.Status200OK)
        .WithSummary("Create a new invitation request.")
        .WithDescription("Creates an invitation request in Requested state. This is the lobby-style step where a principal asks to be granted access before an owner or controller approves the request.");

        invitationGroup.MapPost("/{sessionId}/invitations/{shareId}/approve", async (
            string sessionId,
            string shareId,
            HttpContext httpContext,
            ISessionOrchestrator orchestrator,
            SessionInvitationDecisionBody request,
            CancellationToken ct) =>
        {
            var caller = ApiCallerContext.FromHttpContext(httpContext);
            try
            {
                caller.EnsureModeratorAccess();
                var normalized = caller.Apply(request with { ShareId = shareId });
                return Results.Ok(await orchestrator.ApproveInvitationAsync(sessionId, normalized, ct));
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
        .Accepts<SessionInvitationDecisionBody>("application/json")
        .Produces<SessionShareBody>(StatusCodes.Status200OK)
        .WithSummary("Approve one invitation request.")
        .WithDescription("Transitions a Requested invitation to Pending and optionally overrides the granted role. The grantee must still explicitly accept the resulting invitation before becoming an active participant.");

        invitationGroup.MapPost("/{sessionId}/invitations/{shareId}/decline", async (
            string sessionId,
            string shareId,
            HttpContext httpContext,
            ISessionOrchestrator orchestrator,
            SessionInvitationDecisionBody request,
            CancellationToken ct) =>
        {
            var caller = ApiCallerContext.FromHttpContext(httpContext);
            try
            {
                caller.EnsureModeratorAccess();
                var normalized = caller.Apply(request with { ShareId = shareId });
                return Results.Ok(await orchestrator.DeclineInvitationAsync(sessionId, normalized, ct));
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
        .Accepts<SessionInvitationDecisionBody>("application/json")
        .Produces<SessionShareBody>(StatusCodes.Status200OK)
        .WithSummary("Decline one invitation request.")
        .WithDescription("Transitions a Requested invitation to Rejected without creating a pending invite or participant.");

        var collaborationGroup = endpoints.MapGroup("/api/v1/collaboration")
            .WithTags(ApiEndpointTags.Collaboration);

        collaborationGroup.MapGet("/sessions/{sessionId}/lobby", async (string sessionId, HttpContext httpContext, ISessionStore sessionStore, CancellationToken ct) =>
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

                var shares = snapshot.Shares ?? Array.Empty<SessionShareSnapshot>();
                return Results.Ok(new
                {
                    snapshot.SessionId,
                    requested = shares.Where(x => x.Status == SessionShareStatus.Requested).ToArray(),
                    pending = shares.Where(x => x.Status == SessionShareStatus.Pending).ToArray(),
                    accepted = shares.Where(x => x.Status == SessionShareStatus.Accepted).ToArray(),
                    rejected = shares.Where(x => x.Status == SessionShareStatus.Rejected).ToArray(),
                    revoked = shares.Where(x => x.Status == SessionShareStatus.Revoked).ToArray(),
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
        .WithSummary("Read the collaboration lobby view for one session.")
        .WithDescription("Returns a collaboration read model focused on invitation processing: requested access requests, pending invitations, and their resolved outcomes.");

        return endpoints;
    }
}

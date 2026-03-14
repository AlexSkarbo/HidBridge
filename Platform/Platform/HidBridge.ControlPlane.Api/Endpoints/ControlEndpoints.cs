using HidBridge.Abstractions;
using HidBridge.Contracts;
using HidBridge.SessionOrchestrator;

namespace HidBridge.ControlPlane.Api.Endpoints;

/// <summary>
/// Registers active-control arbitration endpoints for collaborative sessions.
/// </summary>
public static class ControlEndpoints
{
    /// <summary>
    /// Maps control arbitration endpoints onto the API route table.
    /// </summary>
    /// <param name="endpoints">The route builder to extend.</param>
    /// <returns>The same route builder for chaining.</returns>
    public static IEndpointRouteBuilder MapControlEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/sessions")
            .WithTags(ApiEndpointTags.Control);

        group.MapGet("/{sessionId}/control", async (string sessionId, HttpContext httpContext, ISessionStore sessionStore, ISessionOrchestrator orchestrator, CancellationToken ct) =>
        {
            var caller = ApiCallerContext.FromHttpContext(httpContext);
            try
            {
                caller.EnsureViewerAccess();
                if (caller.IsPresent)
                {
                    await caller.RequireScopedSessionAsync(sessionStore, sessionId, ct);
                }

                return Results.Ok(await orchestrator.GetControlLeaseAsync(sessionId, ct));
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
        .Produces<SessionControlLeaseBody>(StatusCodes.Status200OK)
        .WithSummary("Read the currently active control lease for one session.")
        .WithDescription("Returns the active controller lease when one exists. When no controller is active, the endpoint returns a 200 response with a null body.");

        group.MapPost("/{sessionId}/control/request", async (
            string sessionId,
            HttpContext httpContext,
            ISessionOrchestrator orchestrator,
            SessionControlRequestBody request,
            CancellationToken ct) =>
        {
            var caller = ApiCallerContext.FromHttpContext(httpContext);
            try
            {
                caller.EnsureViewerAccess();
                var normalized = caller.Apply(request);
                return Results.Ok(await orchestrator.RequestControlAsync(sessionId, normalized, ct));
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new { sessionId, error = ex.Message });
            }
            catch (UnauthorizedAccessException ex)
            {
                return ApiAuthorizationResults.Forbidden(caller, ex, sessionId: sessionId);
            }
            catch (ControlArbitrationException ex)
            {
                return ToControlConflict(ex);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { sessionId, error = ex.Message });
            }
        })
        .Accepts<SessionControlRequestBody>("application/json")
        .Produces<SessionControlLeaseBody>(StatusCodes.Status200OK)
        .WithSummary("Request active control for one participant.")
        .WithDescription("Grants control immediately when no competing controller lease is active. If another active controller already holds the lease, the endpoint returns a conflict.");

        group.MapPost("/{sessionId}/control/grant", async (
            string sessionId,
            HttpContext httpContext,
            ISessionOrchestrator orchestrator,
            SessionControlGrantBody request,
            CancellationToken ct) =>
        {
            var caller = ApiCallerContext.FromHttpContext(httpContext);
            try
            {
                caller.EnsureModeratorAccess();
                var normalized = caller.Apply(request);
                return Results.Ok(await orchestrator.GrantControlAsync(sessionId, normalized, ct));
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new { sessionId, error = ex.Message });
            }
            catch (UnauthorizedAccessException ex)
            {
                return ApiAuthorizationResults.Forbidden(caller, ex, sessionId: sessionId);
            }
            catch (ControlArbitrationException ex)
            {
                return ToControlConflict(ex);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { sessionId, error = ex.Message });
            }
        })
        .Accepts<SessionControlGrantBody>("application/json")
        .Produces<SessionControlLeaseBody>(StatusCodes.Status200OK)
        .WithSummary("Grant control to one participant.")
        .WithDescription("Allows a moderator to assign the active control lease when no conflicting controller is active.");

        group.MapPost("/{sessionId}/control/force-takeover", async (
            string sessionId,
            HttpContext httpContext,
            ISessionOrchestrator orchestrator,
            SessionControlGrantBody request,
            CancellationToken ct) =>
        {
            var caller = ApiCallerContext.FromHttpContext(httpContext);
            try
            {
                caller.EnsureModeratorAccess();
                var normalized = caller.Apply(request);
                return Results.Ok(await orchestrator.ForceTakeoverControlAsync(sessionId, normalized, ct));
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new { sessionId, error = ex.Message });
            }
            catch (UnauthorizedAccessException ex)
            {
                return ApiAuthorizationResults.Forbidden(caller, ex, sessionId: sessionId);
            }
            catch (ControlArbitrationException ex)
            {
                return ToControlConflict(ex);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { sessionId, error = ex.Message });
            }
        })
        .Accepts<SessionControlGrantBody>("application/json")
        .Produces<SessionControlLeaseBody>(StatusCodes.Status200OK)
        .WithSummary("Force-transfer control to one participant.")
        .WithDescription("Allows a moderator to override the current controller and immediately assign a new active control lease.");

        group.MapPost("/{sessionId}/control/release", async (
            string sessionId,
            HttpContext httpContext,
            ISessionOrchestrator orchestrator,
            SessionControlReleaseBody request,
            CancellationToken ct) =>
        {
            var caller = ApiCallerContext.FromHttpContext(httpContext);
            try
            {
                caller.EnsureViewerAccess();
                var normalized = caller.Apply(request);
                return Results.Ok(await orchestrator.ReleaseControlAsync(sessionId, normalized, ct));
            }
            catch (KeyNotFoundException ex)
            {
                return Results.NotFound(new { sessionId, error = ex.Message });
            }
            catch (UnauthorizedAccessException ex)
            {
                return ApiAuthorizationResults.Forbidden(caller, ex, sessionId: sessionId);
            }
            catch (ControlArbitrationException ex)
            {
                return ToControlConflict(ex);
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { sessionId, error = ex.Message });
            }
        })
        .Accepts<SessionControlReleaseBody>("application/json")
        .Produces<SessionControlLeaseBody>(StatusCodes.Status200OK)
        .WithSummary("Release the currently active control lease.")
        .WithDescription("Allows the current controller or the owner to release active control.");

        return endpoints;
    }

    private static IResult ToControlConflict(ControlArbitrationException ex)
        => Results.Conflict(new
        {
            sessionId = ex.SessionId,
            code = ex.Code,
            error = ex.Message,
            requestedParticipantId = ex.RequestedParticipantId,
            actedBy = ex.ActedBy,
            currentController = ex.CurrentControllerParticipantId is null && ex.CurrentControllerPrincipalId is null && ex.LeaseExpiresAtUtc is null
                ? null
                : new
                {
                    participantId = ex.CurrentControllerParticipantId,
                    principalId = ex.CurrentControllerPrincipalId,
                    expiresAtUtc = ex.LeaseExpiresAtUtc,
                },
        });
}

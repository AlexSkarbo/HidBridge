using HidBridge.Abstractions;
using HidBridge.Contracts;

namespace HidBridge.ControlPlane.Api.Endpoints;

/// <summary>
/// Registers collaboration dashboard and read model endpoints.
/// </summary>
public static class CollaborationReadModelEndpoints
{
    /// <summary>
    /// Maps collaboration read model endpoints onto the API route table.
    /// </summary>
    /// <param name="endpoints">The route builder to extend.</param>
    /// <returns>The same route builder for chaining.</returns>
    public static IEndpointRouteBuilder MapCollaborationReadModelEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/collaboration")
            .WithTags(ApiEndpointTags.CollaborationReadModels);

        group.MapGet("/sessions/{sessionId}/dashboard", async (
            string sessionId,
            HttpContext httpContext,
            ISessionStore sessionStore,
            CollaborationReadModelService service,
            CancellationToken ct) =>
        {
            var caller = ApiCallerContext.FromHttpContext(httpContext);
            try
            {
                caller.EnsureViewerAccess();
                if (caller.IsPresent)
                {
                    await caller.RequireScopedSessionAsync(sessionStore, sessionId, ct);
                }

                return Results.Ok(await service.GetSessionDashboardAsync(sessionId, ct));
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
        .Produces<SessionDashboardReadModel>(StatusCodes.Status200OK)
        .WithSummary("Read the main collaboration dashboard for one session.")
        .WithDescription("Returns one aggregate dashboard projection with participant, share, command, timeline, and lease metrics for the specified session.");

        group.MapGet("/sessions/{sessionId}/shares/dashboard", async (
            string sessionId,
            HttpContext httpContext,
            ISessionStore sessionStore,
            CollaborationReadModelService service,
            CancellationToken ct) =>
        {
            var caller = ApiCallerContext.FromHttpContext(httpContext);
            try
            {
                caller.EnsureViewerAccess();
                if (caller.IsPresent)
                {
                    await caller.RequireScopedSessionAsync(sessionStore, sessionId, ct);
                }

                return Results.Ok(await service.GetShareDashboardAsync(sessionId, ct));
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
        .Produces<ShareDashboardReadModel>(StatusCodes.Status200OK)
        .WithSummary("Read the share and invitation dashboard for one session.")
        .WithDescription("Returns one grouped dashboard projection for requested, pending, accepted, rejected, and revoked shares.");

        group.MapGet("/sessions/{sessionId}/participants/activity", async (
            string sessionId,
            HttpContext httpContext,
            ISessionStore sessionStore,
            CollaborationReadModelService service,
            CancellationToken ct) =>
        {
            var caller = ApiCallerContext.FromHttpContext(httpContext);
            try
            {
                caller.EnsureViewerAccess();
                if (caller.IsPresent)
                {
                    await caller.RequireScopedSessionAsync(sessionStore, sessionId, ct);
                }

                return Results.Ok(await service.GetParticipantActivityAsync(sessionId, ct));
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
        .Produces<IReadOnlyList<ParticipantActivityReadModel>>(StatusCodes.Status200OK)
        .WithSummary("Read participant activity analytics for one session.")
        .WithDescription("Returns one participant-centric activity projection with command counts, failure counts, last command timestamps, and share-derived participant metadata.");

        group.MapGet("/sessions/{sessionId}/operators/timeline", async (
            string sessionId,
            string? principalId,
            int? take,
            HttpContext httpContext,
            ISessionStore sessionStore,
            CollaborationReadModelService service,
            CancellationToken ct) =>
        {
            var caller = ApiCallerContext.FromHttpContext(httpContext);
            try
            {
                caller.EnsureViewerAccess();
                if (caller.IsPresent)
                {
                    await caller.RequireScopedSessionAsync(sessionStore, sessionId, ct);
                }

                return Results.Ok(await service.GetOperatorTimelineAsync(sessionId, principalId, take ?? 100, ct));
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
        .Produces<OperatorTimelineReadModel>(StatusCodes.Status200OK)
        .WithSummary("Read the operator-focused timeline for one session.")
        .WithDescription("Returns one collaboration timeline projection. Use the optional principalId query parameter to filter timeline entries to one operator.");

        group.MapGet("/sessions/{sessionId}/control/dashboard", async (
            string sessionId,
            HttpContext httpContext,
            ISessionStore sessionStore,
            CollaborationReadModelService service,
            CancellationToken ct) =>
        {
            var caller = ApiCallerContext.FromHttpContext(httpContext);
            try
            {
                caller.EnsureViewerAccess();
                if (caller.IsPresent)
                {
                    await caller.RequireScopedSessionAsync(sessionStore, sessionId, ct);
                }

                return Results.Ok(await service.GetControlDashboardAsync(sessionId, ct));
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
        .Produces<ControlDashboardReadModel>(StatusCodes.Status200OK)
        .WithSummary("Read the control-arbitration dashboard for one session.")
        .WithDescription("Returns the current control lease and the list of eligible participants that may become active controllers.");

        return endpoints;
    }
}

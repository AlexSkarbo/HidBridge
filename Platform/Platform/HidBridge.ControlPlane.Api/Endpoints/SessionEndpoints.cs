using HidBridge.Abstractions;
using HidBridge.Contracts;

namespace HidBridge.ControlPlane.Api.Endpoints;

/// <summary>
/// Registers core session lifecycle and command dispatch endpoints.
/// </summary>
public static class SessionEndpoints
{
    /// <summary>
    /// Maps session endpoints onto the API route table.
    /// </summary>
    public static IEndpointRouteBuilder MapSessionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var sessionGroup = endpoints.MapGroup("/api/v1/sessions")
            .WithTags(ApiEndpointTags.Sessions);

        sessionGroup.MapPost("/", async (HttpContext httpContext, ISessionOrchestrator orchestrator, SessionOpenBody request, CancellationToken ct) =>
        {
            var caller = ApiCallerContext.FromHttpContext(httpContext);
            try
            {
                caller.EnsureViewerAccess();
                return Results.Ok(await orchestrator.OpenAsync(caller.Apply(request), ct));
            }
            catch (UnauthorizedAccessException ex)
            {
                return ApiAuthorizationResults.Forbidden(caller, ex, sessionId: request.SessionId);
            }
        })
            .Accepts<SessionOpenBody>("application/json")
            .Produces<SessionOpenBody>(StatusCodes.Status200OK)
            .WithSummary("Open a control session bound to a target agent.")
            .WithDescription("Creates a persisted session snapshot that binds subsequent commands to the specified target agent and optional endpoint.");

        sessionGroup.MapGet("/", async (HttpContext httpContext, ISessionStore sessionStore, CancellationToken ct) =>
        {
            var caller = ApiCallerContext.FromHttpContext(httpContext);
            try
            {
                caller.EnsureViewerAccess();
                var items = await sessionStore.ListAsync(ct);
                if (!caller.IsPresent)
                {
                    return Results.Ok(items);
                }

                return Results.Ok(items.Where(snapshot => IsVisibleToCaller(caller, snapshot)).ToArray());
            }
            catch (UnauthorizedAccessException ex)
            {
                return ApiAuthorizationResults.Forbidden(caller, ex);
            }
        })
            .WithSummary("List persisted session snapshots tracked by the orchestrator.")
            .WithDescription("Returns the persisted session snapshot set, including bound agent, endpoint, role, state, and last update time.");

        sessionGroup.MapGet("/{sessionId}/commands/journal", async (string sessionId, HttpContext httpContext, ISessionStore sessionStore, ICommandJournalStore journalStore, CancellationToken ct) =>
        {
            var caller = ApiCallerContext.FromHttpContext(httpContext);
            try
            {
                caller.EnsureViewerAccess();
                if (caller.IsPresent)
                {
                    await caller.RequireScopedSessionAsync(sessionStore, sessionId, ct);
                }

                return Results.Ok(await journalStore.ListBySessionAsync(sessionId, ct));
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
            .WithSummary("Read the command journal for one session.")
            .WithDescription("Returns persisted command journal entries for the specified session in storage order.");

        sessionGroup.MapPost("/{sessionId}/commands", async (
            string sessionId,
            HttpContext httpContext,
            ISessionOrchestrator orchestrator,
            CommandRequestBody request,
            CancellationToken ct) =>
        {
            // The session owns the target agent binding; commands only need the session id plus payload.
            var caller = ApiCallerContext.FromHttpContext(httpContext);
            try
            {
                caller.EnsureViewerAccess();
                var normalized = caller.Apply(request with { SessionId = sessionId });
                var ack = await orchestrator.DispatchAsync(normalized, ct);
                return Results.Ok(ack);
            }
            catch (UnauthorizedAccessException ex)
            {
                return ApiAuthorizationResults.Forbidden(caller, ex, sessionId: sessionId);
            }
        })
        .Accepts<CommandRequestBody>("application/json")
        .Produces<CommandAckBody>(StatusCodes.Status200OK)
        .WithSummary("Dispatch a command to the agent bound to the session.")
        .WithDescription("Executes a command through the session's bound agent. The route sessionId wins over any SessionId value in the request body.");

        endpoints.MapGet("/api/v1/commands", async (HttpContext httpContext, string? sessionId, ISessionStore sessionStore, ICommandJournalStore journalStore, CancellationToken ct) =>
        {
            var caller = ApiCallerContext.FromHttpContext(httpContext);
            try
            {
                caller.EnsureViewerAccess();
                if (!string.IsNullOrWhiteSpace(sessionId) && caller.IsPresent)
                {
                    await caller.RequireScopedSessionAsync(sessionStore, sessionId, ct);
                }

                var items = string.IsNullOrWhiteSpace(sessionId)
                    ? await journalStore.ListAsync(ct)
                    : await journalStore.ListBySessionAsync(sessionId, ct);

                if (!caller.IsPresent)
                {
                    return Results.Ok(items);
                }

                var visibleSessionIds = (await sessionStore.ListAsync(ct))
                    .Where(snapshot => IsVisibleToCaller(caller, snapshot))
                    .Select(snapshot => snapshot.SessionId)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                return Results.Ok(items.Where(item => visibleSessionIds.Contains(item.SessionId)).ToArray());
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
        .WithTags(ApiEndpointTags.Sessions)
        .WithSummary("Read the persisted command journal.")
        .WithDescription("Returns all command journal entries or narrows the result to one session when the optional sessionId query parameter is supplied.");

        return endpoints;
    }

    private static bool IsVisibleToCaller(ApiCallerContext caller, SessionSnapshot snapshot)
    {
        try
        {
            caller.EnsureSessionScope(snapshot);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}

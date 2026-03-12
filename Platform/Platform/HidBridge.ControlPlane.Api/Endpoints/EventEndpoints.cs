using HidBridge.Abstractions;
using HidBridge.Contracts;

namespace HidBridge.ControlPlane.Api.Endpoints;

/// <summary>
/// Registers audit and telemetry event endpoints.
/// </summary>
public static class EventEndpoints
{
    /// <summary>
    /// Maps event endpoints onto the API route table.
    /// </summary>
    public static IEndpointRouteBuilder MapEventEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var eventGroup = endpoints.MapGroup("/api/v1/events")
            .WithTags(ApiEndpointTags.Events);

        eventGroup.MapGet("/audit", async (HttpContext httpContext, IEventStore eventStore, ISessionStore sessionStore, CancellationToken ct) =>
        {
            var caller = ApiCallerContext.FromHttpContext(httpContext);
            try
            {
                caller.EnsureViewerAccess();
                var items = await eventStore.ListAuditAsync(ct);
                if (!caller.IsPresent)
                {
                    return Results.Ok(items);
                }

                var visibleSessionIds = (await sessionStore.ListAsync(ct))
                    .Where(snapshot => IsVisibleToCaller(caller, snapshot))
                    .Select(snapshot => snapshot.SessionId)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                return Results.Ok(items.Where(item => string.IsNullOrWhiteSpace(item.SessionId) || visibleSessionIds.Contains(item.SessionId)).ToArray());
            }
            catch (UnauthorizedAccessException ex)
            {
                return ApiAuthorizationResults.Forbidden(caller, ex);
            }
        })
            .WithSummary("Read the persisted audit event stream.")
            .WithDescription("Returns audit events currently persisted by the Platform baseline.");

        eventGroup.MapGet("/telemetry", async (HttpContext httpContext, IEventStore eventStore, ISessionStore sessionStore, CancellationToken ct) =>
        {
            var caller = ApiCallerContext.FromHttpContext(httpContext);
            try
            {
                caller.EnsureViewerAccess();
                var items = await eventStore.ListTelemetryAsync(ct);
                if (!caller.IsPresent)
                {
                    return Results.Ok(items);
                }

                var visibleSessionIds = (await sessionStore.ListAsync(ct))
                    .Where(snapshot => IsVisibleToCaller(caller, snapshot))
                    .Select(snapshot => snapshot.SessionId)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                return Results.Ok(items.Where(item => string.IsNullOrWhiteSpace(item.SessionId) || visibleSessionIds.Contains(item.SessionId)).ToArray());
            }
            catch (UnauthorizedAccessException ex)
            {
                return ApiAuthorizationResults.Forbidden(caller, ex);
            }
        })
            .WithSummary("Read the persisted telemetry event stream.")
            .WithDescription("Returns telemetry events currently persisted by the Platform baseline.");

        eventGroup.MapGet("/timeline/{sessionId}", async (string sessionId, int? take, HttpContext httpContext, IEventStore eventStore, ICommandJournalStore journalStore, ISessionStore sessionStore, CancellationToken ct) =>
        {
            var caller = ApiCallerContext.FromHttpContext(httpContext);
            try
            {
                caller.EnsureViewerAccess();
                if (caller.IsPresent)
                {
                    await caller.RequireScopedSessionAsync(sessionStore, sessionId, ct);
                }

                var audit = await eventStore.ListAuditAsync(ct);
                var telemetry = await eventStore.ListTelemetryAsync(ct);
                var commands = await journalStore.ListBySessionAsync(sessionId, ct);
                return Results.Ok(TimelineComposer.Compose(audit, telemetry, commands, sessionId, take ?? 100));
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
        .WithSummary("Read the unified session timeline.")
        .WithDescription("Returns a reverse-chronological timeline that merges audit events, telemetry, and command journal entries for the specified session. Use the optional take query parameter to limit the number of returned records.");

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

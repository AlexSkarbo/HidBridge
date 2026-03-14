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

        sessionGroup.MapPost("/actions/close-failed", async (
            HttpContext httpContext,
            ISessionStore sessionStore,
            ISessionOrchestrator orchestrator,
            CloseFailedSessionsActionBody? request,
            CancellationToken ct) =>
        {
            var caller = ApiCallerContext.FromHttpContext(httpContext);
            try
            {
                caller.EnsureModeratorAccess();
                var snapshots = await sessionStore.ListAsync(ct);
                var visibleSnapshots = caller.IsPresent
                    ? snapshots.Where(snapshot => IsVisibleToCaller(caller, snapshot)).ToArray()
                    : snapshots.ToArray();
                var matchedSnapshots = visibleSnapshots
                    .Where(snapshot => snapshot.State == SessionState.Failed)
                    .OrderBy(snapshot => snapshot.SessionId, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                var dryRun = request?.DryRun ?? false;
                var reason = string.IsNullOrWhiteSpace(request?.Reason)
                    ? $"bulk failed-room cleanup by {caller.EffectivePrincipalId ?? "system"}"
                    : request!.Reason.Trim();

                var closedSessionIds = new List<string>(matchedSnapshots.Length);
                var skippedSessionIds = new List<string>();
                var errors = new List<string>();
                if (!dryRun)
                {
                    foreach (var snapshot in matchedSnapshots)
                    {
                        try
                        {
                            await orchestrator.CloseAsync(new SessionCloseBody(snapshot.SessionId, reason), ct);
                            closedSessionIds.Add(snapshot.SessionId);
                        }
                        catch (KeyNotFoundException)
                        {
                            skippedSessionIds.Add(snapshot.SessionId);
                        }
                        catch (Exception ex)
                        {
                            errors.Add($"{snapshot.SessionId}: {ex.Message}");
                        }
                    }
                }

                return Results.Ok(new SessionBulkCloseResultBody(
                    Action: "close-failed",
                    DryRun: dryRun,
                    Reason: reason,
                    GeneratedAtUtc: DateTimeOffset.UtcNow,
                    ScannedSessions: visibleSnapshots.Length,
                    MatchedSessions: matchedSnapshots.Length,
                    ClosedSessions: closedSessionIds.Count,
                    SkippedSessions: skippedSessionIds.Count,
                    FailedClosures: errors.Count,
                    StaleAfterMinutes: null,
                    MatchedSessionIds: matchedSnapshots.Select(snapshot => snapshot.SessionId).ToArray(),
                    ClosedSessionIds: closedSessionIds.ToArray(),
                    SkippedSessionIds: skippedSessionIds.ToArray(),
                    Errors: errors.ToArray()));
            }
            catch (UnauthorizedAccessException ex)
            {
                return ApiAuthorizationResults.Forbidden(caller, ex);
            }
        })
            .Accepts<CloseFailedSessionsActionBody>("application/json")
            .Produces<SessionBulkCloseResultBody>(StatusCodes.Status200OK)
            .WithSummary("Close all failed sessions visible to the caller.")
            .WithDescription("Finds failed sessions, optionally dry-runs the operation, and returns one summary with closed/skipped/error counters.");

        sessionGroup.MapPost("/actions/close-stale", async (
            HttpContext httpContext,
            ISessionStore sessionStore,
            ISessionOrchestrator orchestrator,
            CloseStaleSessionsActionBody? request,
            CancellationToken ct) =>
        {
            var caller = ApiCallerContext.FromHttpContext(httpContext);
            try
            {
                caller.EnsureModeratorAccess();

                var staleAfterMinutes = request?.StaleAfterMinutes ?? 30;
                staleAfterMinutes = Math.Clamp(staleAfterMinutes, 1, 60 * 24 * 30);

                var snapshots = await sessionStore.ListAsync(ct);
                var visibleSnapshots = caller.IsPresent
                    ? snapshots.Where(snapshot => IsVisibleToCaller(caller, snapshot)).ToArray()
                    : snapshots.ToArray();
                var cutoffUtc = DateTimeOffset.UtcNow.AddMinutes(-staleAfterMinutes);
                var matchedSnapshots = visibleSnapshots
                    .Where(snapshot => IsStaleSnapshot(snapshot, cutoffUtc))
                    .OrderBy(snapshot => snapshot.SessionId, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                var dryRun = request?.DryRun ?? false;
                var reason = string.IsNullOrWhiteSpace(request?.Reason)
                    ? $"bulk stale-room cleanup by {caller.EffectivePrincipalId ?? "system"}"
                    : request!.Reason.Trim();

                var closedSessionIds = new List<string>(matchedSnapshots.Length);
                var skippedSessionIds = new List<string>();
                var errors = new List<string>();
                if (!dryRun)
                {
                    foreach (var snapshot in matchedSnapshots)
                    {
                        try
                        {
                            await orchestrator.CloseAsync(new SessionCloseBody(snapshot.SessionId, reason), ct);
                            closedSessionIds.Add(snapshot.SessionId);
                        }
                        catch (KeyNotFoundException)
                        {
                            skippedSessionIds.Add(snapshot.SessionId);
                        }
                        catch (Exception ex)
                        {
                            errors.Add($"{snapshot.SessionId}: {ex.Message}");
                        }
                    }
                }

                return Results.Ok(new SessionBulkCloseResultBody(
                    Action: "close-stale",
                    DryRun: dryRun,
                    Reason: reason,
                    GeneratedAtUtc: DateTimeOffset.UtcNow,
                    ScannedSessions: visibleSnapshots.Length,
                    MatchedSessions: matchedSnapshots.Length,
                    ClosedSessions: closedSessionIds.Count,
                    SkippedSessions: skippedSessionIds.Count,
                    FailedClosures: errors.Count,
                    StaleAfterMinutes: staleAfterMinutes,
                    MatchedSessionIds: matchedSnapshots.Select(snapshot => snapshot.SessionId).ToArray(),
                    ClosedSessionIds: closedSessionIds.ToArray(),
                    SkippedSessionIds: skippedSessionIds.ToArray(),
                    Errors: errors.ToArray()));
            }
            catch (UnauthorizedAccessException ex)
            {
                return ApiAuthorizationResults.Forbidden(caller, ex);
            }
        })
            .Accepts<CloseStaleSessionsActionBody>("application/json")
            .Produces<SessionBulkCloseResultBody>(StatusCodes.Status200OK)
            .WithSummary("Close stale sessions visible to the caller.")
            .WithDescription("Closes sessions older than the supplied staleness threshold, excluding currently active sessions.");

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

        sessionGroup.MapPost("/{sessionId}/close", async (
            string sessionId,
            HttpContext httpContext,
            ISessionOrchestrator orchestrator,
            ISessionStore sessionStore,
            SessionCloseBody request,
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

                var normalized = request with { SessionId = sessionId };
                return Results.Ok(await orchestrator.CloseAsync(normalized, ct));
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
        .Accepts<SessionCloseBody>("application/json")
        .Produces<SessionCloseBody>(StatusCodes.Status200OK)
        .WithSummary("Close one session and release its endpoint.")
        .WithDescription("Closes the specified session and persists one close audit event. The route sessionId wins over any SessionId value in the request body.");

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

    private static bool IsStaleSnapshot(SessionSnapshot snapshot, DateTimeOffset cutoffUtc)
    {
        if (snapshot.State == SessionState.Active)
        {
            return false;
        }

        var stalePivot = snapshot.LastHeartbeatAtUtc ?? snapshot.UpdatedAtUtc;
        return stalePivot <= cutoffUtc;
    }
}

/// <summary>
/// Represents one bulk close request for failed sessions.
/// </summary>
public sealed record CloseFailedSessionsActionBody(
    bool DryRun = false,
    string? Reason = null);

/// <summary>
/// Represents one bulk close request for stale sessions.
/// </summary>
public sealed record CloseStaleSessionsActionBody(
    bool DryRun = false,
    string? Reason = null,
    int StaleAfterMinutes = 30);

/// <summary>
/// Represents one bulk session-close summary.
/// </summary>
public sealed record SessionBulkCloseResultBody(
    string Action,
    bool DryRun,
    string Reason,
    DateTimeOffset GeneratedAtUtc,
    int ScannedSessions,
    int MatchedSessions,
    int ClosedSessions,
    int SkippedSessions,
    int FailedClosures,
    int? StaleAfterMinutes,
    IReadOnlyList<string> MatchedSessionIds,
    IReadOnlyList<string> ClosedSessionIds,
    IReadOnlyList<string> SkippedSessionIds,
    IReadOnlyList<string> Errors);

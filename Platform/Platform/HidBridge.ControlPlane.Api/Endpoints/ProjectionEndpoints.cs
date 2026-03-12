using HidBridge.Abstractions;
using HidBridge.Contracts;

namespace HidBridge.ControlPlane.Api.Endpoints;

/// <summary>
/// Registers filterable and paged optimized projection endpoints.
/// </summary>
public static class ProjectionEndpoints
{
    /// <summary>
    /// Maps optimized projection endpoints onto the API route table.
    /// </summary>
    /// <param name="endpoints">The route builder to extend.</param>
    /// <returns>The same route builder for chaining.</returns>
    public static IEndpointRouteBuilder MapProjectionEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/projections")
            .WithTags(ApiEndpointTags.Projections);

        group.MapGet("/sessions", async (
            SessionState? state,
            string? agentId,
            string? endpointId,
            string? principalId,
            int? skip,
            int? take,
            HttpContext httpContext,
            ProjectionQueryService service,
            CancellationToken ct) =>
        {
            var caller = ApiCallerContext.FromHttpContext(httpContext);
            try
            {
                caller.EnsureViewerAccess();
                var page = await service.QuerySessionsAsync(
                    state,
                    agentId,
                    endpointId,
                    principalId,
                    caller,
                    skip ?? 0,
                    take ?? 50,
                    ct);
                return Results.Ok(page);
            }
            catch (UnauthorizedAccessException ex)
            {
                return ApiAuthorizationResults.Forbidden(caller, ex);
            }
        })
        .Produces<ProjectionPage<SessionProjectionItemReadModel>>(StatusCodes.Status200OK)
        .WithSummary("Query session projections.")
        .WithDescription("Returns a paged, filterable session projection optimized for operator consoles. Supports filtering by session state, agent, endpoint, and principal.");

        group.MapGet("/endpoints", async (
            string? status,
            string? agentId,
            ConnectorType? connectorType,
            bool? activeOnly,
            int? skip,
            int? take,
            HttpContext httpContext,
            ProjectionQueryService service,
            CancellationToken ct) =>
        {
            var caller = ApiCallerContext.FromHttpContext(httpContext);
            try
            {
                caller.EnsureViewerAccess();
                var page = await service.QueryEndpointsAsync(
                    status,
                    agentId,
                    connectorType,
                    activeOnly,
                    caller,
                    skip ?? 0,
                    take ?? 50,
                    ct);
                return Results.Ok(page);
            }
            catch (UnauthorizedAccessException ex)
            {
                return ApiAuthorizationResults.Forbidden(caller, ex);
            }
        })
        .Produces<ProjectionPage<EndpointProjectionItemReadModel>>(StatusCodes.Status200OK)
        .WithSummary("Query endpoint projections.")
        .WithDescription("Returns a paged, filterable endpoint projection optimized for fleet views. Supports filtering by resolved status, agent, connector type, and active-session presence.");

        group.MapGet("/audit", async (
            string? category,
            string? sessionId,
            string? principalId,
            DateTimeOffset? sinceUtc,
            int? skip,
            int? take,
            HttpContext httpContext,
            ISessionStore sessionStore,
            ProjectionQueryService service,
            CancellationToken ct) =>
        {
            var caller = ApiCallerContext.FromHttpContext(httpContext);
            try
            {
                caller.EnsureViewerAccess();
                if (!string.IsNullOrWhiteSpace(sessionId) && caller.IsPresent)
                {
                    await caller.RequireScopedSessionAsync(sessionStore, sessionId, ct);
                }

                var page = await service.QueryAuditAsync(
                    category,
                    sessionId,
                    principalId,
                    sinceUtc,
                    caller,
                    skip ?? 0,
                    take ?? 100,
                    ct);
                return Results.Ok(page);
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
        .Produces<ProjectionPage<AuditProjectionItemReadModel>>(StatusCodes.Status200OK)
        .WithSummary("Query audit projections.")
        .WithDescription("Returns a paged, filterable audit feed optimized for operator review. Supports filtering by category, session, principal, and lower time bound.");

        group.MapGet("/telemetry", async (
            string? scope,
            string? sessionId,
            string? metricName,
            DateTimeOffset? sinceUtc,
            int? skip,
            int? take,
            HttpContext httpContext,
            ISessionStore sessionStore,
            ProjectionQueryService service,
            CancellationToken ct) =>
        {
            var caller = ApiCallerContext.FromHttpContext(httpContext);
            try
            {
                caller.EnsureViewerAccess();
                if (!string.IsNullOrWhiteSpace(sessionId) && caller.IsPresent)
                {
                    await caller.RequireScopedSessionAsync(sessionStore, sessionId, ct);
                }

                var page = await service.QueryTelemetryAsync(
                    scope,
                    sessionId,
                    metricName,
                    sinceUtc,
                    caller,
                    skip ?? 0,
                    take ?? 100,
                    ct);
                return Results.Ok(page);
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
        .Produces<ProjectionPage<TelemetryProjectionItemReadModel>>(StatusCodes.Status200OK)
        .WithSummary("Query telemetry projections.")
        .WithDescription("Returns a paged, filterable telemetry feed optimized for diagnostics and trend widgets. Supports filtering by scope, session, metric name, and lower time bound.");

        return endpoints;
    }
}

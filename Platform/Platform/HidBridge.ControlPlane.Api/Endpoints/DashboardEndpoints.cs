using HidBridge.ControlPlane.Api;

namespace HidBridge.ControlPlane.Api.Endpoints;

/// <summary>
/// Registers fleet, audit, and telemetry dashboard endpoints.
/// </summary>
public static class DashboardEndpoints
{
    /// <summary>
    /// Maps dashboard endpoints onto the API route table.
    /// </summary>
    /// <param name="endpoints">The route builder to extend.</param>
    /// <returns>The same route builder for chaining.</returns>
    public static IEndpointRouteBuilder MapDashboardEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/v1/dashboards")
            .WithTags(ApiEndpointTags.Dashboards);

        group.MapGet("/inventory", async (
            HttpContext httpContext,
            OperationsDashboardReadModelService service,
            CancellationToken ct) =>
        {
            var caller = ApiCallerContext.FromHttpContext(httpContext);
            try
            {
                caller.EnsureViewerAccess();
                return Results.Ok(await service.GetInventoryDashboardAsync(caller, ct));
            }
            catch (UnauthorizedAccessException ex)
            {
                return ApiAuthorizationResults.Forbidden(caller, ex);
            }
        })
        .Produces<InventoryDashboardReadModel>(StatusCodes.Status200OK)
        .WithSummary("Read the fleet inventory dashboard.")
        .WithDescription("Returns one operator-oriented fleet dashboard with agent totals, endpoint cards, connector type buckets, active session counts, and control-lease totals.");

        group.MapGet("/audit", async (
            int? take,
            HttpContext httpContext,
            OperationsDashboardReadModelService service,
            CancellationToken ct) =>
        {
            var caller = ApiCallerContext.FromHttpContext(httpContext);
            try
            {
                caller.EnsureViewerAccess();
                return Results.Ok(await service.GetAuditDashboardAsync(caller, take ?? 50, ct));
            }
            catch (UnauthorizedAccessException ex)
            {
                return ApiAuthorizationResults.Forbidden(caller, ex);
            }
        })
        .Produces<AuditDashboardReadModel>(StatusCodes.Status200OK)
        .WithSummary("Read the audit diagnostics dashboard.")
        .WithDescription("Returns aggregated audit diagnostics including category buckets, per-session audit counts, and a recent audit feed. Use the optional take query parameter to control how many recent entries are returned.");

        group.MapGet("/telemetry", async (
            int? take,
            HttpContext httpContext,
            OperationsDashboardReadModelService service,
            CancellationToken ct) =>
        {
            var caller = ApiCallerContext.FromHttpContext(httpContext);
            try
            {
                caller.EnsureViewerAccess();
                return Results.Ok(await service.GetTelemetryDashboardAsync(caller, take ?? 50, ct));
            }
            catch (UnauthorizedAccessException ex)
            {
                return ApiAuthorizationResults.Forbidden(caller, ex);
            }
        })
        .Produces<TelemetryDashboardReadModel>(StatusCodes.Status200OK)
        .WithSummary("Read the telemetry diagnostics dashboard.")
        .WithDescription("Returns aggregated telemetry diagnostics including scope buckets, latest signal projections, and session-level health cards. Use the optional take query parameter to control how many signals and session cards are returned.");

        return endpoints;
    }
}

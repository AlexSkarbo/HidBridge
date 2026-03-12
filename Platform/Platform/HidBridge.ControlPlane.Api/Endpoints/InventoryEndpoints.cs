using HidBridge.Abstractions;

namespace HidBridge.ControlPlane.Api.Endpoints;

/// <summary>
/// Registers inventory endpoints for agents and endpoints.
/// </summary>
public static class InventoryEndpoints
{
    /// <summary>
    /// Maps inventory endpoint groups onto the API route table.
    /// </summary>
    public static IEndpointRouteBuilder MapInventoryEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var inventoryGroup = endpoints.MapGroup("/api/v1")
            .WithTags(ApiEndpointTags.Inventory);

        inventoryGroup.MapGet("/agents", async (HttpContext httpContext, IConnectorRegistry registry, CancellationToken ct) =>
        {
            var caller = ApiCallerContext.FromHttpContext(httpContext);
            try
            {
                caller.EnsureViewerAccess();
                return Results.Ok(await registry.ListAsync(ct));
            }
            catch (UnauthorizedAccessException ex)
            {
                return ApiAuthorizationResults.Forbidden(caller, ex);
            }
        })
            .WithSummary("List registered agents.")
            .WithDescription("Returns connector descriptors for the agents currently registered during API startup.");

        inventoryGroup.MapGet("/endpoints", async (HttpContext httpContext, IEndpointSnapshotStore endpointStore, CancellationToken ct) =>
        {
            var caller = ApiCallerContext.FromHttpContext(httpContext);
            try
            {
                caller.EnsureViewerAccess();
                return Results.Ok(await endpointStore.ListAsync(ct));
            }
            catch (UnauthorizedAccessException ex)
            {
                return ApiAuthorizationResults.Forbidden(caller, ex);
            }
        })
            .WithSummary("List known endpoints.")
            .WithDescription("Returns the persisted endpoint snapshots currently known to the Platform, including capabilities and last update time.");

        return endpoints;
    }
}

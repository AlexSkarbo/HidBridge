using System.Text;
using System.Text.Json;
using HidBridge.ControlPlane.Web.Services;

namespace HidBridge.ControlPlane.Web;

/// <summary>
/// Maps export endpoints for operator policy governance screens.
/// </summary>
public static class PolicyExportEndpointExtensions
{
    /// <summary>
    /// Maps policy export endpoints onto the web shell.
    /// </summary>
    public static IEndpointRouteBuilder MapPolicyExportEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/exports/policies/revisions", async (
            string? scopeId,
            string? entityType,
            string? principalId,
            int? take,
            ControlPlaneApiClient apiClient,
            CancellationToken ct) =>
        {
            var revisions = await apiClient.GetPolicyRevisionsAsync(scopeId, entityType, principalId, take ?? 200, ct) ?? [];
            var json = JsonSerializer.Serialize(revisions, new JsonSerializerOptions(JsonSerializerDefaults.Web)
            {
                WriteIndented = true,
            });
            var bytes = Encoding.UTF8.GetBytes(json);
            return Results.File(bytes, "application/json", $"policy-revisions-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.json");
        })
        .RequireAuthorization(Identity.OperatorPolicies.Viewer)
        .WithSummary("Exports filtered policy revision snapshots.")
        .WithDescription("Returns a JSON export of filtered policy revision snapshots through the authenticated web shell.");

        return endpoints;
    }
}

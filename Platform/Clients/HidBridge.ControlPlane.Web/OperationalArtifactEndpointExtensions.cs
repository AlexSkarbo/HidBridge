using HidBridge.ControlPlane.Web.Services;

namespace HidBridge.ControlPlane.Web;

/// <summary>
/// Maps local operational artifact download endpoints.
/// </summary>
public static class OperationalArtifactEndpointExtensions
{
    /// <summary>
    /// Maps operational artifact endpoints.
    /// </summary>
    public static IEndpointRouteBuilder MapOperationalArtifactEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/ops-artifacts/file", (string path, OperationalArtifactService artifacts) =>
        {
            var fullPath = artifacts.ResolveSafePath(path);
            return Results.File(fullPath, "text/plain; charset=utf-8", enableRangeProcessing: false);
        });

        return endpoints;
    }
}

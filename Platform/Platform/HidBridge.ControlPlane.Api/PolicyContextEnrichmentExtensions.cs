using HidBridge.Abstractions;

namespace HidBridge.ControlPlane.Api;

/// <summary>
/// Adds shared operator policy enrichment to incoming API requests.
/// </summary>
public static class PolicyContextEnrichmentExtensions
{
    /// <summary>
    /// Resolves persisted policy assignments and projects the effective caller context into the request.
    /// </summary>
    public static IApplicationBuilder UsePolicyContextEnrichment(this IApplicationBuilder app)
    {
        return app.Use(async (httpContext, next) =>
        {
            var rawCaller = ApiCallerContext.FromHttpContext(httpContext);
            if (!rawCaller.IsPresent)
            {
                await next();
                return;
            }

            var policyService = httpContext.RequestServices.GetRequiredService<IOperatorPolicyService>();
            var resolution = await policyService.ResolveAsync(
                rawCaller.EffectivePrincipalId,
                rawCaller.TenantId,
                rawCaller.OrganizationId,
                rawCaller.OperatorRoles,
                httpContext.RequestAborted);

            httpContext.Items[ApiCallerContext.ItemKey] = rawCaller.Apply(resolution);
            await next();
        });
    }
}

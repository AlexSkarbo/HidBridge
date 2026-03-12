using HidBridge.ControlPlane.Web.Configuration;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;

namespace HidBridge.ControlPlane.Web.Identity;

/// <summary>
/// Maps authentication helper endpoints for the operator web shell.
/// </summary>
public static class IdentityEndpointExtensions
{
    /// <summary>
    /// Maps login, logout, and identity inspection endpoints.
    /// </summary>
    /// <param name="app">The route builder.</param>
    public static IEndpointRouteBuilder MapIdentityEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/auth/login", async (HttpContext context, IOptions<IdentityOptions> options, string? returnUrl) =>
        {
            var destination = string.IsNullOrWhiteSpace(returnUrl) ? "/" : returnUrl;
            if (!options.Value.Enabled)
            {
                context.Response.Redirect(destination);
                return;
            }

            await context.ChallengeAsync(OpenIdConnectDefaults.AuthenticationScheme, new AuthenticationProperties { RedirectUri = destination });
        });

        app.MapGet("/auth/logout", async (HttpContext context, IOptions<IdentityOptions> options) =>
        {
            if (options.Value.Enabled)
            {
                var authenticationResult = await context.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                var properties = new AuthenticationProperties { RedirectUri = "/" };
                var tokens = authenticationResult.Properties?.GetTokens()?.ToArray();
                if (tokens is { Length: > 0 })
                {
                    properties.StoreTokens(tokens);
                }

                await context.SignOutAsync(OpenIdConnectDefaults.AuthenticationScheme, properties);
                await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                return;
            }

            context.Response.Redirect("/");
        });

        app.MapGet("/auth/me", (OperatorIdentityContext identity) => Results.Ok(identity));
        app.MapGet("/auth/status", (OperatorAuthDescriptor descriptor, OperatorIdentityContext identity) => Results.Ok(new
        {
            descriptor.ExternalIdentityEnabled,
            descriptor.ProviderDisplayName,
            descriptor.Mode,
            descriptor.CallbackPath,
            descriptor.SignedOutCallbackPath,
            identity.IsAuthenticated,
            identity.DisplayName,
            identity.PrincipalId,
            identity.TenantId,
            identity.OrganizationId,
            identity.Roles,
        }));

        return app;
    }
}

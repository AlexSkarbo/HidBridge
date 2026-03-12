using Microsoft.AspNetCore.Localization;

namespace HidBridge.ControlPlane.Web.Localization;

/// <summary>
/// Registers thin-shell culture selection endpoints.
/// </summary>
public static class CultureEndpointExtensions
{
    private static readonly HashSet<string> SupportedCultureCodes = ["en", "uk"];

    /// <summary>
    /// Maps the culture-selection endpoint used by the settings page.
    /// </summary>
    /// <param name="endpoints">The route builder.</param>
    public static IEndpointRouteBuilder MapCultureEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/culture/set", (HttpContext httpContext, string culture, string? redirectUri) =>
        {
            var normalizedCulture = SupportedCultureCodes.Contains(culture) ? culture : "en";
            httpContext.Response.Cookies.Append(
                CookieRequestCultureProvider.DefaultCookieName,
                CookieRequestCultureProvider.MakeCookieValue(new RequestCulture(normalizedCulture)),
                new CookieOptions
                {
                    Expires = DateTimeOffset.UtcNow.AddYears(1),
                    IsEssential = true,
                    HttpOnly = false,
                    SameSite = SameSiteMode.Lax,
                });

            var target = string.IsNullOrWhiteSpace(redirectUri) || !redirectUri.StartsWith("/", StringComparison.Ordinal)
                ? "/settings"
                : redirectUri;

            return Results.LocalRedirect(target);
        })
        .WithName("SetCulture")
        .WithSummary("Sets the UI culture cookie for the thin operator shell.")
        .WithDescription("Stores a manual language preference. If the cookie is absent, the browser locale is used.");

        endpoints.MapGet("/culture/clear", (HttpContext httpContext, string? redirectUri) =>
        {
            httpContext.Response.Cookies.Delete(CookieRequestCultureProvider.DefaultCookieName);
            var target = string.IsNullOrWhiteSpace(redirectUri) || !redirectUri.StartsWith("/", StringComparison.Ordinal)
                ? "/settings"
                : redirectUri;

            return Results.LocalRedirect(target);
        })
        .WithName("ClearCulture")
        .WithSummary("Clears the manual UI culture cookie.")
        .WithDescription("Returns the thin operator shell to browser-locale auto-detection.");

        return endpoints;
    }
}

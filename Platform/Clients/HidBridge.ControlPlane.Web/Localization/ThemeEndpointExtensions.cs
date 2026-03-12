using Microsoft.AspNetCore.Http;

namespace HidBridge.ControlPlane.Web.Localization;

/// <summary>
/// Maps theme preference endpoints used by the operator web shell.
/// </summary>
public static class ThemeEndpointExtensions
{
    private const string ThemeCookieName = "hidbridge_theme";

    /// <summary>
    /// Maps endpoints that persist or clear the manual theme preference.
    /// </summary>
    /// <param name="app">The route builder.</param>
    public static IEndpointRouteBuilder MapThemeEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/theme/set", (HttpContext httpContext, string theme, string? redirectUri) =>
        {
            var normalized = NormalizeTheme(theme);
            if (normalized is null)
            {
                return Results.BadRequest(new { error = "Unsupported theme. Use auto, light, or dark." });
            }

            if (normalized == "auto")
            {
                httpContext.Response.Cookies.Delete(ThemeCookieName);
            }
            else
            {
                httpContext.Response.Cookies.Append(ThemeCookieName, normalized, new CookieOptions
                {
                    HttpOnly = false,
                    IsEssential = true,
                    SameSite = SameSiteMode.Lax,
                    Secure = httpContext.Request.IsHttps,
                    MaxAge = TimeSpan.FromDays(365),
                });
            }

            return Results.LocalRedirect(NormalizeRedirectUri(redirectUri));
        });

        app.MapGet("/theme/clear", (HttpContext httpContext, string? redirectUri) =>
        {
            httpContext.Response.Cookies.Delete(ThemeCookieName);
            return Results.LocalRedirect(NormalizeRedirectUri(redirectUri));
        });

        return app;
    }

    private static string NormalizeRedirectUri(string? redirectUri)
    {
        if (string.IsNullOrWhiteSpace(redirectUri))
        {
            return "/settings";
        }

        return redirectUri.StartsWith("/", StringComparison.Ordinal) ? redirectUri : "/settings";
    }

    private static string? NormalizeTheme(string? theme)
        => theme?.ToLowerInvariant() switch
        {
            "auto" => "auto",
            "light" => "light",
            "dark" => "dark",
            _ => null,
        };
}

namespace HidBridge.ControlPlane.Api;

/// <summary>
/// Enforces authenticated access for protected API routes when bearer auth is enabled.
/// </summary>
public static class ApiAuthenticationGuardExtensions
{
    /// <summary>
    /// Adds the API authentication guard middleware.
    /// </summary>
    public static IApplicationBuilder UseApiAuthenticationGuard(this IApplicationBuilder app, ApiAuthenticationOptions options)
        => app.Use(async (httpContext, next) =>
        {
            if (!options.Enabled || !IsProtectedApiPath(httpContext.Request.Path))
            {
                await next();
                return;
            }

            var isAuthenticated = httpContext.User.Identity?.IsAuthenticated == true;
            var isUnsafeMethod = IsUnsafeMethod(httpContext.Request.Method);
            var requiresBearerOnly = options.BearerOnlyPrefixes.Any(prefix =>
                httpContext.Request.Path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase));
            var requiresCallerContext = isUnsafeMethod
                && options.CallerContextRequiredPrefixes.Any(prefix =>
                    httpContext.Request.Path.StartsWithSegments(prefix, StringComparison.OrdinalIgnoreCase));
            var allowHeaderFallbackForRequest = options.AllowHeaderFallback
                && (!isUnsafeMethod || !options.HeaderFallbackDisabledPatterns.Any(pattern =>
                    PathMatchesPattern(httpContext.Request.Path, pattern)));
            var caller = ApiCallerContext.FromHttpContext(httpContext, allowHeaderFallbackForRequest);
            httpContext.Items[ApiCallerContext.ItemKey] = caller;

            if (isAuthenticated || (!requiresBearerOnly && allowHeaderFallbackForRequest && caller.IsPresent))
            {
                if (!requiresCallerContext || caller.IsPresent || (!requiresBearerOnly && allowHeaderFallbackForRequest && caller.IsPresent))
                {
                    await next();
                    return;
                }

                var contextRequiredException = new ApiAuthorizationException(
                    code: "caller_context_required",
                    message: allowHeaderFallbackForRequest
                        ? "Caller must project principal, scope, and operator-role context through bearer claims or approved fallback headers before invoking this mutation path."
                        : "Caller must project principal, scope, and operator-role context through bearer claims before invoking this mutation path.");
                await ApiAuthorizationResults.Forbidden(caller, contextRequiredException).ExecuteAsync(httpContext);
                return;
            }

            var exception = new ApiAuthorizationException(
                code: "authentication_required",
                message: requiresBearerOnly
                    ? "Caller must authenticate with a bearer token before invoking this protected API path."
                    : "Caller must authenticate with a bearer token or an approved caller-scope header set before invoking this API.");
            await ApiAuthorizationResults.Unauthorized(caller, exception).ExecuteAsync(httpContext);
        });

    private static bool IsProtectedApiPath(PathString path)
        => path.StartsWithSegments("/api/v1", StringComparison.OrdinalIgnoreCase);

    private static bool IsUnsafeMethod(string method)
        => HttpMethods.IsPost(method)
        || HttpMethods.IsPut(method)
        || HttpMethods.IsPatch(method)
        || HttpMethods.IsDelete(method);

    private static bool PathMatchesPattern(PathString path, string pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern))
        {
            return false;
        }

        var pathSegments = path.Value?
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            ?? Array.Empty<string>();
        var patternSegments = pattern
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (pathSegments.Length != patternSegments.Length)
        {
            return false;
        }

        for (var index = 0; index < patternSegments.Length; index++)
        {
            var expected = patternSegments[index];
            if (string.Equals(expected, "*", StringComparison.Ordinal))
            {
                continue;
            }

            if (!string.Equals(pathSegments[index], expected, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }
}

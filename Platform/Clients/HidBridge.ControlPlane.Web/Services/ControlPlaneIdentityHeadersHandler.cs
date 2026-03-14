using System.Net.Http.Headers;
using System.Net;
using System.Globalization;
using System.Text.Json;
using HidBridge.ControlPlane.Web.Configuration;
using HidBridge.ControlPlane.Web.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HidBridge.ControlPlane.Web.Services;

/// <summary>
/// Propagates the current operator identity and access token to the backend API.
/// </summary>
public sealed class ControlPlaneIdentityHeadersHandler : DelegatingHandler
{
    private const string SubjectIdHeader = "X-HidBridge-UserId";
    private const string PrincipalIdHeader = "X-HidBridge-PrincipalId";
    private const string TenantIdHeader = "X-HidBridge-TenantId";
    private const string OrganizationIdHeader = "X-HidBridge-OrganizationId";
    private const string RoleHeader = "X-HidBridge-Role";

    private readonly OperatorIdentityContext _identity;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ControlPlaneApiOptions _options;
    private readonly IdentityOptions _identityOptions;
    private readonly IOptionsMonitor<OpenIdConnectOptions> _openIdConnectOptions;
    private readonly ILogger<ControlPlaneIdentityHeadersHandler> _logger;
    private static readonly TimeSpan AccessTokenRefreshSkew = TimeSpan.FromMinutes(3);

    /// <summary>
    /// Creates the identity propagation handler.
    /// </summary>
    public ControlPlaneIdentityHeadersHandler(
        OperatorIdentityContext identity,
        IHttpContextAccessor httpContextAccessor,
        IOptions<ControlPlaneApiOptions> options,
        IOptions<IdentityOptions> identityOptions,
        IOptionsMonitor<OpenIdConnectOptions> openIdConnectOptions,
        ILogger<ControlPlaneIdentityHeadersHandler> logger)
    {
        _identity = identity;
        _httpContextAccessor = httpContextAccessor;
        _options = options.Value;
        _identityOptions = identityOptions.Value;
        _openIdConnectOptions = openIdConnectOptions;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.Remove(SubjectIdHeader);
        request.Headers.Remove(PrincipalIdHeader);
        request.Headers.Remove(TenantIdHeader);
        request.Headers.Remove(OrganizationIdHeader);
        request.Headers.Remove(RoleHeader);
        request.Headers.Authorization = null;

        if (_options.PropagateAccessToken)
        {
            var accessToken = await ResolveAccessTokenAsync();
            if (!string.IsNullOrWhiteSpace(accessToken))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            }
        }

        if (_options.PropagateIdentityHeaders && _identity.IsAuthenticated)
        {
            AddHeader(request, SubjectIdHeader, _identity.SubjectId);
            AddHeader(request, PrincipalIdHeader, _identity.PrincipalId);
            AddHeader(request, TenantIdHeader, _identity.TenantId);
            AddHeader(request, OrganizationIdHeader, _identity.OrganizationId);
            if (_identity.OperatorRoles.Count > 0)
            {
                AddHeader(request, RoleHeader, string.Join(",", _identity.OperatorRoles));
            }
        }

        try
        {
            return await base.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException exception)
        {
            return CreateUnavailableResponse(request, exception.Message);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            return CreateUnavailableResponse(request, exception.Message);
        }
    }

    private async Task<string?> ResolveAccessTokenAsync()
    {
        var context = _httpContextAccessor.HttpContext;
        if (context is null)
        {
            return null;
        }

        var cookieAuthResult = await context.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        var accessToken = cookieAuthResult.Properties?.GetTokenValue("access_token")
            ?? await context.GetTokenAsync(CookieAuthenticationDefaults.AuthenticationScheme, "access_token")
            ?? await context.GetTokenAsync(OpenIdConnectDefaults.AuthenticationScheme, "access_token")
            ?? await context.GetTokenAsync("access_token");

        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return null;
        }

        if (!ShouldRefreshAccessToken(cookieAuthResult.Properties, accessToken))
        {
            return accessToken;
        }

        if (!_identityOptions.Enabled)
        {
            return accessToken;
        }

        var refreshToken = cookieAuthResult.Properties?.GetTokenValue("refresh_token");
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            return accessToken;
        }

        var refreshedAccessToken = await TryRefreshAccessTokenAsync(
            context,
            cookieAuthResult.Principal,
            cookieAuthResult.Properties,
            refreshToken,
            context.RequestAborted);

        return string.IsNullOrWhiteSpace(refreshedAccessToken) ? accessToken : refreshedAccessToken;
    }

    private static bool ShouldRefreshAccessToken(AuthenticationProperties? properties, string accessToken)
    {
        var expiresAtUtc = DateTimeOffset.MinValue;
        var hasExpiry = false;
        if (properties is not null)
        {
            var expiresAtRaw = properties.GetTokenValue("expires_at");
            hasExpiry = DateTimeOffset.TryParse(expiresAtRaw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out expiresAtUtc);
        }

        if (!hasExpiry)
        {
            hasExpiry = TryReadExpiresAtFromJwt(accessToken, out expiresAtUtc);
        }

        if (!hasExpiry)
        {
            // When expiry cannot be resolved reliably, keep current token and let API answer.
            return false;
        }

        return expiresAtUtc <= DateTimeOffset.UtcNow.Add(AccessTokenRefreshSkew);
    }

    private static bool TryReadExpiresAtFromJwt(string accessToken, out DateTimeOffset expiresAtUtc)
    {
        expiresAtUtc = default;
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return false;
        }

        var tokenParts = accessToken.Split('.');
        if (tokenParts.Length < 2)
        {
            return false;
        }

        try
        {
            var payload = tokenParts[1]
                .Replace('-', '+')
                .Replace('_', '/');
            payload = payload.PadRight(payload.Length + ((4 - payload.Length % 4) % 4), '=');
            var payloadBytes = Convert.FromBase64String(payload);
            using var payloadDocument = JsonDocument.Parse(payloadBytes);
            if (!payloadDocument.RootElement.TryGetProperty("exp", out var expElement))
            {
                return false;
            }

            var expValue = expElement.ValueKind switch
            {
                JsonValueKind.Number when expElement.TryGetInt64(out var parsedExp) => parsedExp,
                JsonValueKind.String when long.TryParse(expElement.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedExp) => parsedExp,
                _ => 0L,
            };
            if (expValue <= 0)
            {
                return false;
            }

            expiresAtUtc = DateTimeOffset.FromUnixTimeSeconds(expValue);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task<string?> TryRefreshAccessTokenAsync(
        HttpContext httpContext,
        System.Security.Claims.ClaimsPrincipal? principal,
        AuthenticationProperties? properties,
        string refreshToken,
        CancellationToken cancellationToken)
    {
        if (properties is null || principal is null)
        {
            return null;
        }

        var tokenEndpoint = await ResolveTokenEndpointAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(tokenEndpoint))
        {
            return null;
        }

        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        using var response = await httpClient.PostAsync(
            tokenEndpoint,
            new FormUrlEncodedContent(BuildRefreshTokenForm(refreshToken)),
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning("OIDC refresh_token exchange failed with status {StatusCode}.", (int)response.StatusCode);
            return null;
        }

        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        using var document = JsonDocument.Parse(payload);
        if (!document.RootElement.TryGetProperty("access_token", out var refreshedAccessTokenElement))
        {
            return null;
        }

        var refreshedAccessToken = refreshedAccessTokenElement.GetString();
        if (string.IsNullOrWhiteSpace(refreshedAccessToken))
        {
            return null;
        }

        var newRefreshToken = document.RootElement.TryGetProperty("refresh_token", out var refreshedTokenElement)
            ? refreshedTokenElement.GetString()
            : null;
        var newIdToken = document.RootElement.TryGetProperty("id_token", out var idTokenElement)
            ? idTokenElement.GetString()
            : null;
        var expiresInSeconds = document.RootElement.TryGetProperty("expires_in", out var expiresInElement)
            && expiresInElement.TryGetInt32(out var parsedExpiresIn)
            ? parsedExpiresIn
            : 300;

        var tokens = properties.GetTokens().ToDictionary(token => token.Name, token => token.Value, StringComparer.Ordinal);
        tokens["access_token"] = refreshedAccessToken;
        tokens["refresh_token"] = string.IsNullOrWhiteSpace(newRefreshToken) ? refreshToken : newRefreshToken!;
        if (!string.IsNullOrWhiteSpace(newIdToken))
        {
            tokens["id_token"] = newIdToken!;
        }

        tokens["expires_at"] = DateTimeOffset.UtcNow
            .AddSeconds(expiresInSeconds)
            .ToString("o", CultureInfo.InvariantCulture);

        properties.StoreTokens(tokens.Select(static pair => new AuthenticationToken { Name = pair.Key, Value = pair.Value }));

        // In interactive Blazor rendering the HTTP response may already be started.
        // Persisting refreshed tokens into auth cookie is best-effort in that case.
        if (httpContext.Response.HasStarted)
        {
            _logger.LogWarning("Skipping cookie SignIn after token refresh because response has already started.");
            return refreshedAccessToken;
        }

        try
        {
            await httpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal, properties);
        }
        catch (InvalidOperationException exception) when (httpContext.Response.HasStarted)
        {
            _logger.LogWarning(exception, "Skipping cookie SignIn after token refresh because response headers are read-only.");
        }

        return refreshedAccessToken;
    }

    private async Task<string?> ResolveTokenEndpointAsync(CancellationToken cancellationToken)
    {
        try
        {
            var oidcOptions = _openIdConnectOptions.Get(OpenIdConnectDefaults.AuthenticationScheme);
            if (oidcOptions.ConfigurationManager is not null)
            {
                var configuration = await oidcOptions.ConfigurationManager.GetConfigurationAsync(cancellationToken);
                if (!string.IsNullOrWhiteSpace(configuration.TokenEndpoint))
                {
                    return configuration.TokenEndpoint;
                }
            }

            if (!string.IsNullOrWhiteSpace(_identityOptions.Authority))
            {
                return $"{_identityOptions.Authority.TrimEnd('/')}/protocol/openid-connect/token";
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed to resolve OIDC token endpoint for refresh.");
        }

        return null;
    }

    private IEnumerable<KeyValuePair<string, string>> BuildRefreshTokenForm(string refreshToken)
    {
        var form = new List<KeyValuePair<string, string>>
        {
            new("grant_type", "refresh_token"),
            new("client_id", _identityOptions.ClientId ?? string.Empty),
            new("refresh_token", refreshToken),
        };

        if (!string.IsNullOrWhiteSpace(_identityOptions.ClientSecret))
        {
            form.Add(new KeyValuePair<string, string>("client_secret", _identityOptions.ClientSecret));
        }

        return form;
    }

    private static void AddHeader(HttpRequestMessage request, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)
            && !string.Equals(value, "unassigned", StringComparison.OrdinalIgnoreCase))
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }
    }

    private static HttpResponseMessage CreateUnavailableResponse(HttpRequestMessage request, string message)
        => new(HttpStatusCode.ServiceUnavailable)
        {
            RequestMessage = request,
            Content = new StringContent(message),
        };
}

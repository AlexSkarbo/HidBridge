using System.Net.Http.Headers;
using System.Net;
using HidBridge.ControlPlane.Web.Configuration;
using HidBridge.ControlPlane.Web.Identity;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
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

    /// <summary>
    /// Creates the identity propagation handler.
    /// </summary>
    public ControlPlaneIdentityHeadersHandler(
        OperatorIdentityContext identity,
        IHttpContextAccessor httpContextAccessor,
        IOptions<ControlPlaneApiOptions> options)
    {
        _identity = identity;
        _httpContextAccessor = httpContextAccessor;
        _options = options.Value;
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
        catch (TaskCanceledException exception) when (exception.InnerException is null)
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

        return await context.GetTokenAsync(CookieAuthenticationDefaults.AuthenticationScheme, "access_token")
            ?? await context.GetTokenAsync("access_token");
    }

    private static void AddHeader(HttpRequestMessage request, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
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

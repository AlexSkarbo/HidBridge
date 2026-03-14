using System.Net;
using System.Security.Claims;
using System.IO;
using HidBridge.ControlPlane.Web.Configuration;
using HidBridge.ControlPlane.Web.Identity;
using HidBridge.ControlPlane.Web.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace HidBridge.Platform.Tests;

public sealed class ControlPlaneIdentityHeadersHandlerTests
{
    [Fact]
    public async Task SendAsync_PropagatesBearerTokenAndCallerHeaders()
    {
        var identity = new OperatorIdentityContext(
            true,
            "user-1",
            "operator@example.com",
            "Operator",
            "tenant-a",
            "org-a",
            ["operator.viewer", "offline_access"],
            "OIDC",
            null);

        var httpContext = new DefaultHttpContext();
        var services = new ServiceCollection();
        services.AddSingleton<IAuthenticationService>(new FakeAuthenticationService("access-token-123"));
        httpContext.RequestServices = services.BuildServiceProvider();
        var accessor = new HttpContextAccessor { HttpContext = httpContext };
        var options = Options.Create(new ControlPlaneApiOptions { PropagateAccessToken = true, PropagateIdentityHeaders = true });
        var identityOptions = Options.Create(new IdentityOptions
        {
            Enabled = true,
            Authority = "http://127.0.0.1:18096/realms/hidbridge-dev",
            ClientId = "controlplane-web",
            ClientSecret = "secret",
        });
        var oidcOptions = new StaticOptionsMonitor<OpenIdConnectOptions>(new OpenIdConnectOptions());
        var inner = new RecordingDelegatingHandler();
        var handler = new ControlPlaneIdentityHeadersHandler(
            identity,
            accessor,
            options,
            identityOptions,
            oidcOptions,
            NullLogger<ControlPlaneIdentityHeadersHandler>.Instance)
        { InnerHandler = inner };
        using var invoker = new HttpMessageInvoker(handler);

        using var response = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "http://localhost/test"), TestContext.Current.CancellationToken);

        Assert.Equal("Bearer", inner.LastAuthorizationScheme);
        Assert.Equal("access-token-123", inner.LastAuthorizationParameter);
        Assert.Equal("user-1", inner.Headers["X-HidBridge-UserId"]);
        Assert.Equal("operator@example.com", inner.Headers["X-HidBridge-PrincipalId"]);
        Assert.Equal("tenant-a", inner.Headers["X-HidBridge-TenantId"]);
        Assert.Equal("org-a", inner.Headers["X-HidBridge-OrganizationId"]);
        Assert.Equal("operator.viewer", inner.Headers["X-HidBridge-Role"]);
    }

    [Fact]
    public async Task SendAsync_DoesNotPropagateUnassignedScopeHeaders()
    {
        var identity = new OperatorIdentityContext(
            true,
            "user-1",
            "operator@example.com",
            "Operator",
            "unassigned",
            "unassigned",
            ["operator.viewer"],
            "OIDC",
            null);

        var httpContext = new DefaultHttpContext();
        var services = new ServiceCollection();
        services.AddSingleton<IAuthenticationService>(new FakeAuthenticationService("access-token-123"));
        httpContext.RequestServices = services.BuildServiceProvider();
        var accessor = new HttpContextAccessor { HttpContext = httpContext };
        var options = Options.Create(new ControlPlaneApiOptions { PropagateAccessToken = false, PropagateIdentityHeaders = true });
        var identityOptions = Options.Create(new IdentityOptions { Enabled = true, ClientId = "controlplane-web" });
        var oidcOptions = new StaticOptionsMonitor<OpenIdConnectOptions>(new OpenIdConnectOptions());
        var inner = new RecordingDelegatingHandler();
        var handler = new ControlPlaneIdentityHeadersHandler(
            identity,
            accessor,
            options,
            identityOptions,
            oidcOptions,
            NullLogger<ControlPlaneIdentityHeadersHandler>.Instance)
        { InnerHandler = inner };
        using var invoker = new HttpMessageInvoker(handler);

        using var response = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "http://localhost/test"), TestContext.Current.CancellationToken);

        Assert.False(inner.Headers.ContainsKey("X-HidBridge-TenantId"));
        Assert.False(inner.Headers.ContainsKey("X-HidBridge-OrganizationId"));
    }

    [Fact]
    public async Task SendAsync_ReturnsServiceUnavailable_WhenTransportCancellationContainsInnerException()
    {
        var identity = new OperatorIdentityContext(
            false,
            "user-1",
            "operator@example.com",
            "Operator",
            "tenant-a",
            "org-a",
            [],
            "OIDC",
            null);

        var httpContext = new DefaultHttpContext();
        httpContext.RequestServices = new ServiceCollection().BuildServiceProvider();
        var accessor = new HttpContextAccessor { HttpContext = httpContext };
        var options = Options.Create(new ControlPlaneApiOptions { PropagateAccessToken = false, PropagateIdentityHeaders = false });
        var identityOptions = Options.Create(new IdentityOptions { Enabled = true, ClientId = "controlplane-web" });
        var oidcOptions = new StaticOptionsMonitor<OpenIdConnectOptions>(new OpenIdConnectOptions());
        var timeoutException = new TaskCanceledException(
            "The operation was canceled.",
            new IOException("Unable to read data from the transport connection."));
        var inner = new ThrowingDelegatingHandler(timeoutException);
        var handler = new ControlPlaneIdentityHeadersHandler(
            identity,
            accessor,
            options,
            identityOptions,
            oidcOptions,
            NullLogger<ControlPlaneIdentityHeadersHandler>.Instance)
        { InnerHandler = inner };
        using var invoker = new HttpMessageInvoker(handler);

        using var response = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "http://localhost/test"), CancellationToken.None);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    private sealed class FakeAuthenticationService : IAuthenticationService
    {
        private readonly string _accessToken;

        public FakeAuthenticationService(string accessToken)
        {
            _accessToken = accessToken;
        }

        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme)
        {
            var identity = new ClaimsIdentity(CookieAuthenticationDefaults.AuthenticationScheme);
            var principal = new ClaimsPrincipal(identity);
            var properties = new AuthenticationProperties();
            properties.StoreTokens([new AuthenticationToken { Name = "access_token", Value = _accessToken }]);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, properties, scheme ?? CookieAuthenticationDefaults.AuthenticationScheme)));
        }

        public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
        public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
        public Task SignInAsync(HttpContext context, string? scheme, ClaimsPrincipal principal, AuthenticationProperties? properties) => Task.CompletedTask;
        public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
    }

    private sealed class RecordingDelegatingHandler : HttpMessageHandler
    {
        public Dictionary<string, string> Headers { get; } = new(StringComparer.OrdinalIgnoreCase);
        public string? LastAuthorizationScheme { get; private set; }
        public string? LastAuthorizationParameter { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            foreach (var header in request.Headers)
            {
                Headers[header.Key] = string.Join(",", header.Value);
            }

            LastAuthorizationScheme = request.Headers.Authorization?.Scheme;
            LastAuthorizationParameter = request.Headers.Authorization?.Parameter;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private sealed class ThrowingDelegatingHandler : HttpMessageHandler
    {
        private readonly Exception _exception;

        public ThrowingDelegatingHandler(Exception exception)
        {
            _exception = exception;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromException<HttpResponseMessage>(_exception);
    }

    private sealed class StaticOptionsMonitor<T> : IOptionsMonitor<T>
    {
        private readonly T _value;

        public StaticOptionsMonitor(T value)
        {
            _value = value;
        }

        public T CurrentValue => _value;

        public T Get(string? name) => _value;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}

using HidBridge.ControlPlane.Web;
using HidBridge.ControlPlane.Web.Components;
using HidBridge.ControlPlane.Web.Configuration;
using HidBridge.ControlPlane.Web.Identity;
using HidBridge.ControlPlane.Web.Localization;
using HidBridge.ControlPlane.Web.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authentication.OpenIdConnect.Claims;
using Microsoft.AspNetCore.Localization;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using System.Globalization;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddLocalization();
builder.Services.AddHttpContextAccessor();
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddCascadingAuthenticationState();
builder.Services.Configure<RequestLocalizationOptions>(options =>
{
    var supportedCultures = new[]
    {
        new CultureInfo("en"),
        new CultureInfo("uk"),
    };

    options.DefaultRequestCulture = new RequestCulture("en");
    options.SupportedCultures = supportedCultures;
    options.SupportedUICultures = supportedCultures;
    options.RequestCultureProviders =
    [
        new CookieRequestCultureProvider(),
        new AcceptLanguageHeaderRequestCultureProvider(),
    ];
});
builder.Services.Configure<ControlPlaneApiOptions>(builder.Configuration.GetSection("ControlPlaneApi"));
builder.Services.Configure<IdentityOptions>(builder.Configuration.GetSection("Identity"));
builder.Services.AddScoped<OperatorText>();
builder.Services.AddSingleton<OperationalArtifactService>();
builder.Services.AddScoped(static serviceProvider => OperatorAuthDescriptor.Create(
    serviceProvider.GetRequiredService<IOptions<IdentityOptions>>().Value));
builder.Services.AddScoped(static serviceProvider => OperatorIdentityContext.Create(
    serviceProvider.GetRequiredService<IHttpContextAccessor>(),
    serviceProvider.GetRequiredService<IOptions<IdentityOptions>>()));
builder.Services.AddTransient<ControlPlaneIdentityHeadersHandler>();
builder.Services.AddHttpClient<ControlPlaneApiClient>((serviceProvider, httpClient) =>
{
    var options = serviceProvider
        .GetRequiredService<IOptions<ControlPlaneApiOptions>>()
        .Value;
    httpClient.BaseAddress = new Uri(options.BaseUrl, UriKind.Absolute);
    httpClient.Timeout = TimeSpan.FromSeconds(15);
})
.AddHttpMessageHandler<ControlPlaneIdentityHeadersHandler>();

var identityOptions = builder.Configuration.GetSection("Identity").Get<IdentityOptions>() ?? new IdentityOptions();
var authenticationBuilder = builder.Services.AddAuthentication(authenticationOptions =>
{
    if (identityOptions.Enabled)
    {
        authenticationOptions.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        authenticationOptions.DefaultAuthenticateScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        authenticationOptions.DefaultSignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        authenticationOptions.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
        return;
    }

    authenticationOptions.DefaultScheme = DevelopmentOperatorAuthenticationHandler.SchemeName;
    authenticationOptions.DefaultAuthenticateScheme = DevelopmentOperatorAuthenticationHandler.SchemeName;
    authenticationOptions.DefaultChallengeScheme = DevelopmentOperatorAuthenticationHandler.SchemeName;
    authenticationOptions.DefaultSignInScheme = DevelopmentOperatorAuthenticationHandler.SchemeName;
});

authenticationBuilder.AddScheme<AuthenticationSchemeOptions, DevelopmentOperatorAuthenticationHandler>(
    DevelopmentOperatorAuthenticationHandler.SchemeName,
    static _ => { });

if (identityOptions.Enabled)
{
    authenticationBuilder
        .AddCookie(CookieAuthenticationDefaults.AuthenticationScheme, options =>
        {
            options.Cookie.Name = "hidbridge.operator";
            options.LoginPath = "/auth/login";
            options.LogoutPath = "/auth/logout";
            options.SlidingExpiration = true;
        })
        .AddOpenIdConnect(OpenIdConnectDefaults.AuthenticationScheme, options =>
        {
            options.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            options.Authority = identityOptions.Authority;
            options.ClientId = identityOptions.ClientId;
            options.ClientSecret = identityOptions.ClientSecret;
            options.CallbackPath = identityOptions.CallbackPath;
            options.SignedOutCallbackPath = identityOptions.SignedOutCallbackPath;
            options.SignedOutRedirectUri = "/";
            options.RequireHttpsMetadata = identityOptions.RequireHttpsMetadata;
            options.ResponseType = "code";
            options.SaveTokens = true;
            options.GetClaimsFromUserInfoEndpoint = true;
            options.PushedAuthorizationBehavior = identityOptions.DisablePushedAuthorization
                ? PushedAuthorizationBehavior.Disable
                : PushedAuthorizationBehavior.UseIfAvailable;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                NameClaimType = identityOptions.DisplayNameClaimType,
                RoleClaimType = identityOptions.RoleClaimType,
            };
            options.Scope.Clear();
            var requestedScopes = new[] { "openid" }
                .Concat(identityOptions.Scopes ?? [])
                .Where(static scope => !string.IsNullOrWhiteSpace(scope))
                .Distinct(StringComparer.OrdinalIgnoreCase);
            foreach (var scope in requestedScopes)
            {
                options.Scope.Add(scope);
            }

            options.ClaimActions.MapUniqueJsonKey(identityOptions.SubjectClaimType, identityOptions.SubjectClaimType);
            options.ClaimActions.MapUniqueJsonKey(identityOptions.PrincipalClaimType, identityOptions.PrincipalClaimType);
            options.ClaimActions.MapUniqueJsonKey(identityOptions.DisplayNameClaimType, identityOptions.DisplayNameClaimType);
            options.ClaimActions.MapUniqueJsonKey(identityOptions.TenantClaimType, identityOptions.TenantClaimType);
            options.ClaimActions.MapUniqueJsonKey(identityOptions.OrganizationClaimType, identityOptions.OrganizationClaimType);
            options.ClaimActions.MapUniqueJsonKey("createdTimestamp", "createdTimestamp");
            options.ClaimActions.MapUniqueJsonKey("created_at", "created_at");
            options.ClaimActions.MapUniqueJsonKey("user_created_at", "user_created_at");
            options.Events.OnRedirectToIdentityProviderForSignOut = async context =>
            {
                var authResult = await context.HttpContext.AuthenticateAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                var idToken = context.Properties?.GetTokenValue("id_token")
                    ?? authResult.Properties?.GetTokenValue("id_token")
                    ?? await context.HttpContext.GetTokenAsync(CookieAuthenticationDefaults.AuthenticationScheme, "id_token")
                    ?? await context.HttpContext.GetTokenAsync("id_token");

                if (!string.IsNullOrWhiteSpace(idToken))
                {
                    context.ProtocolMessage.IdTokenHint = idToken;
                    context.ProtocolMessage.SetParameter("id_token_hint", idToken);
                }

                context.ProtocolMessage.ClientId = identityOptions.ClientId;
                context.ProtocolMessage.SetParameter("client_id", identityOptions.ClientId);
                context.ProtocolMessage.PostLogoutRedirectUri = $"{context.Request.Scheme}://{context.Request.Host}{identityOptions.SignedOutCallbackPath}";
            };
        });
}

builder.Services.AddAuthorizationBuilder()
    .AddPolicy(OperatorPolicies.Viewer, policy =>
    {
        policy.RequireAuthenticatedUser();
    })
    .AddPolicy(OperatorPolicies.Moderator, policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireRole("operator.moderator", "operator.admin");
    })
    .AddPolicy(OperatorPolicies.Admin, policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireRole("operator.admin");
    });

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseRequestLocalization(app.Services.GetRequiredService<IOptions<RequestLocalizationOptions>>().Value);
app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapCultureEndpoints();
app.MapThemeEndpoints();
app.MapIdentityEndpoints();
app.MapPolicyExportEndpoints();
app.MapOperationalArtifactEndpoints();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

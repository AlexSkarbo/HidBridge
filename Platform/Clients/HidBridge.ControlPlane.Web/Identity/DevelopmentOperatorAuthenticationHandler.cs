using System.Security.Claims;
using System.Text.Encodings.Web;
using HidBridge.ControlPlane.Web.Configuration;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace HidBridge.ControlPlane.Web.Identity;

/// <summary>
/// Provides a local authenticated operator identity when external OIDC is not enabled.
/// </summary>
public sealed class DevelopmentOperatorAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    /// <summary>
    /// The authentication scheme name used for the local development operator.
    /// </summary>
    public const string SchemeName = "DevelopmentOperator";

    private readonly IdentityOptions _identityOptions;

    /// <summary>
    /// Initializes the development authentication handler.
    /// </summary>
    public DevelopmentOperatorAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IOptions<IdentityOptions> identityOptions)
        : base(options, logger, encoder)
        => _identityOptions = identityOptions.Value;

    /// <inheritdoc />
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var development = _identityOptions.Development;
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, development.SubjectId),
            new("sub", development.SubjectId),
            new(OperatorClaimTypes.PrincipalId, development.PrincipalId),
            new(ClaimTypes.Name, development.DisplayName),
            new("preferred_username", development.PrincipalId),
            new(OperatorClaimTypes.TenantId, development.TenantId),
            new(OperatorClaimTypes.OrganizationId, development.OrganizationId),
        };

        claims.AddRange(development.Roles.Select(static role => new Claim(ClaimTypes.Role, role)));

        var identity = new ClaimsIdentity(claims, SchemeName);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}

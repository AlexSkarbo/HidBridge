namespace HidBridge.ControlPlane.Web.Configuration;

/// <summary>
/// Defines the identity provider settings for the operator web shell.
/// </summary>
public sealed class IdentityOptions
{
    /// <summary>
    /// Gets or sets a value indicating whether external OIDC authentication is enabled.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets or sets the display name of the configured external identity provider.
    /// </summary>
    public string ProviderDisplayName { get; set; } = "OpenID Connect";

    /// <summary>
    /// Gets or sets the OpenID Connect authority URL.
    /// </summary>
    public string? Authority { get; set; }

    /// <summary>
    /// Gets or sets the OpenID Connect client identifier.
    /// </summary>
    public string? ClientId { get; set; }

    /// <summary>
    /// Gets or sets the OpenID Connect client secret.
    /// </summary>
    public string? ClientSecret { get; set; }

    /// <summary>
    /// Gets or sets the callback path used after a successful OIDC login.
    /// </summary>
    public string CallbackPath { get; set; } = "/signin-oidc";

    /// <summary>
    /// Gets or sets the callback path used after a logout flow.
    /// </summary>
    public string SignedOutCallbackPath { get; set; } = "/signout-callback-oidc";

    /// <summary>
    /// Gets or sets a value indicating whether HTTPS metadata is required.
    /// </summary>
    public bool RequireHttpsMetadata { get; set; } = true;

    /// <summary>
    /// Gets or sets the additional OIDC scopes requested by the web shell.
    /// </summary>
    public string[] Scopes { get; set; } = [];

    /// <summary>
    /// Gets or sets a value indicating whether Pushed Authorization Requests should be disabled.
    /// </summary>
    public bool DisablePushedAuthorization { get; set; } = true;

    /// <summary>
    /// Gets or sets the claim type used for the subject identifier.
    /// </summary>
    public string SubjectClaimType { get; set; } = "sub";

    /// <summary>
    /// Gets or sets the claim type used for the operator principal identifier.
    /// </summary>
    public string PrincipalClaimType { get; set; } = "preferred_username";

    /// <summary>
    /// Gets or sets the claim type used for the display name.
    /// </summary>
    public string DisplayNameClaimType { get; set; } = "name";

    /// <summary>
    /// Gets or sets the claim type used for the tenant identifier.
    /// </summary>
    public string TenantClaimType { get; set; } = "tenant_id";

    /// <summary>
    /// Gets or sets the claim type used for the organization identifier.
    /// </summary>
    public string OrganizationClaimType { get; set; } = "org_id";

    /// <summary>
    /// Gets or sets the claim type used for roles.
    /// </summary>
    public string RoleClaimType { get; set; } = "role";

    /// <summary>
    /// Gets or sets the development identity fallback configuration used when OIDC is disabled.
    /// </summary>
    public DevelopmentIdentityOptions Development { get; set; } = new();
}

/// <summary>
/// Defines the local development identity used when external OIDC is disabled.
/// </summary>
public sealed class DevelopmentIdentityOptions
{
    /// <summary>
    /// Gets or sets the synthetic subject identifier.
    /// </summary>
    public string SubjectId { get; set; } = "local-operator";

    /// <summary>
    /// Gets or sets the principal identifier used by operator actions.
    /// </summary>
    public string PrincipalId { get; set; } = "smoke-runner";

    /// <summary>
    /// Gets or sets the display name.
    /// </summary>
    public string DisplayName { get; set; } = "Local Operator";

    /// <summary>
    /// Gets or sets the tenant identifier.
    /// </summary>
    public string TenantId { get; set; } = "local-tenant";

    /// <summary>
    /// Gets or sets the organization identifier.
    /// </summary>
    public string OrganizationId { get; set; } = "local-org";

    /// <summary>
    /// Gets or sets the development roles granted to the local operator.
    /// </summary>
    public string[] Roles { get; set; } = ["operator.viewer", "operator.moderator", "operator.admin"];
}

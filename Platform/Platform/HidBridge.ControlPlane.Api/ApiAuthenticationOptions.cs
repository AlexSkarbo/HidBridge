namespace HidBridge.ControlPlane.Api;

/// <summary>
/// Describes optional bearer-token authentication used by the ControlPlane API.
/// </summary>
public sealed class ApiAuthenticationOptions
{
    /// <summary>
    /// Gets or sets whether JWT bearer authentication is enabled.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Gets or sets the OIDC/JWT authority used to validate access tokens.
    /// </summary>
    public string? Authority { get; set; }

    /// <summary>
    /// Gets or sets the audience expected by the API.
    /// </summary>
    public string? Audience { get; set; }

    /// <summary>
    /// Gets or sets whether HTTPS metadata is required while resolving signing keys.
    /// </summary>
    public bool RequireHttpsMetadata { get; set; } = true;

    /// <summary>
    /// Gets or sets whether legacy caller-scope headers are still accepted when bearer authentication is enabled.
    /// </summary>
    public bool AllowHeaderFallback { get; set; } = true;

    /// <summary>
    /// Gets or sets API path prefixes that require bearer authentication and do not allow header fallback.
    /// </summary>
    public IReadOnlyList<string> BearerOnlyPrefixes { get; set; } = [];

    /// <summary>
    /// Gets or sets API path prefixes that require caller-context claims or approved fallback headers for unsafe mutation methods.
    /// </summary>
    public IReadOnlyList<string> CallerContextRequiredPrefixes { get; set; } = [];

    /// <summary>
    /// Gets or sets unsafe API path patterns where header fallback is disabled and bearer claims must carry caller context.
    /// Supports <c>*</c> as a single path-segment wildcard.
    /// </summary>
    public IReadOnlyList<string> HeaderFallbackDisabledPatterns { get; set; } = [];

    /// <summary>
    /// Gets or sets one default tenant scope that is applied to authenticated callers when no tenant claim/header was provided.
    /// </summary>
    public string? DefaultTenantId { get; set; }

    /// <summary>
    /// Gets or sets one default organization scope that is applied to authenticated callers when no organization claim/header was provided.
    /// </summary>
    public string? DefaultOrganizationId { get; set; }
}

using HidBridge.ControlPlane.Web.Configuration;

namespace HidBridge.ControlPlane.Web.Identity;

/// <summary>
/// Describes the active authentication mode and provider wiring for the operator web shell.
/// </summary>
public sealed record OperatorAuthDescriptor(
    bool ExternalIdentityEnabled,
    string ProviderDisplayName,
    string LoginPath,
    string LogoutPath,
    string CallbackPath,
    string SignedOutCallbackPath,
    string Mode)
{
    /// <summary>
    /// Creates one auth descriptor from the configured identity options.
    /// </summary>
    /// <param name="options">The configured identity options.</param>
    public static OperatorAuthDescriptor Create(IdentityOptions options)
        => new(
            options.Enabled,
            string.IsNullOrWhiteSpace(options.ProviderDisplayName) ? "OpenID Connect" : options.ProviderDisplayName,
            "/auth/login",
            "/auth/logout",
            options.CallbackPath,
            options.SignedOutCallbackPath,
            options.Enabled ? "OIDC" : "Development");
}

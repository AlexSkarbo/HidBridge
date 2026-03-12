using HidBridge.ControlPlane.Web.Configuration;
using HidBridge.ControlPlane.Web.Identity;
using Xunit;

namespace HidBridge.Platform.Tests;

/// <summary>
/// Verifies auth descriptor projection for the operator web shell.
/// </summary>
public sealed class OperatorAuthDescriptorTests
{
    /// <summary>
    /// Verifies that the descriptor reflects an external OIDC configuration.
    /// </summary>
    [Fact]
    public void Create_ReflectsOidcConfiguration()
    {
        var descriptor = OperatorAuthDescriptor.Create(new IdentityOptions
        {
            Enabled = true,
            ProviderDisplayName = "Keycloak",
            CallbackPath = "/signin-oidc",
            SignedOutCallbackPath = "/signout-callback-oidc",
        });

        Assert.True(descriptor.ExternalIdentityEnabled);
        Assert.Equal("Keycloak", descriptor.ProviderDisplayName);
        Assert.Equal("OIDC", descriptor.Mode);
        Assert.Equal("/auth/login", descriptor.LoginPath);
        Assert.Equal("/auth/logout", descriptor.LogoutPath);
    }
}

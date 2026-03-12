namespace HidBridge.ControlPlane.Web.Configuration;

/// <summary>
/// Describes the upstream ControlPlane API endpoint used by the operator web shell.
/// </summary>
public sealed class ControlPlaneApiOptions
{
    /// <summary>
    /// Gets or sets the absolute base URL of the ControlPlane API.
    /// </summary>
    public string BaseUrl { get; set; } = "http://127.0.0.1:18093";

    /// <summary>
    /// Gets or sets the operator principal used by the shell until full authentication is enabled.
    /// </summary>
    public string OperatorPrincipalId { get; set; } = "smoke-runner";

    /// <summary>
    /// Gets or sets whether caller scope headers are forwarded to the backend API.
    /// </summary>
    public bool PropagateIdentityHeaders { get; set; } = true;

    /// <summary>
    /// Gets or sets whether the current access token is forwarded as a bearer token.
    /// </summary>
    public bool PropagateAccessToken { get; set; } = true;
}

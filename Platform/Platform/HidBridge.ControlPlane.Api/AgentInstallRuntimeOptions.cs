namespace HidBridge.ControlPlane.Api;

/// <summary>
/// Configures signed install-link generation for remote edge-agent bootstrap.
/// </summary>
public sealed class AgentInstallRuntimeOptions
{
    /// <summary>
    /// Enables install-link endpoint surface.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// HMAC key used to sign bootstrap payload tokens.
    /// </summary>
    public string SigningKey { get; set; } = "change-me-agent-install-signing-key";

    /// <summary>
    /// Default expiration for generated install links.
    /// </summary>
    public int TokenTtlMinutes { get; set; } = 30;

    /// <summary>
    /// Optional public base URL used in generated links (for example reverse-proxy URL).
    /// </summary>
    public string PublicBaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// Optional downloadable package URL (zip) with edge-agent binary.
    /// </summary>
    public string PackageUrl { get; set; } = string.Empty;

    /// <summary>
    /// Optional package SHA-256 checksum used in bootstrap script validation.
    /// </summary>
    public string PackageSha256 { get; set; } = string.Empty;

    /// <summary>
    /// Relative executable path inside install directory.
    /// </summary>
    public string AgentExecutableRelativePath { get; set; } = "HidBridge.EdgeProxy.Agent.exe";

    /// <summary>
    /// Default install root path used by bootstrap script.
    /// </summary>
    public string DefaultInstallDirectory { get; set; } = @"$env:ProgramData\HidBridge\EdgeAgent";
}

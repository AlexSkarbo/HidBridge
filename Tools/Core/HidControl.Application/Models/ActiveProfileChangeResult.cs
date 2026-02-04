namespace HidControl.Application.Models;

/// <summary>
/// Result of activating a profile.
/// </summary>
/// <param name="Ok">True when the profile was activated.</param>
/// <param name="Active">Active profile name.</param>
/// <param name="ConfigSaved">True when config was saved.</param>
/// <param name="ConfigPath">Config path.</param>
/// <param name="ConfigError">Config error.</param>
public sealed record ActiveProfileChangeResult(
    bool Ok,
    string Active,
    bool ConfigSaved,
    string? ConfigPath,
    string? ConfigError);

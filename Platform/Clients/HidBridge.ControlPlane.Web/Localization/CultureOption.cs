namespace HidBridge.ControlPlane.Web.Localization;

/// <summary>
/// Describes one UI culture option exposed by the thin operator shell.
/// </summary>
/// <param name="Code">The culture code used by ASP.NET localization middleware.</param>
/// <param name="DisplayName">The human-readable culture name shown in the settings UI.</param>
public sealed record CultureOption(string Code, string DisplayName);

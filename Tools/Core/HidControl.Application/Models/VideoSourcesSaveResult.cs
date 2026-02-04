using HidControl.Contracts;
using System.Collections.Generic;

namespace HidControl.Application.Models;

/// <summary>
/// Result of saving video sources into the in-memory store and config.
/// </summary>
/// <param name="Ok">True on success.</param>
/// <param name="Error">Error code/message on failure.</param>
/// <param name="Sources">Current sources snapshot.</param>
/// <param name="ConfigSaved">True when config was persisted.</param>
/// <param name="ConfigPath">Config file path (if applicable).</param>
/// <param name="ConfigError">Config save error (if any).</param>
public sealed record VideoSourcesSaveResult(
    bool Ok,
    string? Error,
    IReadOnlyList<VideoSourceConfig> Sources,
    bool ConfigSaved,
    string? ConfigPath,
    string? ConfigError);

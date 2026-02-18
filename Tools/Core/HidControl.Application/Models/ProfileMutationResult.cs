using HidControl.Contracts;

namespace HidControl.Application.Models;

/// <summary>
/// Result of create/update/delete profile operation.
/// </summary>
/// <param name="Ok">True when operation succeeded.</param>
/// <param name="Error">Validation or runtime error.</param>
/// <param name="Profiles">Latest profile snapshot.</param>
/// <param name="Active">Current active profile.</param>
public sealed record ProfileMutationResult(bool Ok, string? Error, IReadOnlyList<VideoProfileConfig> Profiles, string Active);

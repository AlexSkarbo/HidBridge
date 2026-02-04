using HidControl.Contracts;

namespace HidControl.Application.Models;

/// <summary>
/// Snapshot of profiles and active selection.
/// </summary>
/// <param name="Profiles">Profiles list.</param>
/// <param name="ActiveProfile">Active profile name.</param>
public sealed record VideoProfilesSnapshot(IReadOnlyList<VideoProfileConfig> Profiles, string ActiveProfile);

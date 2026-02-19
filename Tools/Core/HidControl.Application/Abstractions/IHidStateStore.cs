using HidControl.Contracts;

namespace HidControl.Application.Abstractions;

/// <summary>
/// Snapshot of mutable server state persisted outside static config.
/// </summary>
/// <param name="VideoProfiles">User/system video profiles snapshot.</param>
/// <param name="ActiveVideoProfile">Active video profile name.</param>
/// <param name="RoomProfileBindings">Room to profile binding map.</param>
public sealed record HidStateSnapshot(
    IReadOnlyList<VideoProfileConfig> VideoProfiles,
    string? ActiveVideoProfile,
    IReadOnlyDictionary<string, string?> RoomProfileBindings);

/// <summary>
/// Persistent store for mutable runtime-managed state.
/// </summary>
public interface IHidStateStore
{
    /// <summary>
    /// Loads current state snapshot.
    /// </summary>
    /// <returns>State snapshot.</returns>
    HidStateSnapshot Load();

    /// <summary>
    /// Saves video profiles and active profile atomically.
    /// </summary>
    /// <param name="profiles">Profiles.</param>
    /// <param name="activeProfile">Active profile.</param>
    void SaveVideoProfiles(IReadOnlyList<VideoProfileConfig> profiles, string? activeProfile);

    /// <summary>
    /// Saves room-profile bindings atomically.
    /// </summary>
    /// <param name="bindings">Bindings.</param>
    void SaveRoomProfileBindings(IReadOnlyDictionary<string, string?> bindings);
}

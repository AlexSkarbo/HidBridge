using HidControl.Contracts;

namespace HidControl.Application.Abstractions;

/// <summary>
/// Abstraction for killing orphaned FFmpeg processes.
/// </summary>
public interface IVideoOrphanKiller
{
    /// <summary>
    /// Kills orphan processes for a source.
    /// </summary>
    /// <param name="source">Source.</param>
    /// <returns>List of killed process ids.</returns>
    IReadOnlyList<int> KillOrphans(VideoSourceConfig source);
}

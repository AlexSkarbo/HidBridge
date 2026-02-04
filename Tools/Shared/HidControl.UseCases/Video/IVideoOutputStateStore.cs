using HidControl.Contracts;

namespace HidControl.UseCases.Video;

// Storage abstraction for video output settings.
/// <summary>
/// IVideoOutputStateStore.
/// </summary>
public interface IVideoOutputStateStore
{
    /// <summary>
    /// Gets get.
    /// </summary>
    /// <returns>Result.</returns>
    VideoOutputState Get();
    /// <summary>
    /// Sets set.
    /// </summary>
    /// <param name="state">The state.</param>
    void Set(VideoOutputState state);
}

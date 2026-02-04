using HidControl.Contracts;

namespace HidControl.Application.Abstractions;

/// <summary>
/// Abstraction for current video output state.
/// </summary>
public interface IVideoOutputStateProvider
{
    /// <summary>
    /// Gets current output state.
    /// </summary>
    /// <returns>Output state.</returns>
    VideoOutputState Get();
}

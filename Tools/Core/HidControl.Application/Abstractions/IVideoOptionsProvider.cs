using HidControl.Contracts;

namespace HidControl.Application.Abstractions;

/// <summary>
/// Provides current video options to application use cases.
/// </summary>
public interface IVideoOptionsProvider
{
    /// <summary>
    /// Gets current video options snapshot.
    /// </summary>
    VideoOptions GetVideoOptions();
}


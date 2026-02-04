using HidControl.Application.Abstractions;
using HidControl.UseCases.Video;

namespace HidControlServer.Adapters.Application;

/// <summary>
/// Adapts VideoOutputService to application abstraction.
/// </summary>
public sealed class VideoOutputStateProviderAdapter : IVideoOutputStateProvider
{
    private readonly VideoOutputService _service;

    /// <summary>
    /// Executes VideoOutputStateProviderAdapter.
    /// </summary>
    /// <param name="service">Output service.</param>
    public VideoOutputStateProviderAdapter(VideoOutputService service)
    {
        _service = service;
    }

    /// <summary>
    /// Gets current output state.
    /// </summary>
    public VideoOutputState Get() => _service.Get();
}

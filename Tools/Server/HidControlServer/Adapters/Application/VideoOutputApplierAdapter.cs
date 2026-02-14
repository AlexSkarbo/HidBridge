using HidControl.Application.Abstractions;
using HidControl.Contracts;
using HidControl.UseCases.Video;

namespace HidControlServer.Adapters.Application;

/// <summary>
/// Adapts VideoOutputService to application abstraction.
/// </summary>
public sealed class VideoOutputApplierAdapter : IVideoOutputApplier
{
    private readonly VideoOutputService _service;

    /// <summary>
    /// Executes VideoOutputApplierAdapter.
    /// </summary>
    /// <param name="service">Output service.</param>
    public VideoOutputApplierAdapter(VideoOutputService service)
    {
        _service = service;
    }

    /// <summary>
    /// Validates and applies an output request.
    /// </summary>
    public bool TryApply(VideoOutputRequest req, out VideoOutputState next, out string? error)
    {
        return _service.TryApply(req, out next, out error);
    }
}


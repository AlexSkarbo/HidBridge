using HidControl.Application.Abstractions;
using HidControl.Contracts;
using HidControlServer;

namespace HidControlServer.Adapters.Application;

/// <summary>
/// Adapts server options to application abstraction.
/// </summary>
public sealed class VideoOptionsProviderAdapter : IVideoOptionsProvider
{
    private readonly Options _options;

    /// <summary>
    /// Creates a new instance.
    /// </summary>
    public VideoOptionsProviderAdapter(Options options)
    {
        _options = options;
    }

    /// <inheritdoc />
    public VideoOptions GetVideoOptions() => _options.ToVideoOptions();
}


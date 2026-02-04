using HidControl.Application.Abstractions;
using HidControl.Contracts;
using HidControl.UseCases;
using HidControl.UseCases.Video;
using HidControlServer.Services;

namespace HidControlServer.Adapters.Application;

/// <summary>
/// Builds stream args using the existing server-side pipeline builder.
/// </summary>
public sealed class VideoStreamArgsBuilderAdapter : IVideoStreamArgsBuilder
{
    private readonly Options _opt;
    private readonly VideoProfileStore _profiles;
    private readonly AppState _appState;

    /// <summary>
    /// Creates a new instance.
    /// </summary>
    public VideoStreamArgsBuilderAdapter(Options opt, VideoProfileStore profiles, AppState appState)
    {
        _opt = opt;
        _profiles = profiles;
        _appState = appState;
    }

    /// <inheritdoc />
    public string BuildArgs(IReadOnlyList<VideoSourceConfig> enabledSources, VideoSourceConfig source, VideoOutputState outputState)
    {
        return VideoInputService.BuildFfmpegStreamArgs(_opt, _profiles, enabledSources, source, _appState, outputState);
    }
}


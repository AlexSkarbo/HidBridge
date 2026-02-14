using HidControl.Contracts;
using HidControl.UseCases.Video;
using HidControlServer.Services;

namespace HidControlServer.Adapters.Video;

/// <summary>
/// Adapts VideoFfmpegControllerAdapter.
/// </summary>
public sealed class VideoFfmpegControllerAdapter : IVideoFfmpegController
{
    private readonly Options _opt;
    private readonly VideoProfileStore _profiles;
    private readonly AppState _appState;

    /// <summary>
    /// Executes VideoFfmpegControllerAdapter.
    /// </summary>
    /// <param name="opt">The opt.</param>
    /// <param name="profiles">The profiles.</param>
    /// <param name="appState">The appState.</param>
    public VideoFfmpegControllerAdapter(Options opt, VideoProfileStore profiles, AppState appState)
    {
        _opt = opt;
        _profiles = profiles;
        _appState = appState;
    }

    /// <summary>
    /// Starts for sources.
    /// </summary>
    /// <param name="sources">The sources.</param>
    /// <param name="outputState">The outputState.</param>
    /// <param name="restart">The restart.</param>
    /// <param name="force">The force.</param>
    /// <returns>Result.</returns>
    public FfmpegStartResult StartForSources(IReadOnlyList<VideoSourceConfig> sources, VideoOutputState outputState, bool restart, bool force)
    {
        var started = new List<object>();
        var errors = new List<string>();
        FfmpegPipelineService.StartFfmpegForSources(_opt, _profiles, sources, _appState, outputState, _appState.FfmpegProcesses, _appState.FfmpegStates, restart, started, errors, force);
        return new FfmpegStartResult(started, errors);
    }
}

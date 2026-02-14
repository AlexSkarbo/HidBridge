using HidControl.UseCases.Video;

namespace HidControlServer.Adapters.Video;

// AppState-backed adapter for video output settings.
/// <summary>
/// Adapts VideoOutputStateAdapter.
/// </summary>
public sealed class VideoOutputStateAdapter : IVideoOutputStateStore
{
    private readonly AppState _state;

    /// <summary>
    /// Executes VideoOutputStateAdapter.
    /// </summary>
    /// <param name="state">The state.</param>
    public VideoOutputStateAdapter(AppState state)
    {
        _state = state;
    }

    /// <summary>
    /// Gets get.
    /// </summary>
    /// <returns>Result.</returns>
    public VideoOutputState Get()
    {
        return new VideoOutputState(
            _state.VideoOutputHlsEnabled,
            _state.VideoOutputMjpegEnabled,
            _state.VideoOutputFlvEnabled,
            _state.VideoOutputMjpegPassthrough,
            _state.VideoOutputMjpegFps,
            _state.VideoOutputMjpegSize);
    }

    /// <summary>
    /// Sets set.
    /// </summary>
    /// <param name="state">The state.</param>
    public void Set(VideoOutputState state)
    {
        _state.VideoOutputHlsEnabled = state.Hls;
        _state.VideoOutputMjpegEnabled = state.Mjpeg;
        _state.VideoOutputFlvEnabled = state.Flv;
        _state.VideoOutputMjpegPassthrough = state.MjpegPassthrough;
        _state.VideoOutputMjpegFps = state.MjpegFps;
        _state.VideoOutputMjpegSize = state.MjpegSize;
    }
}

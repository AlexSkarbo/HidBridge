using HidControl.UseCases;
using HidControl.UseCases.Video;
using HidControlServer.Services;

namespace HidControlServer;

// Video input parsing and FFmpeg argument construction for sources and profiles.
/// <summary>
/// Provides server utility helpers for videoinput.
/// </summary>
internal static partial class ServerUtils
{
    /// <summary>
    /// Builds ffmpeg input.
    /// </summary>
    /// <param name="source">The source.</param>
    /// <returns>Result.</returns>
    public static string? BuildFfmpegInput(VideoSourceConfig source)
    {
        return VideoInputService.BuildFfmpegInput(source);
    }

    /// <summary>
    /// Builds mjpeg input.
    /// </summary>
    /// <param name="opt">The opt.</param>
    /// <param name="source">The source.</param>
    /// <param name="appState">The appState.</param>
    /// <returns>Result.</returns>
    public static string? BuildMjpegInput(Options opt, VideoSourceConfig source, AppState appState)
    {
        return VideoInputService.BuildMjpegInput(opt, source, appState);
    }

    /// <summary>
    /// Builds ffmpeg stream args.
    /// </summary>
    /// <param name="opt">The opt.</param>
    /// <param name="profiles">The profiles.</param>
    /// <param name="sources">The sources.</param>
    /// <param name="src">The src.</param>
    /// <param name="appState">The appState.</param>
    /// <param name="outputState">The outputState.</param>
    /// <returns>Result.</returns>
    public static string BuildFfmpegStreamArgs(Options opt, VideoProfileStore profiles, IReadOnlyList<VideoSourceConfig> sources, VideoSourceConfig src, AppState appState, VideoOutputState outputState)
    {
        return VideoInputService.BuildFfmpegStreamArgs(opt, profiles, sources, src, appState, outputState);
    }
}

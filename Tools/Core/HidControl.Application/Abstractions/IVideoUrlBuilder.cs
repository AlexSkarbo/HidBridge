using HidControl.Contracts;

namespace HidControl.Application.Abstractions;

/// <summary>
/// Abstraction for building client-facing video URLs.
/// </summary>
public interface IVideoUrlBuilder
{
    /// <summary>
    /// Gets base URL for video endpoints.
    /// </summary>
    string GetVideoBaseUrl(VideoOptions opt);

    /// <summary>
    /// Builds RTSP client URL for a source.
    /// </summary>
    string BuildRtspClientUrl(VideoOptions opt, string id);

    /// <summary>
    /// Builds SRT client URL for a source index.
    /// </summary>
    string BuildSrtClientUrl(VideoOptions opt, int index);

    /// <summary>
    /// Builds RTMP client URL for a source.
    /// </summary>
    string BuildRtmpClientUrl(VideoOptions opt, string id);

    /// <summary>
    /// Builds HLS playback URL for a source.
    /// </summary>
    string BuildHlsUrl(VideoOptions opt, string id);
}


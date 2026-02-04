namespace HidControl.Application.Models;

/// <summary>
/// Video stream URLs and source metadata for clients.
/// </summary>
/// <param name="Id">Source id.</param>
/// <param name="Name">Source name.</param>
/// <param name="Kind">Source kind.</param>
/// <param name="Url">Source url.</param>
/// <param name="Enabled">Whether the source is enabled.</param>
/// <param name="RtspUrl">RTSP client URL.</param>
/// <param name="SrtUrl">SRT client URL.</param>
/// <param name="RtmpUrl">RTMP client URL.</param>
/// <param name="HlsUrl">HLS playback URL.</param>
/// <param name="MjpegUrl">MJPEG endpoint URL.</param>
/// <param name="FlvUrl">FLV endpoint URL.</param>
/// <param name="SnapshotUrl">Snapshot endpoint URL.</param>
/// <param name="FfmpegInput">Derived FFmpeg input args (for diagnostics).</param>
public sealed record VideoStreamInfo(
    string Id,
    string? Name,
    string Kind,
    string Url,
    bool Enabled,
    string RtspUrl,
    string SrtUrl,
    string RtmpUrl,
    string HlsUrl,
    string MjpegUrl,
    string FlvUrl,
    string SnapshotUrl,
    string? FfmpegInput);


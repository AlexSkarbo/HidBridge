namespace HidControl.Contracts;

/// <summary>
/// Video output configuration shared across endpoints and background services.
/// </summary>
/// <param name="Hls">True when HLS output is enabled.</param>
/// <param name="Mjpeg">True when MJPEG output is enabled.</param>
/// <param name="Flv">True when FLV output is enabled.</param>
/// <param name="MjpegPassthrough">True when MJPEG passthrough is enabled.</param>
/// <param name="MjpegFps">MJPEG output framerate.</param>
/// <param name="MjpegSize">MJPEG output size preset string.</param>
public sealed record VideoOutputState(
    bool Hls,
    bool Mjpeg,
    bool Flv,
    bool MjpegPassthrough,
    int MjpegFps,
    string MjpegSize);


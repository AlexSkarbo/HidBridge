namespace HidControl.UseCases.Video;

// Video output configuration shared across endpoints and background services.
/// <summary>
/// Use case model for VideoOutputState.
/// </summary>
/// <param name="Hls">Hls.</param>
/// <param name="Mjpeg">Mjpeg.</param>
/// <param name="Flv">Flv.</param>
/// <param name="MjpegPassthrough">MjpegPassthrough.</param>
/// <param name="MjpegFps">MjpegFps.</param>
/// <param name="MjpegSize">MjpegSize.</param>
public sealed record VideoOutputState(
    bool Hls,
    bool Mjpeg,
    bool Flv,
    bool MjpegPassthrough,
    int MjpegFps,
    string MjpegSize);

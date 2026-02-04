namespace HidControl.Contracts;

/// <summary>
/// Helpers for resolving the active video output mode from a <see cref="VideoOutputState"/>.
/// </summary>
public static class VideoOutputModes
{
    /// <summary>
    /// Resolves the effective mode string from output state.
    /// </summary>
    public static string ResolveMode(VideoOutputState state)
    {
        if (state.Flv) return "flv";
        if (state.MjpegPassthrough) return "mjpeg-passthrough";
        if (state.Mjpeg) return "mjpeg";
        return "hls";
    }
}


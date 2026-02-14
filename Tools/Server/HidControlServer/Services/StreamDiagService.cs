namespace HidControlServer.Services;

/// <summary>
/// Provides stream diagnostics helpers.
/// </summary>
internal static class StreamDiagService
{
    /// <summary>
    /// Checks whether ffmpeg stderr indicates a busy capture device.
    /// </summary>
    /// <param name="stderr">Standard error output.</param>
    /// <returns>True if device appears busy.</returns>
    public static bool IsDeviceBusy(string stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr)) return false;
        return stderr.Contains("device already in use", StringComparison.OrdinalIgnoreCase) ||
            stderr.Contains("Could not run graph", StringComparison.OrdinalIgnoreCase);
    }
}

using HidControl.Contracts;
using HidControlServer.Services;

namespace HidControlServer;

// Device capability discovery (DirectShow devices/modes).
/// <summary>
/// Provides server utility helpers for videomodes.
/// </summary>
internal static partial class ServerUtils
{
    /// <summary>
    /// Executes ListDshowDevices.
    /// </summary>
    /// <param name="ffmpegPath">The ffmpegPath.</param>
    /// <returns>Result.</returns>
    public static IReadOnlyList<VideoDshowDevice> ListDshowDevices(string ffmpegPath)
    {
        return VideoModeService.ListDshowDevices(ffmpegPath);
    }

    /// <summary>
    /// Executes ListDshowModes.
    /// </summary>
    /// <param name="ffmpegPath">The ffmpegPath.</param>
    /// <param name="deviceName">The deviceName.</param>
    /// <returns>Result.</returns>
    public static VideoModesResult ListDshowModes(string ffmpegPath, string deviceName)
    {
        return VideoModeService.ListDshowModes(ffmpegPath, deviceName);
    }
}

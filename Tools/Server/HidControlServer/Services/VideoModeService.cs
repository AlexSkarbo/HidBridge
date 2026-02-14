using HidControl.Contracts;
using InfraVideoModeService = HidControl.Infrastructure.Services.VideoModeService;

namespace HidControlServer.Services;

/// <summary>
/// Discovers DirectShow devices and capture modes.
/// </summary>
internal static class VideoModeService
{
    /// <summary>
    /// Lists DirectShow video devices.
    /// </summary>
    /// <param name="ffmpegPath">FFmpeg path.</param>
    /// <returns>Device list.</returns>
    public static IReadOnlyList<VideoDshowDevice> ListDshowDevices(string ffmpegPath)
    {
        return InfraVideoModeService.ListDshowDevices(ffmpegPath);
    }

    /// <summary>
    /// Lists DirectShow capture modes for a device.
    /// </summary>
    /// <param name="ffmpegPath">FFmpeg path.</param>
    /// <param name="deviceName">Device name.</param>
    /// <returns>Modes and MJPEG support.</returns>
    public static VideoModesResult ListDshowModes(string ffmpegPath, string deviceName)
    {
        return InfraVideoModeService.ListDshowModes(ffmpegPath, deviceName);
    }
}

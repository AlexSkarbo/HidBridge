using HidControl.Contracts;
using HidControl.UseCases.Video;

namespace HidControlServer.Services;

/// <summary>
/// Initializes video output and cached capabilities on startup.
/// </summary>
internal static class VideoStartupService
{
    /// <summary>
    /// Applies default output settings and preloads DirectShow modes when available.
    /// </summary>
    /// <param name="opt">Options.</param>
    /// <param name="store">Video source store.</param>
    /// <param name="appState">Application state.</param>
    /// <param name="outputService">Output service.</param>
    public static void InitializeDefaultVideoOutput(Options opt, VideoSourceStore store, AppState appState, VideoOutputService outputService)
    {
        outputService.TryApply(new VideoOutputRequest(
            Mode: null,
            Hls: false,
            Mjpeg: true,
            Flv: false,
            MjpegPassthrough: false,
            MjpegFps: null,
            MjpegSize: null), out _, out _);

        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        if (!ExecutableService.TryValidateExecutablePath(opt.FfmpegPath, out _))
        {
            return;
        }

        var sources = store.GetAll().Where(s => s.Enabled).ToList();
        if (sources.Count == 0)
        {
            return;
        }

        IReadOnlyList<VideoDshowDevice>? dshowDevices = null;
        try
        {
            dshowDevices = VideoModeService.ListDshowDevices(opt.FfmpegPath);
        }
        catch
        {
            dshowDevices = null;
        }

        bool supportsAll = true;
        foreach (var src in sources)
        {
            if (string.IsNullOrWhiteSpace(src.Name))
            {
                supportsAll = false;
                continue;
            }
            string deviceName = src.Name;
            if (dshowDevices is not null)
            {
                var match = dshowDevices.FirstOrDefault(d => string.Equals(d.Name, src.Name, StringComparison.OrdinalIgnoreCase));
                if (match is not null && !string.IsNullOrWhiteSpace(match.AlternativeName))
                {
                    deviceName = match.AlternativeName;
                }
            }

            try
            {
                var modesResult = VideoModeService.ListDshowModes(opt.FfmpegPath, deviceName);
                appState.VideoModesCache[src.Id] = new AppState.VideoModesCacheEntry(DateTimeOffset.UtcNow, deviceName, modesResult.Modes, modesResult.SupportsMjpeg);
                if (!modesResult.SupportsMjpeg)
                {
                    supportsAll = false;
                }
            }
            catch
            {
                supportsAll = false;
            }
        }

        outputService.TryApply(new VideoOutputRequest(
            Mode: null,
            Hls: null,
            Mjpeg: null,
            Flv: null,
            MjpegPassthrough: supportsAll,
            MjpegFps: null,
            MjpegSize: null), out _, out _);
    }
}

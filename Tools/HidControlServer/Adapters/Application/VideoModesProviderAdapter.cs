using HidControl.Application.Abstractions;
using HidControl.Application.Models;
using HidControl.Contracts;
using HidControlServer.Services;
using HidControl.UseCases;

namespace HidControlServer.Adapters.Application;

/// <summary>
/// Server-side implementation of <see cref="IVideoModesProvider"/> using FFmpeg + DirectShow probing (Windows-only).
/// </summary>
public sealed class VideoModesProviderAdapter : IVideoModesProvider
{
    private readonly Options _opt;
    private readonly VideoSourceStore _store;
    private readonly AppState _appState;

    /// <summary>
    /// Creates a new instance.
    /// </summary>
    public VideoModesProviderAdapter(Options opt, VideoSourceStore store, AppState appState)
    {
        _opt = opt;
        _store = store;
        _appState = appState;
    }

    /// <inheritdoc />
    public Task<VideoModesQueryResult> QueryAsync(string id, bool refresh, CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows())
        {
            return Task.FromResult(new VideoModesQueryResult(false, "not_supported", false, null, null, Array.Empty<VideoMode>(), false));
        }

        if (!ExecutableService.TryValidateExecutablePath(_opt.FfmpegPath, out string ffmpegError))
        {
            return Task.FromResult(new VideoModesQueryResult(false, $"ffmpeg {ffmpegError}", false, null, null, Array.Empty<VideoMode>(), false));
        }

        VideoSourceConfig? src = _store.GetAll().FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));
        if (src is null)
        {
            return Task.FromResult(new VideoModesQueryResult(false, "source_not_found", false, null, null, Array.Empty<VideoMode>(), false));
        }
        if (string.IsNullOrWhiteSpace(src.Name))
        {
            return Task.FromResult(new VideoModesQueryResult(false, "source_name_missing", false, src.Id, null, Array.Empty<VideoMode>(), false));
        }

        string deviceName = src.Name;
        try
        {
            var dshow = VideoModeService.ListDshowDevices(_opt.FfmpegPath);
            var match = dshow.FirstOrDefault(d => string.Equals(d.Name, src.Name, StringComparison.OrdinalIgnoreCase));
            if (match is not null && !string.IsNullOrWhiteSpace(match.AlternativeName))
            {
                deviceName = match.AlternativeName;
            }
        }
        catch { }

        string cacheKey = src.Id;
        int cacheTtlSeconds = _opt.VideoModesCacheTtlSeconds;
        if (!refresh && cacheTtlSeconds > 0 && _appState.VideoModesCache.TryGetValue(cacheKey, out var cached))
        {
            if (string.Equals(cached.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase) &&
                (DateTimeOffset.UtcNow - cached.CachedAt) < TimeSpan.FromSeconds(cacheTtlSeconds))
            {
                return Task.FromResult(new VideoModesQueryResult(true, null, true, src.Id, deviceName, cached.Modes, cached.SupportsMjpeg));
            }
        }

        VideoModesResult modesResult;
        try
        {
            modesResult = VideoModeService.ListDshowModes(_opt.FfmpegPath, deviceName);
        }
        catch (Exception ex)
        {
            return Task.FromResult(new VideoModesQueryResult(false, ex.Message, false, src.Id, deviceName, Array.Empty<VideoMode>(), false));
        }

        _appState.VideoModesCache[cacheKey] = new AppState.VideoModesCacheEntry(DateTimeOffset.UtcNow, deviceName, modesResult.Modes, modesResult.SupportsMjpeg);
        return Task.FromResult(new VideoModesQueryResult(true, null, false, src.Id, deviceName, modesResult.Modes, modesResult.SupportsMjpeg));
    }
}


using HidControl.Contracts;
using HidControl.Core;

namespace HidControl.UseCases.Video;

/// <summary>
/// Use case model for VideoWatchdogService.
/// </summary>
public sealed class VideoWatchdogService
{
    private readonly VideoFfmpegService _ffmpegService;
    private readonly IVideoWatchdogEvents? _events;

    /// <summary>
    /// Executes VideoWatchdogService.
    /// </summary>
    /// <param name="ffmpegService">The ffmpegService.</param>
    /// <param name="events">Optional watchdog event sink.</param>
    public VideoWatchdogService(VideoFfmpegService ffmpegService, IVideoWatchdogEvents? events = null)
    {
        _ffmpegService = ffmpegService;
        _events = events;
    }

    public void RunFfmpegWatchdog(
        VideoWatchdogOptions options,
        VideoSourceStore sourcesStore,
        VideoOutputState outputState,
        IVideoRuntimeState runtime)
    {
        var sources = sourcesStore.GetAll().Where(s => s.Enabled).ToList();
        if (sources.Count == 0) return;
        bool noOutputsEnabled = !outputState.Hls && !outputState.Mjpeg && !outputState.Flv;

        DateTimeOffset now = DateTimeOffset.UtcNow;
        foreach (VideoSourceConfig src in sources)
        {
            if (runtime.IsManualStop(src.Id))
            {
                var manualState = runtime.GetOrAddFfmpegState(src.Id);
                LogWatchdogSkip(manualState, now, src.Id, "manual_stop");
                continue;
            }
            if (!runtime.TryGetFfmpegProcess(src.Id, out var proc) || proc.HasExited)
            {
                var state = runtime.GetOrAddFfmpegState(src.Id);
                if (noOutputsEnabled)
                {
                    LogWatchdogSkip(state, now, src.Id, "roomless_no_output_enabled");
                    continue;
                }
                if (IsCaptureSourceId(src.Id) && state.LastExitCode == -22)
                {
                    LogWatchdogSkip(state, now, src.Id, "invalid_args_exit_-22", new
                    {
                        lastExitCode = state.LastExitCode,
                        restartCount = state.RestartCount
                    });
                    continue;
                }
                if (state.RestartCount >= options.FfmpegMaxRestarts)
                {
                    LogWatchdogSkip(state, now, src.Id, "restart_limit", new
                    {
                        restartCount = state.RestartCount,
                        maxRestarts = options.FfmpegMaxRestarts
                    });
                    continue;
                }
                if (state.LastStartAt.HasValue &&
                    state.LastStartAt.Value.AddMilliseconds(options.FfmpegRestartDelayMs) > now)
                {
                    LogWatchdogSkip(state, now, src.Id, "restart_delay_window", new
                    {
                        lastStartAt = state.LastStartAt,
                        restartDelayMs = options.FfmpegRestartDelayMs
                    });
                    continue;
                }
                if (state.LastStartAt.HasValue &&
                    now - state.LastStartAt.Value > TimeSpan.FromMilliseconds(options.FfmpegRestartWindowMs))
                {
                    state.RestartCount = 0;
                }
                state.RestartCount++;
                state.LastWatchdogSkipReason = null;
                state.LastWatchdogSkipAt = null;
                _events?.Log("watchdog", "ffmpeg_restart", new
                {
                    id = src.Id,
                    reason = "process_missing",
                    hadProcess = proc is not null,
                    hasExited = proc?.HasExited ?? true,
                    lastStartAt = state.LastStartAt,
                    restartCount = state.RestartCount
                });
                if (proc is not null)
                {
                    try { proc.Kill(true); } catch { }
                }
                if (options.KillOrphanFfmpeg)
                {
                    runtime.KillOrphansForSource(src, proc);
                }
                runtime.TryRemoveFfmpegProcess(src.Id, out _);
                _ffmpegService.StartForSources(new[] { src }, outputState, restart: true, force: true);
            }
        }
    }

    public void RunFlvWatchdog(
        VideoWatchdogOptions options,
        VideoSourceStore sourcesStore,
        VideoOutputState outputState,
        IVideoRuntimeState runtime)
    {
        if (!outputState.Flv) return;
        var sources = sourcesStore.GetAll().Where(s => s.Enabled).ToList();
        if (sources.Count == 0) return;

        DateTimeOffset now = DateTimeOffset.UtcNow;
        var staleThreshold = TimeSpan.FromSeconds(Math.Max(1, options.FlvStaleSeconds));

        foreach (VideoSourceConfig src in sources)
        {
            if (runtime.IsManualStop(src.Id)) continue;
            if (!runtime.TryGetFfmpegProcess(src.Id, out var proc) || proc.HasExited) continue;

            var stats = runtime.GetFlvStats(src.Id);
            if (stats is null) continue;
            if (stats.Value.SubscribersActive <= 0) continue;
            if (!stats.Value.HasHeader || !stats.Value.HasVideoConfig || !stats.Value.HasKeyframe) continue;
            if (stats.Value.LastTagAtUtc == default) continue;
            if (now - stats.Value.LastTagAtUtc <= staleThreshold) continue;

            var state = runtime.GetOrAddFfmpegState(src.Id);
            if (state.RestartCount >= options.FfmpegMaxRestarts) continue;
            if (state.LastStartAt.HasValue &&
                state.LastStartAt.Value.AddMilliseconds(options.FfmpegRestartDelayMs) > now)
            {
                continue;
            }
            if (state.LastStartAt.HasValue &&
                now - state.LastStartAt.Value > TimeSpan.FromMilliseconds(options.FfmpegRestartWindowMs))
            {
                state.RestartCount = 0;
            }
            state.RestartCount++;
            _events?.Log("watchdog", "ffmpeg_restart", new
            {
                id = src.Id,
                reason = "flv_stale",
                subscribersActive = stats.Value.SubscribersActive,
                lastTagAtUtc = stats.Value.LastTagAtUtc,
                staleThresholdSeconds = staleThreshold.TotalSeconds,
                restartCount = state.RestartCount
            });
            try { proc.Kill(true); } catch { }
            if (options.KillOrphanFfmpeg)
            {
                runtime.KillOrphansForSource(src, proc);
            }
            runtime.TryRemoveFfmpegProcess(src.Id, out _);
            _ffmpegService.StartForSources(new[] { src }, outputState, restart: true, force: true);
        }
    }

    private static bool IsCaptureSourceId(string sourceId) =>
        !string.IsNullOrWhiteSpace(sourceId) &&
        sourceId.StartsWith("cap-", StringComparison.OrdinalIgnoreCase);

    private void LogWatchdogSkip(FfmpegProcState state, DateTimeOffset now, string sourceId, string reason, object? extra = null)
    {
        if (!ShouldLogSkipReason(state, now, reason))
        {
            return;
        }

        if (extra is null)
        {
            _events?.Log("watchdog", "watchdog_skip_reason", new { id = sourceId, reason });
            return;
        }

        _events?.Log("watchdog", "watchdog_skip_reason", new
        {
            id = sourceId,
            reason,
            data = extra
        });
    }

    private static bool ShouldLogSkipReason(FfmpegProcState state, DateTimeOffset now, string reason)
    {
        // Avoid noisy duplicate logs every watchdog tick for the same skip reason.
        if (string.Equals(state.LastWatchdogSkipReason, reason, StringComparison.OrdinalIgnoreCase) &&
            state.LastWatchdogSkipAt.HasValue &&
            (now - state.LastWatchdogSkipAt.Value) < TimeSpan.FromSeconds(10))
        {
            return false;
        }

        state.LastWatchdogSkipReason = reason;
        state.LastWatchdogSkipAt = now;
        return true;
    }
}

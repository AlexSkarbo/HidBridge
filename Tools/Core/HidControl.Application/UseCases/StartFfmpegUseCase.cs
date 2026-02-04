using HidControl.Application.Abstractions;
using HidControl.Application.Models;
using HidControl.Contracts;

namespace HidControl.Application.UseCases;

/// <summary>
/// Starts FFmpeg processes for enabled video sources.
/// </summary>
public sealed class StartFfmpegUseCase
{
    private readonly IVideoSourceStore _store;
    private readonly IVideoRuntimeControl _runtime;
    private readonly IVideoOutputStateProvider _outputProvider;
    private readonly IVideoFfmpegStarter _ffmpegStarter;
    private readonly IVideoOrphanKiller _orphanKiller;
    private readonly IVideoFfmpegOptions _options;

    /// <summary>
    /// Executes StartFfmpegUseCase.
    /// </summary>
    /// <param name="store">Source store.</param>
    /// <param name="runtime">Runtime control.</param>
    /// <param name="outputProvider">Output state provider.</param>
    /// <param name="ffmpegStarter">FFmpeg starter.</param>
    /// <param name="orphanKiller">Orphan killer.</param>
    /// <param name="options">Options.</param>
    public StartFfmpegUseCase(
        IVideoSourceStore store,
        IVideoRuntimeControl runtime,
        IVideoOutputStateProvider outputProvider,
        IVideoFfmpegStarter ffmpegStarter,
        IVideoOrphanKiller orphanKiller,
        IVideoFfmpegOptions options)
    {
        _store = store;
        _runtime = runtime;
        _outputProvider = outputProvider;
        _ffmpegStarter = ffmpegStarter;
        _orphanKiller = orphanKiller;
        _options = options;
    }

    /// <summary>
    /// Executes the use case.
    /// </summary>
    /// <param name="request">Start request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Execution result.</returns>
    public async Task<FfmpegStartExecutionResult> ExecuteAsync(FfmpegStartRequest? request, CancellationToken cancellationToken)
    {
        string? targetId = request?.SourceId;
        bool restart = request?.Restart ?? false;
        bool manualStart = request?.ManualStart ?? false;
        List<VideoSourceConfig> sources = _store.GetAll().Where(s => s.Enabled).ToList();
        if (!string.IsNullOrWhiteSpace(targetId))
        {
            sources = sources.Where(s => string.Equals(s.Id, targetId, StringComparison.OrdinalIgnoreCase)).ToList();
        }
        if (sources.Count == 0)
        {
            return new FfmpegStartExecutionResult(false, Array.Empty<object>(), Array.Empty<string>(), Array.Empty<OrphanKillInfo>(), Array.Empty<string>(), "no sources", null);
        }
        if (!manualStart)
        {
            var blocked = sources.Where(s => _runtime.IsManualStop(s.Id)).Select(s => s.Id).ToList();
            if (blocked.Count > 0)
            {
                return new FfmpegStartExecutionResult(false, Array.Empty<object>(), Array.Empty<string>(), Array.Empty<OrphanKillInfo>(), Array.Empty<string>(), "manual_stop", blocked);
            }
        }

        IReadOnlyList<string> stoppedWorkers = await _runtime.StopCaptureWorkersAsync(targetId, cancellationToken);
        List<OrphanKillInfo> killedOrphans = new();
        if (_options.KillOrphansEnabled)
        {
            foreach (var src in sources)
            {
                IReadOnlyList<int> killed = _orphanKiller.KillOrphans(src);
                if (killed.Count > 0)
                {
                    killedOrphans.Add(new OrphanKillInfo(src.Id, killed));
                }
            }
        }

        if (manualStart)
        {
            foreach (var src in sources)
            {
                _runtime.SetManualStop(src.Id, false);
            }
        }

        var outputState = _outputProvider.Get();
        var result = _ffmpegStarter.StartForSources(sources, outputState, restart, force: false);
        return new FfmpegStartExecutionResult(
            result.Errors.Count == 0,
            result.Started,
            result.Errors,
            killedOrphans,
            stoppedWorkers,
            null,
            null);
    }
}

using HidControl.Application.Abstractions;
using HidControl.Application.Models;
using HidControl.Contracts;
using System.Linq;
using AppFfmpegStartResult = HidControl.Application.Models.FfmpegStartResult;
using System.Collections.Generic;

namespace HidControl.Application.UseCases;

/// <summary>
/// Applies video output settings and (optionally) restarts streaming when needed.
/// </summary>
public sealed class ApplyVideoOutputUseCase
{
    private readonly IVideoOutputApplier _output;
    private readonly IVideoSourceStore _sources;
    private readonly IVideoRuntimeControl _runtime;
    private readonly IVideoProcessTracker _processTracker;
    private readonly IVideoFfmpegStarter _ffmpegStarter;

    /// <summary>
    /// Executes ApplyVideoOutputUseCase.
    /// </summary>
    /// <param name="output">Output applier.</param>
    /// <param name="sources">Source store.</param>
    /// <param name="runtime">Runtime control.</param>
    /// <param name="processTracker">Process tracker.</param>
    /// <param name="ffmpegStarter">FFmpeg starter.</param>
    public ApplyVideoOutputUseCase(
        IVideoOutputApplier output,
        IVideoSourceStore sources,
        IVideoRuntimeControl runtime,
        IVideoProcessTracker processTracker,
        IVideoFfmpegStarter ffmpegStarter)
    {
        _output = output;
        _sources = sources;
        _runtime = runtime;
        _processTracker = processTracker;
        _ffmpegStarter = ffmpegStarter;
    }

    /// <summary>
    /// Executes the use case.
    /// </summary>
    /// <param name="req">Output request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Apply result.</returns>
    public async Task<VideoOutputApplyResult> ExecuteAsync(VideoOutputRequest req, CancellationToken cancellationToken)
    {
        bool anyRunningBefore = _processTracker.AnyRunning();

        if (!_output.TryApply(req, out var state, out var error))
        {
            return new VideoOutputApplyResult(
                Ok: false,
                State: state,
                Error: error ?? "invalid_request",
                Mode: VideoOutputService.ResolveMode(state),
                AnyRunningBefore: anyRunningBefore,
                SourcesEligible: 0,
                ManualStops: 0,
                StoppedWorkers: Array.Empty<string>(),
                StoppedFfmpeg: Array.Empty<string>(),
                StartResult: new AppFfmpegStartResult(Array.Empty<object>(), Array.Empty<string>()));
        }

        IReadOnlyList<string> stoppedWorkers = await _runtime.StopCaptureWorkersAsync(null, cancellationToken);
        var stoppedFfmpeg = new List<string>();
        foreach (string id in _processTracker.GetTrackedIds())
        {
            if (_runtime.StopFfmpegProcess(id))
            {
                stoppedFfmpeg.Add(id);
            }
        }

        var allSources = _sources.GetAll();
        int manualStops = allSources.Count(s => _runtime.IsManualStop(s.Id));
        var eligible = allSources.Where(s => s.Enabled && !_runtime.IsManualStop(s.Id)).ToList();

        AppFfmpegStartResult start = new(Array.Empty<object>(), Array.Empty<string>());
        if (anyRunningBefore && eligible.Count > 0)
        {
            start = _ffmpegStarter.StartForSources(eligible, state, restart: true, force: true);
        }

        return new VideoOutputApplyResult(
            Ok: true,
            State: state,
            Error: null,
            Mode: VideoOutputModes.ResolveMode(state),
            AnyRunningBefore: anyRunningBefore,
            SourcesEligible: eligible.Count,
            ManualStops: manualStops,
            StoppedWorkers: stoppedWorkers,
            StoppedFfmpeg: stoppedFfmpeg,
            StartResult: start);
    }
}

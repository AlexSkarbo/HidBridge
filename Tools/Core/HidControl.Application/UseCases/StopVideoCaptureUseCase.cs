using HidControl.Application.Abstractions;
using HidControl.Application.Models;
using System.Linq;

namespace HidControl.Application.UseCases;

/// <summary>
/// Sets manual stop flags and stops video capture workers.
/// </summary>
public sealed class StopVideoCaptureUseCase
{
    private readonly IVideoSourceStore _sources;
    private readonly IVideoRuntimeControl _runtime;

    /// <summary>
    /// Creates a new instance.
    /// </summary>
    public StopVideoCaptureUseCase(IVideoSourceStore sources, IVideoRuntimeControl runtime)
    {
        _sources = sources;
        _runtime = runtime;
    }

    /// <summary>
    /// Executes the use case.
    /// </summary>
    /// <param name="id">Source id or null for all enabled sources.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<VideoCaptureStopResult> ExecuteAsync(string? id, CancellationToken ct)
    {
        var enabled = _sources.GetAll().Where(s => s.Enabled).ToList();
        if (string.IsNullOrWhiteSpace(id))
        {
            foreach (var src in enabled)
            {
                _runtime.SetManualStop(src.Id, true);
            }
        }
        else
        {
            foreach (var src in enabled.Where(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase)))
            {
                _runtime.SetManualStop(src.Id, true);
            }
        }

        IReadOnlyList<string> stopped = await _runtime.StopCaptureWorkersAsync(id, ct);
        return new VideoCaptureStopResult(true, null, stopped);
    }
}


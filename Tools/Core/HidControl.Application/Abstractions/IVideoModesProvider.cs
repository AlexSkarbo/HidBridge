using HidControl.Application.Models;

namespace HidControl.Application.Abstractions;

/// <summary>
/// Abstraction for probing available capture modes for a video source.
/// </summary>
public interface IVideoModesProvider
{
    /// <summary>
    /// Probes modes for a source id.
    /// </summary>
    /// <param name="id">Source id.</param>
    /// <param name="refresh">True to bypass cache.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<VideoModesQueryResult> QueryAsync(string id, bool refresh, CancellationToken ct);
}


using HidControl.Application.Abstractions;
using HidControl.Application.Models;

namespace HidControl.Application.UseCases;

/// <summary>
/// Returns available capture modes for a video source.
/// </summary>
public sealed class GetVideoModesUseCase
{
    private readonly IVideoModesProvider _modes;

    /// <summary>
    /// Creates a new instance.
    /// </summary>
    public GetVideoModesUseCase(IVideoModesProvider modes)
    {
        _modes = modes;
    }

    /// <summary>
    /// Executes the use case.
    /// </summary>
    /// <param name="id">Source id.</param>
    /// <param name="refresh">True to bypass cache.</param>
    /// <param name="ct">Cancellation token.</param>
    public Task<VideoModesQueryResult> ExecuteAsync(string? id, bool refresh, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return Task.FromResult(new VideoModesQueryResult(false, "missing_id", false, null, null, Array.Empty<HidControl.Contracts.VideoMode>(), false));
        }

        return _modes.QueryAsync(id, refresh, ct);
    }
}


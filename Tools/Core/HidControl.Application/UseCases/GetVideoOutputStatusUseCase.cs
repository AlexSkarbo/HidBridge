using HidControl.Application.Abstractions;
using HidControl.Application.Models;
using HidControl.Contracts;

namespace HidControl.Application.UseCases;

/// <summary>
/// Returns current video output state and a resolved mode.
/// </summary>
public sealed class GetVideoOutputStatusUseCase
{
    private readonly IVideoOutputStateProvider _state;

    /// <summary>
    /// Creates a new instance.
    /// </summary>
    public GetVideoOutputStatusUseCase(IVideoOutputStateProvider state)
    {
        _state = state;
    }

    /// <summary>
    /// Executes the use case.
    /// </summary>
    public VideoOutputStatusResult Execute()
    {
        VideoOutputState state = _state.Get();
        string mode = VideoOutputModes.ResolveMode(state);
        return new VideoOutputStatusResult(true, null, state, mode);
    }
}


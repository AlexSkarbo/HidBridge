using HidControl.Contracts;

namespace HidControl.Application.Abstractions;

/// <summary>
/// Abstraction for validating and applying video output requests.
/// </summary>
public interface IVideoOutputApplier
{
    /// <summary>
    /// Validates and applies a video output request.
    /// </summary>
    /// <param name="req">Request.</param>
    /// <param name="next">Applied state (or current state on validation failure).</param>
    /// <param name="error">Validation error.</param>
    /// <returns>True when applied.</returns>
    bool TryApply(VideoOutputRequest req, out VideoOutputState next, out string? error);
}

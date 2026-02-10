using HidControl.Application.Abstractions;

namespace HidControl.Application.UseCases.WebRtc;

/// <summary>
/// Use-case for validating whether signaling message forwarding is allowed.
/// </summary>
public sealed class ValidateWebRtcSignalUseCase
{
    private readonly IWebRtcSignalingService _signaling;

    /// <summary>
    /// Initializes a new instance.
    /// </summary>
    /// <param name="signaling">Signaling service.</param>
    public ValidateWebRtcSignalUseCase(IWebRtcSignalingService signaling)
    {
        _signaling = signaling;
    }

    /// <summary>
    /// Executes validation for signal forwarding from a client in a room.
    /// </summary>
    /// <param name="room">Room id.</param>
    /// <param name="clientId">Client id.</param>
    /// <returns>Validation result.</returns>
    public ValidateWebRtcSignalResult Execute(string? room, string clientId)
    {
        if (string.IsNullOrWhiteSpace(room) || !_signaling.IsJoined(room, clientId))
        {
            return new ValidateWebRtcSignalResult(false, "not_joined");
        }

        return new ValidateWebRtcSignalResult(true, null);
    }
}

/// <summary>
/// Validation result for signaling forward operation.
/// </summary>
/// <param name="Ok">True when allowed.</param>
/// <param name="Error">Error code when denied.</param>
public sealed record ValidateWebRtcSignalResult(bool Ok, string? Error);

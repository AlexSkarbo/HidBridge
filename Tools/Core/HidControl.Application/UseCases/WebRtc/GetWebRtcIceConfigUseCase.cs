using HidControl.Application.Abstractions;

namespace HidControl.Application.UseCases.WebRtc;

/// <summary>
/// Use-case for retrieving WebRTC ICE servers (STUN/TURN).
/// </summary>
public sealed class GetWebRtcIceConfigUseCase
{
    private readonly IWebRtcIceService _svc;

    /// <summary>
    /// Initializes a new instance.
    /// </summary>
    /// <param name="svc">Service.</param>
    public GetWebRtcIceConfigUseCase(IWebRtcIceService svc)
    {
        _svc = svc;
    }

    /// <summary>
    /// Executes the use-case.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>ICE config.</returns>
    public Task<WebRtcIceConfig> Execute(CancellationToken ct) => _svc.GetIceConfigAsync(ct);
}


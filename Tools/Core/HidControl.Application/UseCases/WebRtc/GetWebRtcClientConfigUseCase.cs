using HidControl.Application.Abstractions;

namespace HidControl.Application.UseCases.WebRtc;

/// <summary>
/// Use-case for retrieving WebRTC UI/client configuration.
/// </summary>
public sealed class GetWebRtcClientConfigUseCase
{
    private readonly IWebRtcConfigService _svc;

    /// <summary>
    /// Initializes a new instance.
    /// </summary>
    /// <param name="svc">Service.</param>
    public GetWebRtcClientConfigUseCase(IWebRtcConfigService svc)
    {
        _svc = svc;
    }

    /// <summary>
    /// Executes the use-case.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Config.</returns>
    public Task<WebRtcClientConfig> Execute(CancellationToken ct) => _svc.GetClientConfigAsync(ct);
}


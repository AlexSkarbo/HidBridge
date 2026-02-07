using HidControl.Application.Abstractions;

namespace HidControl.Infrastructure.Services;

/// <summary>
/// Default implementation of WebRTC UI/client configuration service.
/// </summary>
public sealed class WebRtcConfigService : IWebRtcConfigService
{
    private readonly IWebRtcBackend _backend;

    /// <summary>
    /// Creates an instance.
    /// </summary>
    /// <param name="backend">Backend.</param>
    public WebRtcConfigService(IWebRtcBackend backend)
    {
        _backend = backend;
    }

    /// <inheritdoc />
    public Task<WebRtcClientConfig> GetClientConfigAsync(CancellationToken ct)
    {
        _ = ct;
        int joinTimeoutMs = Math.Clamp(_backend.ClientJoinTimeoutMs, 250, 60_000);
        int connectTimeoutMs = Math.Clamp(_backend.ClientConnectTimeoutMs, 250, 120_000);

        return Task.FromResult(new WebRtcClientConfig(
            joinTimeoutMs,
            connectTimeoutMs,
            _backend.RoomsCleanupIntervalSeconds,
            _backend.RoomIdleStopSeconds,
            _backend.RoomsMaxHelpers));
    }
}


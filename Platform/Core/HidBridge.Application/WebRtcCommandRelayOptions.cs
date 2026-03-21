namespace HidBridge.Application;

/// <summary>
/// Configures transient WebRTC relay peer-presence behavior.
/// </summary>
public sealed class WebRtcCommandRelayOptions
{
    /// <summary>
    /// Gets or sets the number of seconds after which an online peer snapshot is treated as stale.
    /// </summary>
    public int PeerStaleAfterSec { get; set; } = 15;

    /// <summary>
    /// Gets normalized stale threshold as a <see cref="TimeSpan"/>.
    /// </summary>
    public TimeSpan PeerStaleAfter => TimeSpan.FromSeconds(Math.Clamp(PeerStaleAfterSec, 1, 3600));
}

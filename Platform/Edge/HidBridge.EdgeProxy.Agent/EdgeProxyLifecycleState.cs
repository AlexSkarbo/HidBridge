namespace HidBridge.EdgeProxy.Agent;

/// <summary>
/// Defines canonical edge relay lifecycle states published into peer metadata.
/// </summary>
public enum EdgeProxyLifecycleState
{
    Starting,
    Connecting,
    Connected,
    Degraded,
    Reconnecting,
    Offline,
}
